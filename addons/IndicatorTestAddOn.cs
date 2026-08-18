// IndicatorTestAddOn.cs - Small research AddOn for probing headless indicator hosting in NT8.
// Listens on http://localhost:7892/ so it does not conflict with McpBridgeAddOn on :7890.
// Compile in NT8: File -> Utilities -> NinjaScript Editor -> right-click -> Compile (F5)
//
// Endpoints:
//   GET /api/health                       - confirm addon loaded
//   GET /api/bars?symbol=...             - fetch bars via BarsRequest
//   GET /api/indicator/reflect?name=...  - find indicator type in loaded assemblies
//   GET /api/indicator/try-host?name=... - try to instantiate and drive a NinjaScript indicator
        //   GET /api/indicator/builtin?symbol=...&indicatorName=SMA|EMA|RSI|ATR|VWAP - compute built-ins directly from bars

#region Using declarations
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class IndicatorTestAddOn : AddOnBase
    {
        private const string Version = "0.1.0-research";
        private HttpListener _listener;
        private Thread _serverThread;
        private bool _running;

        protected override void OnStateChange()
        {
            if (State == State.Active)
            {
                StartServer();
            }
            else if (State == State.Terminated)
            {
                StopServer();
            }
        }

        private void StartServer()
        {
            if (_running) return;
            _running = true;
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:7892/");
            _listener.Start();
            _serverThread = new Thread(HandleRequests) { IsBackground = true };
            _serverThread.Start();
            Log($"IndicatorTestAddOn v{Version} started on http://localhost:7892/");
        }

        private void StopServer()
        {
            if (!_running) return;
            _running = false;
            _listener?.Stop();
            _listener?.Close();
            Log("IndicatorTestAddOn stopped");
        }

        private void HandleRequests()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Log($"Listener error: {ex.Message}"); }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            bool responseSent = false;
            try
            {
                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var query = context.Request.QueryString;
                object response;

                switch (path)
                {
                    case "/api/health":
                        response = new { status = "ok", version = Version, timestamp = DateTime.UtcNow };
                        break;

                    case "/api/bars":
                        response = GetBars(query["symbol"], query["period"], query["periodValue"], query["count"]);
                        break;

                    case "/api/indicator/reflect":
                        response = ReflectIndicator(query["name"]);
                        break;

                    case "/api/indicator/try-host":
                        response = TryHostIndicator(query["symbol"], query["name"], query["period"], query["barsBack"]);
                        break;

                    case "/api/indicator/builtin":
                        response = GetBuiltinIndicatorValues(query["symbol"], query["indicatorName"], query["period"], query["barsBack"]);
                        break;

                    default:
                        response = new { error = $"unknown path: {path}" };
                        break;
                }

                responseSent = true;
                WriteResponse(context, 200, response);
            }
            catch (Exception ex)
            {
                if (!responseSent)
                    WriteResponse(context, 500, new { error = ex.Message, stack = ex.StackTrace });
            }
        }

        private void WriteResponse(HttpListenerContext context, int statusCode, object body)
        {
            var json = JsonConvert.SerializeObject(body, Formatting.Indented);
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 1) BarsRequest fetcher
        // ─────────────────────────────────────────────────────────────────────────
        private object GetBars(string symbol, string period, string periodValueStr, string countStr)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return new { error = "symbol required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            BarsPeriodType bpType;
            if (!Enum.TryParse(period ?? "Minute", true, out bpType)) bpType = BarsPeriodType.Minute;
            int periodValue = int.TryParse(periodValueStr, out int pv) ? Math.Max(1, pv) : 1;
            int count = int.TryParse(countStr, out int c) ? Math.Max(1, Math.Min(5000, c)) : 100;

            var barsPeriod = new BarsPeriod { BarsPeriodType = bpType, Value = periodValue };
            Bars bars = null;
            string status = null;
            var done = new ManualResetEventSlim(false);
            // Match the main bridge /api/bars pattern exactly:
            //   * run on the calling thread
            //   * request exactly count bars (offset=0)
            //   * use the caller-requested period/value
            //   * wait INSIDE the using block so the Bars object is still valid when read
            var resultBars = new List<object>();
            using (var request = new BarsRequest(instrument, count) { BarsPeriod = barsPeriod })
            {
                request.Request((req, code, msg) =>
                {
                    status = $"{code} | {msg}";
                    bars = req.Bars;
                    Log($"BarsRequest callback: code={code}, msg={msg}, bars={(bars == null ? "null" : bars.Count.ToString())}");
                    if (bars != null && bars.Count > 0)
                    {
                        int n = bars.Count;
                        int take = Math.Min(count, n);
                        for (int i = 0; i < take; i++)
                        {
                            int idx = n - take + i; // oldest of the requested window first
                            resultBars.Add(new
                            {
                                time = bars.GetTime(idx).ToUniversalTime(),
                                open = bars.GetOpen(idx),
                                high = bars.GetHigh(idx),
                                low = bars.GetLow(idx),
                                close = bars.GetClose(idx),
                                volume = bars.GetVolume(idx),
                            });
                        }
                    }
                    done.Set();
                });
                if (!done.Wait(TimeSpan.FromSeconds(30))) status = "timeout";
            }

            if (status == "timeout") return new { error = "bars request timed out" };
            if (resultBars.Count == 0) return new { error = $"no bar data (status={status})" };
            return new { symbol, period = bpType.ToString(), periodValue, count = resultBars.Count, status, bars = resultBars };
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 2) Indicator reflection: find the type in loaded assemblies
        // ─────────────────────────────────────────────────────────────────────────
        private object ReflectIndicator(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new { error = "name required" };

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var candidates = new List<object>();
            foreach (var asm in assemblies)
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            var baseType = t.BaseType?.FullName;
                            var isIndicator = baseType != null && (baseType.Contains("IndicatorBase") || baseType.Contains(".Indicators.Indicator"));
                            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .Select(m => m.Name)
                                .Distinct()
                                .OrderBy(m => m)
                                .ToList();
                            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .Select(p => new { p.Name, Type = p.PropertyType.Name })
                                .ToList();
                            candidates.Add(new { assembly = asm.GetName().Name, type = t.FullName, baseType, isIndicator, methods, properties = props });
                        }
                    }
                }
                catch { }
            }

            return new { indicatorName = name, candidateCount = candidates.Count, candidates };
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 3) Try to instantiate and drive a NinjaScript indicator headlessly
        // ─────────────────────────────────────────────────────────────────────────
        private object TryHostIndicator(string symbol, string name, string periodStr, string barsBackStr)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(name))
                return new { error = "symbol and name required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            int period = int.TryParse(periodStr, out int p) ? Math.Max(1, p) : 14;
            int barsBack = int.TryParse(barsBackStr, out int bb) ? Math.Max(1, bb) : 20;

            // First fetch bars to host the indicator on.
            var barsResult = GetBars(symbol, "Minute", "1", (barsBack + Math.Max(period * 4, 400)).ToString()) as dynamic;
            var err = GetDynamicError(barsResult);
            if (err != null) return new { error = err };

            // Find indicator type
            Type indicatorType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    indicatorType = asm.GetTypes().FirstOrDefault(t =>
                        t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (indicatorType != null) break;
                }
                catch { }
            }

            if (indicatorType == null)
                return new { error = $"indicator type '{name}' not found in loaded assemblies" };

            var log = new List<string>();
            object instance = null;
            string failure = null;

            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null) return new { error = "no WPF dispatcher" };

                disp.Invoke((Action)(() =>
                {
                    try
                    {
                        log.Add("Creating instance via Activator.CreateInstance");
                        instance = Activator.CreateInstance(indicatorType);
                        log.Add($"Instance created: {instance.GetType().FullName}");

                        // Try to find SetState / Initialize / OnBarUpdate / Values / AddDataSeries
                        var setState = indicatorType.GetMethod("SetState", BindingFlags.Public | BindingFlags.Instance);
                        var init = indicatorType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance);
                        var onBarUpdate = indicatorType.GetMethod("OnBarUpdate", BindingFlags.Public | BindingFlags.Instance);
                        var valuesProp = indicatorType.GetProperty("Values", BindingFlags.Public | BindingFlags.Instance);

                        log.Add($"SetState found: {setState != null}");
                        log.Add($"Initialize found: {init != null}");
                        log.Add($"OnBarUpdate found: {onBarUpdate != null}");
                        log.Add($"Values found: {valuesProp != null}");

                        if (setState != null)
                        {
                            try
                            {
                                var stateEnum = Type.GetType("NinjaTrader.NinjaScript.State, NinjaTrader.Core");
                                if (stateEnum != null && Enum.IsDefined(stateEnum, "Configure"))
                                {
                                    var configure = Enum.Parse(stateEnum, "Configure");
                                    setState.Invoke(instance, new[] { configure });
                                    log.Add("SetState(Configure) invoked");
                                }
                            }
                            catch (Exception ex) { log.Add($"SetState(Configure) failed: {ex.Message}"); }
                        }

                        if (init != null)
                        {
                            try
                            {
                                init.Invoke(instance, new object[0]);
                                log.Add("Initialize() invoked");
                            }
                            catch (Exception ex) { log.Add($"Initialize() failed: {ex.Message}"); }
                        }

                        if (onBarUpdate != null)
                        {
                            try
                            {
                                onBarUpdate.Invoke(instance, new object[0]);
                                log.Add("OnBarUpdate() invoked once");
                            }
                            catch (Exception ex) { log.Add($"OnBarUpdate() failed: {ex.Message}"); }
                        }

                        if (valuesProp != null)
                        {
                            try
                            {
                                var values = valuesProp.GetValue(instance);
                                log.Add($"Values type: {values?.GetType().FullName ?? "null"}");
                            }
                            catch (Exception ex) { log.Add($"Values read failed: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex.Message;
                        log.Add($"Exception during hosting: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                log.Add($"Dispatcher invoke failed: {ex.Message}");
            }

            return new
            {
                symbol,
                indicatorName = name,
                indicatorType = indicatorType.FullName,
                success = failure == null && instance != null,
                failure,
                log
            };
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 4) Built-in indicator values computed directly from bars (fallback path)
        // ─────────────────────────────────────────────────────────────────────────
        private object GetBuiltinIndicatorValues(string symbol, string indicatorName, string periodStr, string barsBackStr)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(indicatorName))
                return new { error = "symbol and indicatorName required" };

            int period = int.TryParse(periodStr, out int p) ? Math.Max(1, p) : 14;
            int barsBack = int.TryParse(barsBackStr, out int bb) ? Math.Max(1, bb) : 20;

            var barsResult = GetBars(symbol, "Minute", "1", (barsBack + Math.Max(period * 4, 400)).ToString()) as dynamic;
            var err = GetDynamicError(barsResult);
            if (err != null) return new { error = err };

            // Compute directly from the bar list returned by GetBars.
            // GetBars returns oldest-first, which is what the math helpers expect.
            var barList = ((IEnumerable<object>)barsResult.bars).ToList();
            var closes = barList.Select(b => (double)((dynamic)b).close).ToArray();
            var highs = barList.Select(b => (double)((dynamic)b).high).ToArray();
            var lows = barList.Select(b => (double)((dynamic)b).low).ToArray();

            List<double> values = null;
            switch (indicatorName.Trim().ToUpperInvariant())
            {
                case "SMA": values = ComputeSma(closes, period, barsBack); break;
                case "EMA": values = ComputeEma(closes, period, barsBack); break;
                case "RSI": values = ComputeRsi(closes, period, barsBack); break;
                case "ATR": values = ComputeAtr(highs, lows, closes, period, barsBack); break;
                case "VWAP": values = ComputeVwap(barList, period, barsBack); break;
                default: return new { error = $"unsupported built-in: {indicatorName}. Use SMA, EMA, RSI, ATR, VWAP." };
            }

            return new { symbol, indicatorName, period, count = values.Count, values };
        }

        private static List<double> ComputeSma(double[] c, int period, int barsBack)
        {
            int n = c.Length;
            var s = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += c[i];
                if (i >= period) sum -= c[i - period];
                s[i] = i >= period - 1 ? sum / period : double.NaN;
            }
            return TakeLast(s, barsBack);
        }

        private static List<double> ComputeEma(double[] c, int period, int barsBack)
        {
            int n = c.Length;
            var e = new double[n];
            double mult = 2.0 / (period + 1);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (i < period - 1) { sum += c[i]; e[i] = double.NaN; }
                else if (i == period - 1) { sum += c[i]; e[i] = sum / period; }
                else e[i] = c[i] * mult + e[i - 1] * (1 - mult);
            }
            return TakeLast(e, barsBack);
        }

        private static List<double> ComputeRsi(double[] c, int period, int barsBack)
        {
            int n = c.Length;
            var rsi = new double[n];
            double avgGain = 0, avgLoss = 0;
            for (int i = 1; i < n; i++)
            {
                double change = c[i] - c[i - 1];
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? -change : 0;
                if (i < period) { avgGain += gain / period; avgLoss += loss / period; rsi[i] = double.NaN; }
                else if (i == period) { avgGain += gain / period; avgLoss += loss / period; rsi[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + avgGain / avgLoss)); }
                else { avgGain = (avgGain * (period - 1) + gain) / period; avgLoss = (avgLoss * (period - 1) + loss) / period; rsi[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + avgGain / avgLoss)); }
            }
            rsi[0] = double.NaN;
            return TakeLast(rsi, barsBack);
        }

        private static List<double> ComputeAtr(double[] h, double[] l, double[] c, int period, int barsBack)
        {
            int n = c.Length;
            var tr = new double[n];
            for (int i = 1; i < n; i++)
                tr[i] = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
            var atr = new double[n];
            double sum = 0;
            for (int i = 1; i < n; i++)
            {
                sum += tr[i];
                if (i > period) sum -= tr[i - period];
                atr[i] = i >= period ? sum / period : double.NaN;
            }
            return TakeLast(atr, barsBack);
        }

        private static List<double> TakeLast(double[] series, int barsBack)
        {
            var list = new List<double>(barsBack);
            int n = series.Length;
            int take = Math.Min(barsBack, n);
            for (int i = 0; i < take; i++)
            {
                double v = series[n - 1 - i];
                if (!double.IsNaN(v)) list.Add(Math.Round(v, 4));
            }
            return list;
        }

        private static List<double> ComputeVwap(List<object> bars, int period, int barsBack)
        {
            // Intraday anchored VWAP: reset cumulative sums at each new session day.
            // Assumes bar time is UTC; anchor uses Date portion.
            var list = new List<double>();
            double cumTpVol = 0;
            double cumVol = 0;
            DateTime anchorDate = DateTime.MinValue;
            int n = bars.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic b = bars[i];
                DateTime t = (DateTime)b.time;
                double high = (double)b.high;
                double low = (double)b.low;
                double close = (double)b.close;
                long volume = (long)b.volume;
                DateTime day = t.Date;
                if (day != anchorDate)
                {
                    anchorDate = day;
                    cumTpVol = 0;
                    cumVol = 0;
                }
                double tp = (high + low + close) / 3.0;
                double vol = volume;
                cumTpVol += tp * vol;
                cumVol += vol;
                double vwap = cumVol > 0 ? cumTpVol / cumVol : double.NaN;
                list.Add(vwap);
            }
            var result = new List<double>(barsBack);
            int take = Math.Min(barsBack, list.Count);
            for (int i = 0; i < take; i++)
            {
                double v = list[n - 1 - i];
                if (!double.IsNaN(v)) result.Add(Math.Round(v, 4));
            }
            return result;
        }

        private static readonly object _logLock = new object();
        private static string LogFile => Path.Combine(Globals.UserDataDir, "IndicatorTestAddOn.log");

        private static string GetDynamicError(object result)
        {
            if (result == null) return null;
            try
            {
                var d = (dynamic)result;
                string err = d.error;
                return err;
            }
            catch { return null; }
        }

        private void Log(string message)
        {
            try
            {
                string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [IndicatorTest] {message}";
                NinjaTrader.Code.Output.Process(line, PrintTo.OutputTab1);
                lock (_logLock)
                {
                    File.AppendAllText(LogFile, line + "\n");
                }
            }
            catch { }
        }
    }
}
