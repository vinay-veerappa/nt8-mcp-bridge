// McpBridgeAddOn.cs - NinjaTrader 8 AddOn, HTTP API on port 7890
// Compile in NT8: File - Utilities - NinjaScript Editor - right-click - Compile (F5)
// Or: copy to Documents\NinjaTrader 8\bin\Custom\AddOns\ and compile via NinjaScript Editor.
//
// v0.2.0 - Phase 2: strategy authoring, in-process compile, Strategy Analyzer backtest.
//   New endpoints:
//     GET  /api/strategies              list NinjaScript strategy source files
//     GET  /api/strategy/source?name=   read one strategy's source
//     POST /api/strategy/create         write a strategy .cs into bin\Custom\Strategies
//     POST /api/compile                 recompile NinjaScript in-process (hot-swap, no restart)
//     POST /api/backtest                run a backtest via the Strategy Analyzer
//     POST /api/dev/reflect             DEV ONLY - reflection RPC for probing NT8 internals
//                                       (enabled only when env NT8_MCP_DEV=1)

#region Using declarations
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Collections.Generic;
using System.Collections;
using System.Threading;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Core;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class McpBridgeAddOn : AddOnBase
    {
        private const string Version = "1.5.2-chart-discovery";

        // Win32 capture helpers for chart windows that render on non-WPF threads (Direct2D).
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private HttpListener _listener;
        private Thread _serverThread;
        private bool _running;

        private static string ServerToken
        {
            get
            {
                string tok = Environment.GetEnvironmentVariable("NT8_MCP_TOKEN");
                if (string.IsNullOrEmpty(tok))
                {
                    try
                    {
                        string tokenFile = Path.Combine(Globals.UserDataDir, "mcp_token.txt");
                        if (File.Exists(tokenFile)) tok = File.ReadAllText(tokenFile).Trim();
                    }
                    catch {}
                }
                return tok;
            }
        }

        private bool CheckAuth(HttpListenerContext context)
        {
            string requiredToken = ServerToken;
            if (string.IsNullOrEmpty(requiredToken)) return true;

            string authHeader = context.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authHeader.Substring(7).Trim();
                return FixedTimeEquals(token, requiredToken);
            }
            return false;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var bytesA = Encoding.UTF8.GetBytes(a);
            var bytesB = Encoding.UTF8.GetBytes(b);
            if (bytesA.Length != bytesB.Length) return false;

            int diff = 0;
            for (int i = 0; i < bytesA.Length; i++)
            {
                diff |= bytesA[i] ^ bytesB[i];
            }
            return diff == 0;
        }

        // Dev-only reflection RPC: object handle registry so callers can chain calls
        // (e.g. construct a window - invoke methods on it - read results).
        // Gated dynamically (checked per request) on either env NT8_MCP_DEV=1 or the
        // presence of a marker file, so it can be toggled WITHOUT restarting NT8.
        private static string DevMarkerFile => Path.Combine(Globals.UserDataDir, "mcp_dev.on");
        private static bool DevMode =>
            Environment.GetEnvironmentVariable("NT8_MCP_DEV") == "1" || File.Exists(DevMarkerFile);
        private readonly Dictionary<string, object> _handles = new Dictionary<string, object>();
        private int _handleSeq;

        // Idempotency cache (1-hour TTL) for POST endpoints
        private struct IdempotencyRecord
        {
            public DateTime Timestamp;
            public object Result;
        }
        private readonly Dictionary<string, IdempotencyRecord> _idempotencyCache = new Dictionary<string, IdempotencyRecord>();
        private readonly object _idempotencyLock = new object();
        private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(1);

        // Persistent State Stores
        private static readonly Dictionary<string, JObject> _copierConfig = new Dictionary<string, JObject>();
        private static readonly Dictionary<string, JObject> _propLimits = new Dictionary<string, JObject>();
        private static readonly Dictionary<string, JObject> _riskGuardConfig = new Dictionary<string, JObject>();
        private static readonly Dictionary<string, JObject> _scheduledTasks = new Dictionary<string, JObject>();
        private static readonly Dictionary<string, JObject> _tradeJournal = new Dictionary<string, JObject>();
        private static readonly Dictionary<string, JObject> _alerts = new Dictionary<string, JObject>();
        private static readonly List<JObject> _drawnLevels = new List<JObject>();
        private static readonly object _interventionsLock = new object();

        private void LogIntervention(string path, string body, object response)
        {
            try
            {
                string logDir = Path.Combine(Globals.UserDataDir, "RiskGuard");
                Directory.CreateDirectory(logDir);
                string interventionsFile = Path.Combine(logDir, "interventions.jsonl");

                object reqObj = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try { reqObj = JObject.Parse(body); } catch { reqObj = body; }
                }

                var record = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    path,
                    request = reqObj,
                    response
                };

                string line = JsonConvert.SerializeObject(record);
                lock (_interventionsLock)
                {
                    File.AppendAllText(interventionsFile, line + "\n");
                }
            }
            catch (Exception ex)
            {
                Log($"Error writing intervention log: {ex.Message}", LogLevel.Error);
            }
        }


        private object ExecuteIdempotent(string idempotencyKey, Func<object> action)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey)) return action();

            lock (_idempotencyLock)
            {
                var cutoff = DateTime.UtcNow - IdempotencyTtl;
                var expired = _idempotencyCache.Where(kv => kv.Value.Timestamp < cutoff).Select(kv => kv.Key).ToList();
                foreach (var k in expired) _idempotencyCache.Remove(k);

                IdempotencyRecord record;
                if (_idempotencyCache.TryGetValue(idempotencyKey, out record))
                {
                    Log($"[IDEMPOTENCY] Cache hit for key={idempotencyKey}");
                    return record.Result;
                }
            }

            var result = action();

            lock (_idempotencyLock)
            {
                if (result != null)
                {
                    _idempotencyCache[idempotencyKey] = new IdempotencyRecord { Timestamp = DateTime.UtcNow, Result = result };
                }
            }

            return result;
        }

        private object ExecuteIdempotencyFromReq(string body, Func<string, object> action)
        {
            string key = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var jobj = JObject.Parse(body);
                    key = jobj.Str("idempotencyKey");
                }
                catch {}
            }
            return ExecuteIdempotent(key, () => action(body));
        }

        // NT8 AddOns are driven by OnStateChange (there is no OnStartUp/OnShutDown on
        // AddOnBase in NT8.1). Start the HTTP listener once at State.Configure and tear
        // it down at State.Terminated.
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "McpBridgeAddOn";
            }
            else if (State == State.Configure)
            {
                TradeCopierEngine.Instance.LoadFromDisk(CopierConfigFile);
                PropFirmProtectionSuite.Instance.LoadFromDisk(PropLimitsFile);
                // Load persisted stores for schedule, alerts, riskguard config, and trade journal
                LoadJsonStore(ScheduledTasksFile, _scheduledTasks);
                LoadJsonStore(AlertsFile, _alerts);
                LoadJsonStore(RiskGuardConfigFile, _riskGuardConfig);
                LoadJsonStore(TradeJournalFile, _tradeJournal);
#if !TESTING
                // P1-21: this pass used to be the ONLY one, so any account that connected
                // after State.Configure never fed the copier and its relationships were
                // silently dead. Subscribe now, and again on every connection change.
                TradeCopierEngine.Instance.RefreshAccountSubscriptions();
                Connection.ConnectionStatusUpdate -= OnConnectionStatusUpdateForCopier;
                Connection.ConnectionStatusUpdate += OnConnectionStatusUpdateForCopier;
#endif
                StartServer();
            }
            else if (State == State.Terminated)
            {
#if !TESTING
                Connection.ConnectionStatusUpdate -= OnConnectionStatusUpdateForCopier;
                // Handlers left attached outlive the AddOn reload that follows every
                // recompile, and the next engine instance cannot detach them -- the account
                // would then deliver each execution to both engines and copy every fill twice.
                TradeCopierEngine.Instance.UnsubscribeAllAccounts();
#endif
                StopServer();
            }
        }

#if !TESTING
        private void OnConnectionStatusUpdateForCopier(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                int added = TradeCopierEngine.Instance.RefreshAccountSubscriptions();
                if (added > 0)
                {
                    NinjaTrader.Code.Output.Process(
                        string.Format("[McpBridge] Copier subscribed to {0} newly available account(s) after connection change.", added),
                        PrintTo.OutputTab1);
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process(
                    string.Format("[McpBridge] Copier re-subscribe failed: {0}", ex.Message),
                    PrintTo.OutputTab1);
            }
        }
#endif

        private void StartServer()
        {
            if (_running) return;
            _running = true;
            _listener = new HttpListener();

            // Bind address is configurable via the NT8_MCP_PREFIX environment variable.
            // Default: localhost only (safe - same-machine access).
            // For remote access over a PRIVATE network (e.g. Tailscale), set it to
            // "http://+:7890/" so the AddOn also listens on the VPN interface.
            // NEVER expose this on a public interface without auth + firewall.
            var prefix = Environment.GetEnvironmentVariable("NT8_MCP_PREFIX");
            if (string.IsNullOrEmpty(prefix)) prefix = "http://localhost:7890/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _serverThread = new Thread(HandleRequests) { IsBackground = true };
            _serverThread.Start();

            Log($"McpBridgeAddOn v{Version} started on {prefix}" + (DevMode ? " (DEV reflection RPC enabled)" : ""));
        }

        private void StopServer()
        {
            if (!_running) return;
            _running = false;
            // Do NOT close SA windows here - closing pops a blocking confirmation dialog, and on a
            // hot-swap the next addon instance adopts the existing window anyway (FindExistingSaWindow).
            _listener?.Stop();
            _listener?.Close();
            Log("McpBridgeAddOn stopped");
        }

        // Manual cleanup endpoint: report all open windows and close the SA ones.
        private object CloseSaWindows()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher" };
            var all = new List<object>();
            int closed = 0;
            disp.Invoke((Action)(() =>
            {
                var app = System.Windows.Application.Current;
                var wins = new List<System.Windows.Window>();
                foreach (System.Windows.Window w in app.Windows) wins.Add(w);
                foreach (var w in wins)
                {
                    bool sa = IsSaWindow(w);
                    all.Add(new { type = w.GetType().FullName, title = SafeToString(w.Title), isSa = sa });
                    if (sa) { try { w.Close(); closed++; } catch { } }
                }
                _saWindow = null;
            }));
            return new { closed, openWindows = all };
        }

        private void HandleRequests()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    // Dispatch each request to the ThreadPool so a blocking handler
                    // (e.g. 180s backtest, 30s BarsRequest) does not stall all other
                    // endpoints. The previous single-threaded loop serialized every
                    // request — one slow call made the entire API unresponsive.
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Log($"Error: {ex.Message}", LogLevel.Error); }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            bool responseSent = false;
            try
            {
                if (!CheckAuth(context))
                {
                    WriteResponse(context, 401, new { error = "Unauthorized: Invalid or missing Bearer token" });
                    return;
                }

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                var method = context.Request.HttpMethod;

                if (path == "/api/events/stream")
                {
                    HandleSseStream(context);
                    return;
                }

                string body = null;
                if (method == "POST")
                {
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        body = reader.ReadToEnd();
                }

                var response = RouteRequest(path, method, body, context.Request.QueryString);
                if (method == "POST")
                {
                    LogIntervention(path, body, response);
                }
                responseSent = true;
                WriteResponse(context, 200, response);

            }
            catch (Exception ex)
            {
                if (!responseSent)
                {
                    WriteResponse(context, 500, new { error = ex.Message, stack = ex.StackTrace });
                }
            }
        }

        private object RouteRequest(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            switch (path)
            {
                case "/api/health":
                    int accountCount = 0;
                    bool connectedToFeed = false;
                    try
                    {
                        accountCount = Account.All != null ? Account.All.Count : 0;
                        connectedToFeed = accountCount > 0;


                    }
                    catch {}
                    return new
                    {
                        status = "ok",
                        timestamp = DateTime.UtcNow,
                        version = Version,
                        dev = DevMode,
                        accounts = accountCount,
                        feedConnected = connectedToFeed
                    };

                case "/api/dev/reset-risk":
                    return Post(method, () => ResetRiskGuard());

                case "/api/dev/run-firm-tests":
                    return Post(method, () => RunFirmDiagnostics());

                case "/api/dev/inspect-state":
                    return GetRiskGuardState();

                case "/api/dev/reload-state":
                    return Post(method, () => ReloadRiskGuardState());

                // - RiskGuard FSM observation & Version (read-only) -
                case "/api/riskguard/version":
                    // P1-47: report the arm state here too. It was previously visible only on the
                    // dashboard, so a silently disarmed guard was indistinguishable from a working one.
                    return new
                    {
                        success = true,
                        version = RiskGuardAddOn.Version,
                        name = "RiskGuardAddOn",
                        loaded = RiskGuardAddOn.Instance != null,
                        mode = RiskGuardAddOn.Instance != null ? RiskGuardAddOn.Instance.GetMode() : null,
                        isArmed = RiskGuardAddOn.Instance != null && RiskGuardAddOn.Instance.IsArmed,
                        guarding = RiskGuardAddOn.Instance != null && RiskGuardAddOn.Instance.IsArmed
                    };
                case "/api/riskguard/fsm-state":
                    return GetFsmState(query["account"], query["instrument"]);
                case "/api/riskguard/fsm-reset":
                    return Post(method, () => ResetFsmState(query["account"], query["instrument"]));

                // - Phase 1 (account / trading / data) -
                case "/api/account":            return GetAccountInfo();
                case "/api/positions":          return GetPositions();
                case "/api/orders":             return GetOrders();
                case "/api/quote":              return GetQuote(query["symbol"]);
                case "/api/bars":
                    return GetBars(query["symbol"], query["period"] ?? "Minute",
                        int.Parse(query["periodValue"] ?? "1"), int.Parse(query["count"] ?? "100"));
                case "/api/search":             return SearchInstruments(query["query"]);
                case "/api/bars/export":        return Post(method, () => ExportBars(body));
                case "/api/export":             return ReadExportFile(query["name"]);
                case "/api/order":              return Post(method, () => ExecuteIdempotencyFromReq(body, b => PlaceOrder(b)));
                case "/api/order/oco":          return Post(method, () => ExecuteIdempotencyFromReq(body, b => PlaceOcoOrder(b)));
                case "/api/order/atm":          return Post(method, () => ExecuteIdempotencyFromReq(body, b => PlaceAtmOrder(b)));
                case "/api/order/atm/status":   return GetAtmBracketStatus(query["bracketId"]);
                case "/api/order/cancel":       return Post(method, () => ExecuteIdempotencyFromReq(body, b => CancelOrder(b)));
                case "/api/order/change":       return Post(method, () => ExecuteIdempotencyFromReq(body, b => ChangeOrder(b)));
                case "/api/orders/cancel-all":  return Post(method, () => CancelAllOrders());
                case "/api/position/close":     return Post(method, () => ClosePosition(body));
                case "/api/emergency-flatten":  return Post(method, () => ExecuteIdempotencyFromReq(body, b => EmergencyFlatten(b)));
                case "/api/lockout":            return Post(method, () => HandleLockout(body));

                // - Phase 2 & Expansion (strategy authoring / compile / backtest / v1.4 tools) -
                case "/api/strategies":         return ListStrategies();
                case "/api/strategy/source":    return GetStrategySource(query["name"]);
                case "/api/strategy/create":    return Post(method, () => CreateStrategy(body));
                case "/api/compile":            return Post(method, () => Compile(body));
                case "/api/compile/result":     return ReadCompileResult();
                case "/api/backtest":           return Post(method, () => Backtest(body));
                case "/api/strategy/running":   return RunningStrategies();
                case "/api/strategy/deploy":    return Post(method, () => DeployStrategy(body));
                case "/api/strategy/stop":      return Post(method, () => StopStrategy(body));
                case "/api/strategy/param":     return Post(method, () => SetStrategyParam(body));
                case "/api/strategy/inspect":   return InspectStrategy(query["name"]);
                int l;
                case "/api/logs":               return GetDiagnosticLogs(query["tab"] ?? "Output", int.TryParse(query["lines"], out l) ? l : 100);
                case "/api/chart/capture":      return CaptureChart(query["symbol"]);
                case "/api/chart/diag":         return ChartDiagnostics();
                case "/api/chart/list":         return ListCharts();
                case "/api/chart/snapshot":     return Post(method, () => ChartSnapshot(body));
                case "/api/chart/trade":        return Post(method, () => TradeChart(body));
                case "/api/chart/open":         return Post(method, () => OpenChart(body));
                case "/api/chart/draw":         return Post(method, () => DrawChartLevel(body));

                case "/api/events/fills":       return GetFillEvents(query["count"] ?? "50");
                case "/api/copier/config":      return Post(method, () => CopierConfig(body));
                case "/api/prop/limits":        return Post(method, () => PropLimits(body));
                case "/api/trades/extract":     return ExtractTrades(query["account"], query["format"], query["from"], query["to"], query["limit"]);
                case "/api/trades/monte-carlo": return Post(method, () => MonteCarlo(body));
                case "/api/indicator/values":   return GetIndicatorValues(query["symbol"], query["indicatorName"], query["period"], query["barsBack"]);
                case "/api/script/execute":     return Post(method, () => ScriptExecute(body));
                case "/api/backtest/portfolio": return Post(method, () => PortfolioBacktest(body));
                case "/api/data/synthetic":    return Post(method, () => SyntheticData(body));
                case "/api/backtest/signal":   return Post(method, () => SignalBacktest(body));
                case "/api/schedule/task":      return Post(method, () => ScheduleTask(body));
                case "/api/trades/journal":     return Post(method, () => TradeJournal(body));
                case "/api/alert/create":       return Post(method, () => CreateAlert(body));
                case "/api/riskguard/config":   return method == "GET" ? RiskGuardConfig(null) : Post(method, () => RiskGuardConfig(body));
                case "/api/compliance/report":  return GetComplianceReport(query["account"]);
                case "/api/orchestrator/multi-account": return Post(method, () => MultiAccountOrchestrator(body));
                case "/api/sa/close":           return Post(method, () => CloseSaWindows());
                case "/api/sa/inspect":         if (!DevMode) return new { error = "dev only" }; return SaInspect();

                // - Dev-only reflection RPC -
                case "/api/dev/reflect":
                    if (!DevMode) return new { error = "dev mode disabled (set NT8_MCP_DEV=1 and restart NT8)" };
                    return Post(method, () => DevReflect(body));

                default:
                    throw new Exception($"Unknown endpoint: {path}");
            }
        }

        private static object Post(string method, Func<object> fn)
            => method == "POST" ? fn() : new { error = "method not allowed" };

        // -
        //  Strategy authoring (safe - pure file I/O)
        // -

        private static string StrategiesDir =>
            Path.Combine(Globals.UserDataDir, "bin", "Custom", "Strategies");

        // Guard against path traversal - accept a bare class/file name only.
        private static string SafeStrategyPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new Exception("name required");
            name = name.Trim();
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..") || name.Contains("/") || name.Contains("\\"))
                throw new Exception($"invalid strategy name: {name}");
            return Path.Combine(StrategiesDir, name + ".cs");
        }

        private object ListStrategies()
        {
            var dir = StrategiesDir;
            if (!Directory.Exists(dir)) return new { dir, strategies = new List<object>() };
            var list = Directory.GetFiles(dir, "*.cs")
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Select(fi => new { name = Path.GetFileNameWithoutExtension(fi.Name), file = fi.Name, bytes = fi.Length, modified = fi.LastWriteTimeUtc })
                .ToList();
            return new { dir, count = list.Count, strategies = list };
        }

        private object GetStrategySource(string name)
        {
            var path = SafeStrategyPath(name);
            if (!File.Exists(path)) return new { error = $"strategy not found: {name}" };
            return new { name = Path.GetFileNameWithoutExtension(path), file = Path.GetFileName(path), source = File.ReadAllText(path) };
        }

        private object CreateStrategy(string body)
        {
            var req = JObject.Parse(body ?? "{}");
            var name = req.Str("name");
            var source = req.Str("source");
            var overwrite = req.Bool("overwrite", true);
            if (string.IsNullOrWhiteSpace(source)) return new { error = "source required" };

            var path = SafeStrategyPath(name);
            var existed = File.Exists(path);
            if (existed && !overwrite) return new { error = $"strategy exists (pass overwrite=true): {name}" };

            Directory.CreateDirectory(StrategiesDir);
            File.WriteAllText(path, source, new UTF8Encoding(false));
            return new { status = existed ? "updated" : "created", name = Path.GetFileNameWithoutExtension(path), file = path, bytes = source.Length,
                         note = "call /api/compile to build + hot-load this strategy" };
        }

        // -
        //  Compile - invoke NT8's internal Roslyn compiler in-process.
        //  NinjaTrader.Code.Compiler is public but obfuscated; call via
        //  reflection so we don't take a compile-time dep on Microsoft.CodeAnalysis.
        // -

        // A successful compile hot-swaps the NinjaScript AppDomain - the very domain
        // THIS addon runs in - so the addon (and its HttpListener) is torn down and
        // recreated moments after Compiler.Compile() returns. The in-flight HTTP
        // response usually dies with it. So we persist the result to disk immediately
        // and expose GET /api/compile/result as a reliable fallback.
        private static string CompileResultFile => Path.Combine(Globals.UserDataDir, "mcp_compile_result.json");

        private object Compile(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var debug = req.Bool("debug", false);
            return CompileCore(debug);
        }

        private object CompileCore(bool debug)
        {
            var compilerType = Type.GetType("NinjaTrader.Code.Compiler, NinjaTrader.Core");
            if (compilerType == null) return Persist(new { success = false, error = "NinjaTrader.Code.Compiler not found" });

            // EmitResult Compile(bool checkCompileOnly, bool debugBuild,
            //                    IEnumerable<string> filesToIgnore, IEnumerable<string> filesInTmp)
            var compile = compilerType.GetMethod("Compile",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(bool), typeof(bool), typeof(IEnumerable<string>), typeof(IEnumerable<string>) }, null);
            if (compile == null) return Persist(new { success = false, error = "Compiler.Compile(bool,bool,IEnumerable<string>,IEnumerable<string>) not found" });

            object emit = null;
            Exception compileEx = null;

            // Marshal to UI thread — NT8's NinjaScript compiler requires the WPF Dispatcher.
            // Without this, Compiler.Compile() deadlocks/crashes when called from the HTTP listener thread.
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return Persist(new { success = false, error = "no WPF dispatcher (is NT8 UI up?)" });

            disp.Invoke((Action)(() =>
            {
                try
                {
                    // Compile everything from disk: no ignores, no tmp overlay (files already written to Custom\Strategies).
                    emit = compile.Invoke(null, new object[] { false, debug, new List<string>(), new List<string>() });
                }
                catch (Exception ex)
                {
                    compileEx = (ex as TargetInvocationException)?.InnerException ?? ex;
                }
            }));

            if (compileEx != null)
                return Persist(new { success = false, error = "compile threw: " + compileEx.Message, stack = compileEx.StackTrace });

            // EmitResult.Success (bool) + EmitResult.Diagnostics (IEnumerable<Diagnostic>)
            var success = (bool?)emit?.GetType().GetProperty("Success")?.GetValue(emit) ?? false;
            var diagnostics = ReadDiagnostics(emit);
            var assemblyToLoad = compilerType.GetProperty("AssemblyToLoad", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;

            // Persist BEFORE the imminent AppDomain hot-swap tears this addon down.
            return Persist(new
            {
                success,
                errorCount = diagnostics.Count(d => d.severity == "Error"),
                errors = diagnostics.Where(d => d.severity == "Error").ToList(),
                // CS1701/CS1702 are benign assembly-version-unification notices NT8 emits en masse
                // (thousands of them) - filter them out, and hard-cap the rest so the result file
                // can never balloon.
                warnings = diagnostics.Where(d => d.severity == "Warning" && d.id != "CS1701" && d.id != "CS1702").Take(25).ToList(),
                assemblyToLoad,
                note = "NinjaScript hot-swaps right after this; if the connection dropped, GET /api/compile/result",
                timestamp = DateTime.UtcNow,
            });
        }

        private object Persist(object result)
        {
            try { File.WriteAllText(CompileResultFile, JsonConvert.SerializeObject(result), new UTF8Encoding(false)); } catch { }
            return result;
        }

        private object ReadCompileResult()
        {
            if (!File.Exists(CompileResultFile)) return new { error = "no compile has run yet" };
            try { return JObject.Parse(File.ReadAllText(CompileResultFile)); }
            catch (Exception ex) { return new { error = ex.Message }; }
        }

        private class Diag { public string severity; public string id; public string message; public string location; }

        private List<Diag> ReadDiagnostics(object emit)
        {
            var result = new List<Diag>();
            if (emit == null) return result;
            var diags = emit.GetType().GetProperty("Diagnostics")?.GetValue(emit) as IEnumerable;
            if (diags == null) return result;
            foreach (var d in diags)
            {
                if (d == null) continue;
                var t = d.GetType();
                var sev = t.GetProperty("Severity")?.GetValue(d)?.ToString();
                if (sev == "Hidden" || sev == "Info") continue;
                var id = t.GetProperty("Id")?.GetValue(d)?.ToString();
                string loc = null;
                try { loc = t.GetProperty("Location")?.GetValue(d)?.ToString(); } catch { }
                // Diagnostic.ToString() = "file(line,col): error CSxxxx: message" - ideal for reporting.
                result.Add(new Diag { severity = sev, id = id, message = SafeToString(d), location = loc });
            }
            return result;
        }

        // -
        //  Backtest - driven via a bridge-managed Strategy Analyzer window.
        //  Sequence (all on the WPF dispatcher):
        //    create+show(minimized) SA window (cached/reused) - configure the
        //    selected tab (Strategy, Instrument, BarsPeriod, params) -
        //    CheckSettingsValid - ViewModel.OnRun - poll SelectedResult.Results
        //    - extract SystemPerformance (metrics + trade list).
        // -

        private object _saWindow; // reused across backtests
        private static readonly object _saLock = new object(); // serialize backtests (shared SA window)

        private const string SaNs = "NinjaTrader.Gui.NinjaScript.StrategyAnalyzer.";

        private object Backtest(string body)
        {
            // Backtests share a single Strategy Analyzer window (_saWindow).
            // With concurrent request dispatch, two backtests running simultaneously
            // would conflict. Lock here so only one backtest runs at a time; other
            // request types (health, quote, orders, etc.) remain fully concurrent.
            lock (_saLock)
            {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string strategy = req.Str("strategy");
            string symbol = req.Str("symbol");
            string period = req.Str("period") ?? "Minute";
            int periodValue = req["periodValue"] != null ? (int)req["periodValue"] : 1;
            int timeoutSec = req["timeoutSec"] != null ? (int)req["timeoutSec"] : 180;
            int maxTrades = req["maxTrades"] != null ? (int)req["maxTrades"] : 50;
            var prms = req["params"] as JObject;
            DateTime fromDt = DateTime.MinValue, toDt = DateTime.MinValue;
            DateTime.TryParse(req.Str("from"), out fromDt);
            DateTime.TryParse(req.Str("to"), out toDt);
            if (string.IsNullOrEmpty(strategy) || string.IsNullOrEmpty(symbol))
                return new { error = "strategy and symbol are required" };

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher (is NT8 UI up?)" };

            Exception cfgErr = null;
            bool valid = false;
            object tabRef = null;
            object baseline = null;
            object baselineResults = null;  // SystemPerformance ref before run — detects SA reusing the entry object

            disp.Invoke((Action)(() =>
            {
                try
                {
                    // Reuse a single SA window across runs/hot-swaps. Closing NT8 windows pops a
                    // blocking "are you sure?" dialog, so we NEVER close - we adopt any existing
                    // SA window (orphaned by a prior hot-swap) or create one if none exists.
                    _saWindow = FindExistingSaWindow();
                    if (_saWindow == null)
                    {
                        var saType = Type.GetType(SaNs + "StrategyAnalyzer, NinjaTrader.Gui");
                        _saWindow = Activator.CreateInstance(saType);
                        InvokeM(_saWindow, "Show");
                    }
                    try { SetP(_saWindow, "WindowState", System.Windows.WindowState.Minimized); } catch { }
                    var vm = GetP(_saWindow, "ViewModel");
                    var tab = GetP(vm, "SelectedTab");
                    tabRef = tab;
                    var props = GetP(tab, "TabStrategyProperties");

                    SetP(props, "Strategy", strategy);
                    SetP(props, "InstrumentOrInstrumentList", symbol);
                    var bp = GetP(props, "BarsPeriod");
                    var bpType = Type.GetType("NinjaTrader.Data.BarsPeriodType, NinjaTrader.Core");
                    SetP(bp, "BarsPeriodType", Enum.Parse(bpType, period, true));
                    SetP(bp, "Value", periodValue);

                    if (prms != null)
                    {
                        var tmpl = GetP(props, "StrategyTemplate");
                        if (tmpl != null)
                            foreach (var p in prms.Properties())
                            {
                                var pi = tmpl.GetType().GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                                if (pi != null && pi.CanWrite && p.Value is JValue jv && jv.Value != null)
                                    try { pi.SetValue(tmpl, Convert.ChangeType(jv.Value, pi.PropertyType)); } catch { }
                            }
                    }

                    // Custom date range: the SA toolbar From/To are Infragistics XamDateTimeEditor
                    // controls; set their DateValue before running. (No config property exists.)
                    if (fromDt != DateTime.MinValue || toDt != DateTime.MinValue)
                        SetSaDateRange(_saWindow, fromDt, toDt);

                    baseline = GetP(tab, "SelectedResult");
                    // Also capture the baseline Results reference. NT8's SA may REUSE the same
                    // SelectedResult object across runs and just swap its Results (SystemPerformance).
                    // Detecting a new Results reference is more reliable than a new SelectedResult.
                    baselineResults = baseline != null ? GetP(baseline, "Results") : null;
                    valid = Convert.ToBoolean(InvokeM(vm, "CheckSettingsValid"));
                    if (valid) InvokeM(vm, "OnRun", null, null);
                }
                catch (Exception ex) { cfgErr = ex; }
            }));

            if (cfgErr != null) return new { error = "configure/fire failed: " + cfgErr.Message, stack = cfgErr.StackTrace };
            if (!valid) return new { error = "settings invalid - check strategy name, instrument, or that data exists for the range" };

            // Poll for a new completed result. Two signals:
            //   (a) SelectedResult becomes a NEW object reference (classic), OR
            //   (b) SelectedResult.Results becomes a NEW object reference (SA reused the entry
            //       but created a fresh SystemPerformance for this run).
            // Either signal + non-null Results = done. 0-trade runs still produce a SystemPerformance.
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            object entry = null;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                object sel = null, results = null;
                disp.Invoke((Action)(() =>
                {
                    sel = GetP(tabRef, "SelectedResult");
                    if (sel == null) return;
                    bool selChanged = !ReferenceEquals(sel, baseline);
                    results = GetP(sel, "Results");
                    bool resultsChanged = results != null && !ReferenceEquals(results, baselineResults);
                    // Accept if either the entry or the Results object changed AND results is non-null.
                    if (results != null && (selChanged || resultsChanged)) return;
                    results = null;  // not ready yet — keep polling
                }));
                if (results != null) { entry = sel; break; }
            }
            if (entry == null) return new { status = "timeout", message = $"no result within {timeoutSec}s (backtest may still be running)" };

            object report = null;
            // Leave the (minimized) window open for reuse - closing pops a blocking dialog.
            disp.Invoke((Action)(() => { report = ExtractBacktest(entry, maxTrades); }));
            return report;
            } // end lock(_saLock)
        }

        // DEV: walk the SA window's logical tree and report every DateTime-valued property,
        // to locate the toolbar From/To date controls. Also reports control types seen.
        private object ResetRiskGuard()
        {
            try
            {
                if (RiskGuardAddOn.Instance != null)
                {
                    RiskGuardAddOn.Instance.ResetStateForDev();
                    return new { success = true, message = "RiskGuard state reset successfully." };
                }
                return new { error = "RiskGuardAddOn instance not found. Make sure the AddOn is enabled." };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object RunFirmDiagnostics()
        {
            try
            {
                if (RiskGuardAddOn.Instance != null)
                {
                    var res = RiskGuardAddOn.Instance.RunFirmDiagnostics();
                    return new { success = res.Success, logs = res.Logs };
                }
                return new { error = "RiskGuardAddOn instance not found. Make sure the AddOn is enabled." };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object GetRiskGuardState()
        {
            try
            {
                if (RiskGuardAddOn.Instance != null)
                {
                    return new { success = true, version = RiskGuardAddOn.Version, snapshots = RiskGuardAddOn.Instance.GetAccountSnapshots() };
                }
                return new { error = "RiskGuardAddOn instance not found." };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object ReloadRiskGuardState()
        {
            try
            {
                if (RiskGuardAddOn.Instance != null)
                {
                    RiskGuardAddOn.Instance.ReloadPersistedState();
                    return new { success = true };
                }
                return new { error = "RiskGuardAddOn instance not found." };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        // - FSM observation endpoints (-7 of RiskGuardAddOn.md) -
        // Read-only window onto the per-position guard state machines plus a
        // targeted reset. No guard evaluation lives here; the MCP stays an
        // observation/intervention surface, never the driver.
        private object GetFsmState(string accountName, string instrument)
        {
            try
            {
                if (RiskGuardAddOn.Instance == null)
                    return new { error = "RiskGuardAddOn instance not found. Make sure the AddOn is enabled." };

                var snapshots = RiskGuardAddOn.Instance.GetFsmSnapshots();
                if (!string.IsNullOrEmpty(accountName))
                    snapshots = snapshots.Where(s => s.AccountName == accountName).ToList();
                if (!string.IsNullOrEmpty(instrument))
                    snapshots = snapshots.Where(s => s.Instrument == instrument).ToList();
                return new { success = true, count = snapshots.Count, fsms = snapshots };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object ResetFsmState(string accountName, string instrument)
        {
            try
            {
                if (RiskGuardAddOn.Instance == null)
                    return new { error = "RiskGuardAddOn instance not found. Make sure the AddOn is enabled." };
                if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(instrument))
                    return new { error = "Both 'account' and 'instrument' query params are required." };

                bool removed = RiskGuardAddOn.Instance.ResetFsm(accountName, instrument);
                return new { success = true, removed = removed };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object SaInspect()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { error = "no WPF dispatcher" };
            var dates = new List<object>();
            disp.Invoke((Action)(() =>
            {
                var win = FindExistingSaWindow() as System.Windows.DependencyObject;
                if (win == null)
                {
                    var saType = Type.GetType(SaNs + "StrategyAnalyzer, NinjaTrader.Gui");
                    var w = (System.Windows.Window)Activator.CreateInstance(saType);
                    w.Show(); w.WindowState = System.Windows.WindowState.Minimized;
                    win = w;
                }
                _walkSeen.Clear();
                WalkLogical(win, dates, 0);
            }));
            return new { dateProps = dates };
        }

        private readonly HashSet<object> _walkSeen = new HashSet<object>();
        private void WalkLogical(object node, List<object> dates, int depth)
        {
            if (node == null || depth > 80) return;
            var deo = node as System.Windows.DependencyObject;
            if (deo != null) { if (_walkSeen.Contains(node)) return; _walkSeen.Add(node); }
            var t = node.GetType();
            // For XamDateTimeEditor, report its Value binding source (reveals which VM drives it).
            if (t.Name == "XamDateTimeEditor" && deo != null)
            {
                string src = "?";
                try
                {
                    var dp = t.GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as System.Windows.DependencyProperty;
                    var be = dp != null ? System.Windows.Data.BindingOperations.GetBindingExpression(deo, dp) : null;
                    var so = be?.GetType().GetProperty("ResolvedSource")?.GetValue(be);
                    var pth = be?.GetType().GetProperty("ResolvedSourcePropertyName")?.GetValue(be)?.ToString();
                    src = so != null ? so.GetType().Name + "." + pth : "unbound";
                }
                catch { }
                var dvv = GetP(node, "DateValue");
                dates.Add(new { control = t.Name, value = dvv is DateTime dd ? dd.ToString("yyyy-MM-dd HH:mm") : null, bindsTo = src });
            }
            // recurse both logical and visual trees (toolbar controls are often visual-only)
            if (deo != null)
            {
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(deo))
                    WalkLogical(child, dates, depth + 1);
                try
                {
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(deo);
                    for (int i = 0; i < n; i++) WalkLogical(System.Windows.Media.VisualTreeHelper.GetChild(deo, i), dates, depth + 1);
                }
                catch { }
            }
        }

        // Set the SA toolbar's From/To date pickers. The editors display the run-config dates via a
        // (OneWay) binding, so we resolve each editor's binding SOURCE object+property and set THAT
        // directly - that source is what the run actually reads.
        private object _dateNote;
        private void SetSaDateRange(object win, DateTime from, DateTime to)
        {
            try { InvokeM(win, "UpdateLayout"); } catch { }   // realize the property-grid tree first
            var editors = new List<object>();
            var seen = new HashSet<object>();
            CollectDateEditors(win, editors, seen);

            // The backtest run-input From/To are the date editors whose Value binds to a property-grid
            // PropertyItemValue (NOT the TradePerformanceReportViewModel report filter). Pick those,
            // ordered by current date - [From, To].
            var runEditors = editors
                .Select(e => new { e, dv = GetP(e, "DateValue") as DateTime?, src = EditorBindingSource(e) })
                .Where(x => x.dv.HasValue && x.dv.Value.Year >= 2000
                            && x.src != null && x.src.GetType().Name == "PropertyItemValue")
                .OrderBy(x => x.dv.Value)
                .ToList();

            string info;
            if (runEditors.Count >= 2)
            {
                if (from != DateTime.MinValue) SetP(runEditors[0].src, "Value", from);
                if (to != DateTime.MinValue) SetP(runEditors[1].src, "Value", to);
                info = $"set from={from:yyyy-MM-dd} to={to:yyyy-MM-dd} on {runEditors.Count} property-grid editors";
            }
            else info = $"run-input date editors not found (found {runEditors.Count}); range left at default";
            _dateNote = info;
        }

        // The binding source object behind an editor's Value property.
        private object EditorBindingSource(object editor)
        {
            try
            {
                var deo = editor as System.Windows.DependencyObject;
                var dp = editor.GetType().GetField("ValueProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null)
                         as System.Windows.DependencyProperty;
                if (deo == null || dp == null) return null;
                var be = System.Windows.Data.BindingOperations.GetBindingExpression(deo, dp);
                return be?.GetType().GetProperty("ResolvedSource")?.GetValue(be);
            }
            catch { return null; }
        }

        // Collect XamDateTimeEditor from both the logical and visual trees (property-grid editors
        // are often visual-only).
        private void CollectDateEditors(object node, List<object> acc, HashSet<object> seen)
        {
            if (node == null) return;
            var deo = node as System.Windows.DependencyObject;
            if (deo != null) { if (seen.Contains(node)) return; seen.Add(node); }
            if (node.GetType().Name == "XamDateTimeEditor") acc.Add(node);
            if (deo != null)
            {
                foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(deo)) CollectDateEditors(c, acc, seen);
                try { int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(deo);
                    for (int i = 0; i < n; i++) CollectDateEditors(System.Windows.Media.VisualTreeHelper.GetChild(deo, i), acc, seen); }
                catch { }
            }
        }

        private static bool IsSaWindow(System.Windows.Window w)
        {
            var fn = w.GetType().FullName ?? "";
            var title = w.Title ?? "";
            return fn.StartsWith(SaNs) || title.IndexOf("Strategy Analyzer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Adopt an existing Strategy Analyzer window if one is open (e.g. orphaned by a prior
        // hot-swap), so at most one exists and we never have to close (which would prompt).
        private object FindExistingSaWindow()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return null;
            foreach (System.Windows.Window w in app.Windows)
                if (IsSaWindow(w)) return w;
            return null;
        }

        // Manual cleanup only (user-triggered). Closing pops NT8's confirmation dialog unless the
        // user has ticked "don't show again", so this is not used automatically.
        private int CloseAllSaWindows()
        {
            var app = System.Windows.Application.Current;
            if (app == null) return 0;
            var toClose = new List<System.Windows.Window>();
            foreach (System.Windows.Window w in app.Windows)
                if (IsSaWindow(w)) toClose.Add(w);
            foreach (var w in toClose) { try { w.Close(); } catch { } }
            _saWindow = null;
            return toClose.Count;
        }

        // Pull metrics + (capped) trade list out of a StrategyAnalyzerGridEntry's SystemPerformance.
        private object ExtractBacktest(object entry, int maxTrades)
        {
            var perf = GetP(entry, "Results");            // SystemPerformance
            var all = GetP(perf, "AllTrades");            // TradeCollection (IEnumerable<Trade>)
            var tp = GetP(all, "TradesPerformance");      // TradesPerformance
            var cur = tp != null ? GetP(tp, "Currency") : null; // TradesPerformanceValues

            var trades = new List<object>();
            int total = 0, winners = 0, losers = 0;
            // Aggregate P&L per distinct entry (scale-out exits share one entry) so we can report
            // entry-level win rate - comparable to research/kernel numbers, unlike per-partial-trade WR.
            var entryPnl = new Dictionary<string, double>();
            var exitReasons = new Dictionary<string, int>();   // per-partial-exit tally by exit order name
            DateTime firstEntry = DateTime.MaxValue, lastExit = DateTime.MinValue;
            if (all is IEnumerable en)
                foreach (var tr in en)
                {
                    total++;
                    var entryExec = GetP(tr, "Entry");         // Execution
                    var exitExec = GetP(tr, "Exit");
                    var pc = GetP(tr, "ProfitCurrency");
                    double pcd = pc is double dd ? dd : 0;
                    if (pcd > 0) winners++; else if (pcd < 0) losers++;
                    if (GetP(entryExec, "Time") is DateTime et)
                    {
                        if (et < firstEntry) firstEntry = et;
                        var ekey = et.Ticks + "|" + SafeToString(GetP(entryExec, "MarketPosition"));
                        double pv;
                        entryPnl[ekey] = (entryPnl.TryGetValue(ekey, out pv) ? pv : 0) + pcd;
                    }
                    if (GetP(exitExec, "Time") is DateTime xt && xt > lastExit) lastExit = xt;
                    // Exit-reason tally: the exit order's signal name ("bank","runner","flat","time",
                    // "Stop loss", ...). Falls back to the exit order's Name if Execution.Name is empty.
                    var xname = SafeToString(GetP(exitExec, "Name"));
                    if (string.IsNullOrWhiteSpace(xname) || xname == "<toString threw>")
                        xname = SafeToString(GetP(GetP(exitExec, "Order"), "Name"));
                    if (!string.IsNullOrWhiteSpace(xname))
                    {
                        int xc;
                        exitReasons[xname] = (exitReasons.TryGetValue(xname, out xc) ? xc : 0) + 1;
                    }
                    if (trades.Count >= maxTrades) continue;   // still count the rest
                    trades.Add(new
                    {
                        instrument = SafeToString(GetP(entryExec, "Instrument") is object inst ? GetP(inst, "FullName") : null),
                        marketPosition = SafeToString(GetP(entryExec, "MarketPosition")),
                        quantity = GetP(tr, "Quantity"),
                        entryPrice = GetP(entryExec, "Price"),
                        exitPrice = GetP(exitExec, "Price"),
                        entryTime = GetP(entryExec, "Time"),
                        exitTime = GetP(exitExec, "Time"),
                        profitCurrency = pc,
                        profitPoints = GetP(tr, "ProfitPoints"),
                        exitName = GetP(exitExec, "Name"),
                    });
                }

            double gp = D(tp, "GrossProfit") is double g1 ? g1 : 0;
            double gl = D(tp, "GrossLoss") is double g2 ? g2 : 0;
            int entries = entryPnl.Count;
            int winEntries = entryPnl.Values.Count(v => v > 0);
            // Per-ENTRY loss/win profile (kernel-comparable: the scale-out means a full position
            // outcome, not per-partial). This is the diagnostic for "are losers riding to the stop".
            var winVals = entryPnl.Values.Where(v => v > 0).ToList();
            var lossVals = entryPnl.Values.Where(v => v < 0).ToList();
            double? avgWinEntry = winVals.Count > 0 ? (double?)Math.Round(winVals.Average(), 2) : null;
            double? avgLossEntry = lossVals.Count > 0 ? (double?)Math.Round(lossVals.Average(), 2) : null;
            double? maxLossEntry = lossVals.Count > 0 ? (double?)Math.Round(lossVals.Min(), 2) : null;
            return new
            {
                summary = SafeToString(entry),
                metrics = new
                {
                    entries,
                    entryWinRatePct = entries > 0 ? Math.Round(100.0 * winEntries / entries, 1) : 0,   // per-entry (kernel-comparable)
                    avgWinEntry,
                    avgLossEntry,
                    maxLossEntry,
                    exitReasons,
                    totalTrades = D(tp, "TradesCount"),
                    winners,
                    losers,
                    tradeWinRatePct = total > 0 ? Math.Round(100.0 * winners / total, 1) : 0,           // per-NT8-trade (incl. scale-outs)
                    profitFactor = gl != 0 ? Math.Round(gp / Math.Abs(gl), 3) : (double?)null,
                    tradesPerDay = D(tp, "TradesPerDay"),
                    grossProfit = gp,
                    grossLoss = gl,
                    totalCommission = D(tp, "TotalCommission"),
                    maxConsecWinners = D(tp, "MaxConsecutiveWinner"),
                    maxConsecLosers = D(tp, "MaxConsecutiveLoser"),
                    netProfit = D(cur, "CumProfit"),
                    maxDrawdown = D(cur, "Drawdown"),
                    avgProfit = D(cur, "AverageProfit"),
                    largestWinner = D(cur, "LargestWinner"),
                    largestLoser = D(cur, "LargestLoser"),
                    stdDev = D(cur, "StdDev"),
                    firstEntry = firstEntry == DateTime.MaxValue ? null : (DateTime?)firstEntry,
                    lastExit = lastExit == DateTime.MinValue ? null : (DateTime?)lastExit,
                },
                tradeCount = total,
                tradesReturned = trades.Count,
                dateRange = _dateNote,
                trades,
            };
        }

        private static object D(object obj, string prop)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { return null; }
        }

        // - small reflection helpers (instance) -
        private static object GetP(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.GetIndexParameters().Length == 0)
            {
                try { return p.GetValue(o); }
                catch { /* indexer or access threw */ }
            }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                try { return f.GetValue(o); } catch { }
            }
            return null;
        }
        private static void SetP(object o, string name, object val)
        {
            var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.GetIndexParameters().Length == 0)
            {
                try
                {
                    if (val != null && !pi.PropertyType.IsInstanceOfType(val))
                        try { val = pi.PropertyType.IsEnum ? Enum.ToObject(pi.PropertyType, Convert.ToInt64(val)) : Convert.ChangeType(val, pi.PropertyType); } catch { }
                    pi.SetValue(o, val);
                    return;
                }
                catch { /* indexer or access threw */ }
            }
            var fi = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null)
            {
                try { fi.SetValue(o, val); } catch { }
            }
        }
        private static object InvokeM(object o, string name, params object[] args)
        {
            var methods = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           .Where(x => x.Name == name && x.GetParameters().Length == (args?.Length ?? 0)).ToList();
            if (methods.Count == 0)
                throw new Exception($"method not found: {o.GetType().FullName}.{name}/{args?.Length ?? 0}");
            var best = methods.Count == 1 ? methods[0] : FindBestOverload(methods, args);
            if (best == null)
                throw new Exception($"no compatible overload for {o.GetType().FullName}.{name}/{args?.Length ?? 0}");
            return best.Invoke(o, args);
        }

        private static MethodInfo FindBestOverload(List<MethodInfo> methods, object[] args)
        {
            MethodInfo best = null; int bestScore = -1;
            foreach (var m in methods)
            {
                var ps = m.GetParameters(); int score = 0; bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var a = args[i]; var p = ps[i].ParameterType;
                    if (a == null) { if (p.IsValueType) { ok = false; break; } continue; }
                    if (p.IsInstanceOfType(a)) score += 2;
                    else if (a is IConvertible && (p.IsPrimitive || p.IsEnum))
                    {
                        try { Convert.ChangeType(a, p); score += 1; } catch { ok = false; break; }
                    }
                    else { ok = false; break; }
                }
                if (ok && score > bestScore) { bestScore = score; best = m; }
            }
            return best;
        }

        // -
        //  DEV reflection RPC - probe/drive NT8 internals over HTTP so we
        //  can discover the Strategy Analyzer / compile behaviour without
        //  recompiling this addon on every iteration. Localhost + dev-gated.
        //
        //  Body: { "ops": [ { op, ... }, ... ] }  executed in order.
        //  Ops:
        //    listMembers { type, [assembly] }
        //    getStatic   { type, member, [assembly] }
        //    setStatic   { type, member, value, [assembly] }
        //    invokeStatic{ type, method, [args], [assembly] }
        //    getProp     { target(handle/ref), member }
        //    invoke      { target, method, [args] }
        //    new         { type, [args], [assembly] }
        //  Args accept literals or {"$ref":"h3"} / {"$type":"...","value":...} coercions.
        //  Any op result that is a non-primitive object is stored and returned as a handle
        //  ("h1", "h2", ...) reusable as a target/$ref in later ops.
        // -

        private object DevReflect(string body)
        {
            // Json.NET treats properties beginning with '$' as metadata tokens by default,
            // which strips them from the parsed JObject. We need $ref/$result placeholders
            // to survive as ordinary properties, so parse with MetadataPropertyHandling.Ignore.
            var req = JsonConvert.DeserializeObject<JObject>(body ?? "{}",
                new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore })
                ?? new JObject();
            var ops = req["ops"] as JArray ?? new JArray();

            // WPF objects (windows, viewmodels) must be touched on the UI dispatcher.
            // Pass "ui": true to run the whole op batch on the app dispatcher (for
            // Control Center / SA window objects that live on thread 1).
            // Pass "dispatcher": "auto" to resolve the target object's own dispatcher
            // per-op (for chart-owned objects that live on thread 18/19).
            if (req.Bool("ui", false))
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null) return new { error = "no WPF dispatcher (Application.Current is null)" };
                object uiResult = null;
                disp.Invoke((Action)(() => { uiResult = RunOps(ops); }));
                return uiResult;
            }
            return RunOps(ops);
        }

        private object RunOps(JArray ops)
        {
            var results = new List<object>();
            // Clear the handle registry at the start of each batch so handles
            // from a previous dev/reflect call don't accumulate (memory leak).
            lock (_handles) { _handles.Clear(); }
            _batchHandles = new List<string>();
            foreach (var opTok in ops)
            {
                var op = (JObject)opTok;
                var kind = op.Str("op");
                _lastHandle = null;
                try { var r = RunOp(kind, op); _batchHandles.Add(_lastHandle); results.Add(r); }
                catch (Exception ex)
                {
                    var inner = ex.InnerException;
                    _batchHandles.Add(null);
                    results.Add(new { op = kind, error = ex.Message,
                        inner = inner?.Message, innerType = inner?.GetType().FullName,
                        innerStack = inner?.StackTrace, stack = ex.StackTrace });
                    return new { results };
                }
            }
            return new { results };
        }
        private string _lastHandle;
        private List<string> _batchHandles;

        private object RunOp(string kind, JObject op)
        {
            switch (kind)
            {
                case "findTypes":
                {
                    var pat = op.Str("pattern") ?? "";
                    var asmFilter = op.Str("assembly");
                    var matches = new List<string>();
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!string.IsNullOrEmpty(asmFilter) && !asm.GetName().Name.Equals(asmFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                            if (t.FullName != null && t.FullName.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                                matches.Add(t.FullName);
                    }
                    matches.Sort();
                    return new { count = matches.Count, types = matches.Take(120).ToList() };
                }
                case "listMembers":
                {
                    var t = ResolveType(op);
                    return new
                    {
                        type = t.FullName,
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(m => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
                            .OrderBy(s => s).Distinct().ToList(),
                        properties = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(p => $"{p.PropertyType.Name} {p.Name}").OrderBy(s => s).ToList(),
                        fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Select(f => $"{f.FieldType.Name} {f.Name}").OrderBy(s => s).ToList(),
                    };
                }
                case "getStatic":
                {
                    var t = ResolveType(op);
                    var member = op.Str("member");
                    var val = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                              ?? t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                    return Describe(val);
                }
                case "setStatic":
                {
                    var t = ResolveType(op);
                    var member = op.Str("member");
                    var val = Coerce(op["value"]);
                    var pi = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (pi != null) pi.SetValue(null, val);
                    else t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, val);
                    return new { ok = true };
                }
                case "invokeStatic":
                {
                    var t = ResolveType(op);
                    var args = CoerceArgs(op["args"]);
                    var mi = FindMethod(t, op.Str("method"), args, true);
                    var val = mi.Invoke(null, args);
                    return Describe(val);
                }
                case "new":
                {
                    var t = ResolveType(op);
                    var args = CoerceArgs(op["args"]);
                    var val = Activator.CreateInstance(t, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                    return Describe(val);
                }
                case "getProp":
                {
                    var target = Coerce(op["target"]);
                    var member = op.Str("member") ?? op.Str("property");
                    var t = target.GetType();
                    var val = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
                              ?? t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
                    return Describe(val);
                }
                case "setProp":
                {
                    var target = Coerce(op["target"]);
                    var member = op.Str("member");
                    var t = target.GetType();
                    var val = Coerce(op["value"]);
                    var pi = t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                    {
                        // coerce numeric/enum to the property type when needed
                        if (val != null && !pi.PropertyType.IsInstanceOfType(val))
                        {
                            try { val = pi.PropertyType.IsEnum ? Enum.ToObject(pi.PropertyType, Convert.ToInt64(val)) : Convert.ChangeType(val, pi.PropertyType); }
                            catch { }
                        }
                        pi.SetValue(target, val);
                    }
                    else t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, val);
                    return new { ok = true, member };
                }
                case "invoke":
                {
                    var target = Coerce(op["target"]);
                    var args = CoerceArgs(op["args"]);
                    var mi = FindMethod(target.GetType(), op.Str("method"), args, false);
                    var val = mi.Invoke(target, args);
                    return Describe(val);
                }
                default:
                    throw new Exception($"unknown op: {kind}");
            }
        }

        private Type ResolveType(JObject op)
        {
            var typeName = op.Str("type");
            var asm = op.Str("assembly");
            Type t = asm != null ? Type.GetType($"{typeName}, {asm}") : null;
            if (t == null) t = Type.GetType(typeName);
            if (t == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { t = a.GetType(typeName); if (t != null) break; }
            if (t == null) throw new Exception($"type not found: {typeName}");
            return t;
        }

        private MethodInfo FindMethod(Type t, string name, object[] args, bool isStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var candidates = t.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == (args?.Length ?? 0)).ToList();
            if (candidates.Count == 0) throw new Exception($"method not found: {t.FullName}.{name}/{args?.Length ?? 0}");
            if (candidates.Count > 1 && args != null)
            {
                MethodInfo best = null; int bestScore = -1;
                foreach (var c in candidates)
                {
                    var ps = c.GetParameters(); int score = 0; bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var a = args[i]; var p = ps[i].ParameterType;
                        if (a == null) { if (p.IsValueType) { ok = false; break; } continue; }
                        if (p.IsInstanceOfType(a)) score += 2;
                        else if (a is IConvertible && (p.IsPrimitive || p.IsEnum))
                        {
                            try { Convert.ChangeType(a, p); score += 1; } catch { ok = false; break; }
                        }
                        else { ok = false; break; }
                    }
                    if (ok && score > bestScore) { bestScore = score; best = c; }
                }
                if (best != null) return best;
            }
            return candidates[0];
        }

        private object[] CoerceArgs(JToken argsTok)
        {
            if (!(argsTok is JArray arr)) return new object[0];
            return arr.Select(Coerce).ToArray();
        }

        // Coerce a JSON token to a CLR value. Supports literals, {"$ref":"h1"} handles,
        // {"$type":"...","value":...} typed values, and {"$enum":"Type.Value"}.
        // NOTE: Json.NET treats $ref/$result/$type as metadata tokens, so the JObject indexer
        // returns null for them. We scan JObject.Properties() to resolve these placeholders.
        private object Coerce(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;
            if (tok is JObject o)
            {
                string GetMeta(string key)
                {
                    foreach (var p in o.Properties())
                        if (p.Name == key)
                            return p.Value?.ToString();
                    return null;
                }
                JToken GetToken(string key)
                {
                    foreach (var p in o.Properties())
                        if (p.Name == key)
                            return p.Value;
                    return null;
                }

                // Support both Json.NET $-style metadata and Python-safe aliases.
                var refId = GetMeta("$ref") ?? GetMeta("ref");
                object h;
                if (refId != null) return _handles.TryGetValue(refId, out h) ? h : throw new Exception($"no handle {refId}");

                var resultIdx = GetMeta("$result") ?? GetMeta("result");
                if (resultIdx != null)
                {
                    int n = int.Parse(resultIdx);
                    var hid = (_batchHandles != null && n >= 0 && n < _batchHandles.Count) ? _batchHandles[n] : null;
                    object hv;
                    if (hid == null || !_handles.TryGetValue(hid, out hv)) throw new Exception($"$result {n} is not a handle");
                    return hv;
                }

                var enumStr = GetMeta("$enum") ?? GetMeta("enum");
                if (enumStr != null)
                {
                    var idx = enumStr.LastIndexOf('.');
                    var et = ResolveType(new JObject { ["type"] = enumStr.Substring(0, idx), ["assembly"] = GetMeta("assembly") });
                    return Enum.Parse(et, enumStr.Substring(idx + 1));
                }

                var typeStr = GetMeta("$type") ?? GetMeta("type");
                if (typeStr != null)
                {
                    var target = ResolveType(new JObject { ["type"] = typeStr, ["assembly"] = GetMeta("assembly") });
                    var valTok = GetToken("value");
                    return Convert.ChangeType((valTok is JValue jv ? jv.Value : valTok), target);
                }

                var strlistTok = GetToken("$strlist") ?? GetToken("strlist");
                if (strlistTok != null) return strlistTok.Select(x => x.ToString()).ToList();

                // Any remaining JObject that didn't match $ref/$result/$enum/$type/$strlist returns null;
                // placeholders are resolved above, and nested JObject arguments are not supported.
            }
            if (tok is JArray a) return a.Select(Coerce).ToList();
            if (tok is JValue v) return v.Value;
            return null;
        }

        // Turn a return value into a JSON-friendly description; register non-trivial
        // objects as reusable handles.
        private object Describe(object val)
        {
            if (val == null) return new { value = (object)null };
            var t = val.GetType();
            if (val is string || t.IsPrimitive || val is DateTime || val is decimal || t.IsEnum)
                return new { type = t.FullName, value = val.ToString() };

            bool isJson = val is JToken;
            bool shouldEnumerate = val is IEnumerable en && !(val is IDictionary) && !(val is StringBuilder) && !isJson;
            if (shouldEnumerate)
            {
                var items = new List<object>();
                int n = 0;
                foreach (var item in (IEnumerable)val) { items.Add(item?.ToString()); if (++n >= 50) break; }
                var id = "h" + (++_handleSeq);
                _handles[id] = val;
                _lastHandle = id;
                return new { handle = id, type = t.FullName, count = items.Count, items, toString = SafeToString(val) };
            }

            var hid = "h" + (++_handleSeq);
            _handles[hid] = val;
            _lastHandle = hid;
            return new { handle = hid, type = t.FullName, toString = SafeToString(val) };
        }

        private static string SafeToString(object v) { try { return v.ToString(); } catch { return "<toString threw>"; } }

        // -
        //  Phase 1 handlers (unchanged)
        // -

        // Account.Get in NT8.1 requires the account's currency (Denomination).
        private static double AcctGet(Account a, AccountItem item)
        {
            try { return a.Get(item, a.Denomination); } catch { return 0; }
        }

        private object GetAccountInfo()
        {
            var accounts = new List<object>();
            foreach (Account account in Account.All)
                accounts.Add(new
                {
                    name = account.Name,
                    provider = account.Provider.ToString(),
                    denomination = account.Denomination.ToString(),
                    cashValue = AcctGet(account, AccountItem.CashValue),
                    netLiquidation = AcctGet(account, AccountItem.NetLiquidation),
                    realizedPnL = AcctGet(account, AccountItem.RealizedProfitLoss),
                    unrealizedPnL = AcctGet(account, AccountItem.UnrealizedProfitLoss),
                    buyingPower = AcctGet(account, AccountItem.BuyingPower),
                });
            return accounts;
        }

        private object GetPositions()
        {
            var positions = new List<object>();
            foreach (Account account in Account.All)
                foreach (Position pos in account.Positions)
                {
                    if (pos.Instrument == null || pos.MarketPosition == MarketPosition.Flat) continue;
                    double upnl = 0;
                    try { upnl = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency); } catch { }
                    positions.Add(new
                    {
                        account = account.Name,
                        symbol = pos.Instrument.FullName,
                        marketPosition = pos.MarketPosition.ToString(),
                        quantity = pos.Quantity,
                        avgPrice = pos.AveragePrice,
                        unrealizedPnL = upnl,
                    });
                }
            return positions;
        }

        private object GetOrders()
        {
            var orders = new List<object>();
            foreach (Account account in Account.All)
                foreach (Order order in account.Orders)
                {
                    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;
                    orders.Add(new
                    {
                        id = order.Id.ToString(), orderId = order.OrderId, name = order.Name, account = account.Name,
                        symbol = order.Instrument?.FullName, action = order.OrderAction.ToString(),
                        orderType = order.OrderType.ToString(), quantity = order.Quantity,
                        limitPrice = order.LimitPrice, stopPrice = order.StopPrice,
                        state = order.OrderState.ToString(), filled = order.Filled, time = order.Time,
                    });
                }
            return orders;
        }

        // Read-only inventory of the strategies NT8 currently runs on an account
        // (Account.Strategies / ServerStrategies). Basis for nt_strategy_status.
        private object RunningStrategies()
        {
            var accountStrategies = new List<object>();
            foreach (Account a in Account.All)
            {
                AddStrats(accountStrategies, a.Strategies as System.Collections.IEnumerable, "account:" + a.Name);
                AddStrats(accountStrategies, a.ServerStrategies as System.Collections.IEnumerable, "server:" + a.Name);
            }
            return new { count = accountStrategies.Count, accountStrategies };
        }

        private void AddStrats(List<object> outp, System.Collections.IEnumerable col, string src)
        {
            if (col == null) return;
            foreach (var s in col) if (s != null) outp.Add(DescribeStrategy(s, src));
        }

        // -
        //  Deploy / stop a strategy on a chart (SIM-first). Validated sequence:
        //    create instance (defaults auto-populate) -> set Account (+params) ->
        //    ChartControl.ApplyStrategy(null, strat, chartBars, false, null) [adds, disabled] ->
        //    ChartControl.StrategyEnable(template, chartBars, true, null) [enables -> Realtime].
        //  Stop = static ChartControl.StrategyDisable(template, clone) + Strategies.Remove(template).
        // -
        private object DeployStrategy(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string stratName = req.Str("strategy");
            string instrument = req.Str("instrument");
            string accountName = req.Str("account");
            if (string.IsNullOrEmpty(accountName)) accountName = "Sim101";
            bool enable = req["enable"] == null || (bool)req["enable"];
            bool confirmLive = req["confirmLive"] != null && (bool)req["confirmLive"];
            var prms = req["params"] as JObject;
            if (string.IsNullOrEmpty(stratName) || string.IsNullOrEmpty(instrument))
                return new { error = "strategy and instrument are required" };

            var account = Account.All.FirstOrDefault(a => a.Name == accountName);
            if (account == null) return new { error = "account not found: " + accountName };
            // SIM-first guard: refuse a non-sim account unless explicitly confirmed.
            // P2-38: provider only. The name clause was OR'd in FRONT of the provider test, so
            // a funded account called "SimpsonFund" classified as simulated and slipped this
            // gate without confirmLive=true. Same root cause as P1-20, different blast radius.
            bool isSim = TradeCopierEngine.IsSimulationAccount(account);
            if (!isSim && !confirmLive)
                return new { error = "refusing to deploy to LIVE account '" + account.Name + "' without confirmLive=true" };

            var stratType = FindStrategyType(stratName);
            if (stratType == null) return new { error = "strategy type not found (compiled?): " + stratName };

            object result = null; Exception err = null;

            // FindChartControl already marshals to each chart window's own dispatcher
            // internally and returns the live ChartControl.  We must then use that
            // ChartControl's OWN dispatcher (not the app dispatcher) for any calls on
            // it, because each NT8 chart lives on its own thread (18/19, not thread 1).
            object ccFound, cbFound;
            if (!FindChartControl(instrument, out ccFound, out cbFound) || ccFound == null || cbFound == null)
            {
                return new
                {
                    status = "best_effort",
                    error = $"could not access a chart control for '{instrument}'. NinjaTrader 8 does not expose a public API to open a chart from an AddOn. Open a chart for this instrument manually via the Control Center shortcut Ctrl+Shift+N, then call deploy again. Deploy can attach to a chart that already has at least one strategy on this instrument; if the chart is strategy-less, attach the first strategy via the chart's Strategies dialog."
                };
            }

            var chartDisp = (ccFound as System.Windows.Threading.DispatcherObject)?.Dispatcher;
            if (chartDisp == null) return new { error = "no chart dispatcher available" };

            chartDisp.Invoke((Action)(() =>
            {
                try
                {
                    var cc = ccFound;
                    var cb = cbFound;

                    var strat = Activator.CreateInstance(stratType);   // ctor runs SetDefaults
                    SetP(strat, "Account", account);
                    var applied = new List<string>();
                    if (prms != null)
                        foreach (var p in prms.Properties())
                            if (p.Value is JValue jv && jv.Value != null)
                                try { SetP(strat, p.Name, jv.Value); applied.Add(p.Name); } catch { }

                    var addedObj = InvokeM(cc, "ApplyStrategy", null, strat, cb, false, null);
                    object live = addedObj ?? strat;
                    string state = GetMember(live, "State")?.ToString();
                    if (enable)
                    {
                        var enabled = InvokeM(cc, "StrategyEnable", live, cb, true, null);
                        if (enabled != null) live = enabled;
                        state = GetMember(live, "State")?.ToString();
                    }
                    result = new
                    {
                        deployed = true, strategy = stratName, account = account.Name, isSim,
                        instrument = GetMember(cc, "Instrument")?.ToString(),
                        enabled = enable, state, paramsApplied = applied,
                    };
                }
                catch (Exception ex) { err = ex; }
            }));
            if (err != null) return new { error = "deploy failed: " + err.Message, stack = err.StackTrace };
            return result;
        }

        private object StopStrategy(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string stratName = req.Str("strategy");
            string accountName = req.Str("account");
            bool flatten = req["flatten"] == null || (bool)req["flatten"];
            var stopped = new List<object>(); Exception err = null;

            // Strategy clones hold a reference to their ChartControl, which lives on
            // the chart window's own dispatcher thread (18/19), not the app dispatcher
            // (thread 1).  We must marshal chart-control access to the correct thread.
            // First, collect matching clones and their accounts from Account.All
            // (which is safe to read from any thread).
            var clonesToStop = new List<object[]>();
            foreach (Account a in Account.All)
            {
                if (!string.IsNullOrEmpty(accountName) && a.Name != accountName) continue;
                foreach (var s in a.Strategies)
                    if (s != null && (string.IsNullOrEmpty(stratName) || s.GetType().Name == stratName))
                        clonesToStop.Add(new object[] { s, a });
            }

            foreach (var entry in clonesToStop)
            {
                var clone = entry[0];
                var acct = (Account)entry[1];
                var cc = GetMember(clone, "ChartControl");
                if (cc == null) continue;
                var chartDisp = (cc as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                if (chartDisp == null) continue;
                chartDisp.Invoke((Action)(() =>
                {
                    try
                    {
                        // the chart holds the TEMPLATE (same Id as the running clone)
                        object template = null;
                        var col = GetMember(cc, "Strategies") as System.Collections.IEnumerable;
                        var cloneId = GetMember(clone, "Id");
                        if (col != null)
                            foreach (var tmpl in col)
                                if (tmpl != null && Equals(GetMember(tmpl, "Id"), cloneId)) { template = tmpl; break; }
                        if (template == null) template = clone;
                        var posObj = GetMember(clone, "Position");
                        var posBefore = GetMember(posObj, "MarketPosition")?.ToString();
                        int posQty = 0; try { posQty = Convert.ToInt32(GetMember(posObj, "Quantity")); } catch { }
                        var instr = GetMember(clone, "Instrument") as Instrument;
                        try { InvokeStaticM(cc.GetType(), "StrategyDisable", template, clone); } catch { }
                        try { SetP(template, "IsEnabled", false); } catch { }
                        try { InvokeM(col, "Remove", template); } catch { }
                        // Auto-flatten THIS strategy's own position with an offsetting market
                        // order (strategy-sized, so it won't zero another strategy's net).
                        string flattenResult = "none";
                        if (flatten && instr != null && posQty > 0 && (posBefore == "Long" || posBefore == "Short"))
                        {
                            try
                            {
                                var act = posBefore == "Long" ? OrderAction.Sell : OrderAction.Buy;
                                var o = acct.CreateOrder(instr, act, OrderType.Market, TimeInForce.Day, posQty, 0, 0, string.Empty, "McpFlatten", null);
                                acct.Submit(new[] { o });
                                flattenResult = act + " " + posQty + " market";
                            }
                            catch (Exception fex) { flattenResult = "FAILED: " + fex.Message; }
                        }
                        stopped.Add(new { strategy = clone.GetType().Name, account = acct.Name, positionAtStop = posBefore, flatten = flattenResult });
                    }
                    catch (Exception ex) { err = ex; }
                }));
            }
            if (err != null) return new { error = "stop failed: " + err.Message, stack = err.StackTrace };
            return new { stoppedCount = stopped.Count, stopped,
                         note = "disabled + removed from chart; open positions auto-flattened via an offsetting market order when flatten=true (default)" };
        }

        // Change inputs on a RUNNING strategy live (no restart): e.g. Qty (#2), or
        // Allow long / Allow short to pause/resume trading (#1). Only affects inputs the
        // strategy re-reads each bar; startup-only inputs need a disable/enable to take hold.
        private object SetStrategyParam(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string stratName = req.Str("strategy");
            string accountName = req.Str("account");
            var prms = req["params"] as JObject;
            if (prms == null || !prms.Properties().Any())
                return new { error = "params object required, e.g. { \"Qty\": 2 } or { \"AllowLong\": false, \"AllowShort\": false }" };

            var updated = new List<object>();
            foreach (Account a in Account.All)
            {
                if (!string.IsNullOrEmpty(accountName) && a.Name != accountName) continue;
                foreach (var s in a.Strategies)
                {
                    if (s == null) continue;
                    if (!string.IsNullOrEmpty(stratName) && s.GetType().Name != stratName) continue;
                    var changes = new List<object>();
                    foreach (var p in prms.Properties())
                    {
                        if (!(p.Value is JValue jv) || jv.Value == null) continue;
                        object before = GetMember(s, p.Name);
                        bool ok = false;
                        try { SetP(s, p.Name, jv.Value); ok = true; } catch { }
                        object after = GetMember(s, p.Name);
                        changes.Add(new { name = p.Name, before = before?.ToString(), after = after?.ToString(), applied = ok });
                    }
                    updated.Add(new { strategy = s.GetType().Name, account = a.Name,
                                      state = GetMember(s, "State")?.ToString(), changes });
                }
            }
            if (updated.Count == 0)
                return new { error = "no running strategy matched (strategy='" + stratName + "', account='" + accountName + "')" };
            return new { updatedCount = updated.Count, updated,
                         note = "applies to inputs the strategy reads each bar (Qty, AllowLong/AllowShort, etc.); startup-only inputs (instrument, session windows) need a disable/enable" };
        }

        // Locate the target chart's ChartControl + primary ChartBars for an instrument.
        //   (a) reuse the ChartControl off any running strategy on a matching chart;
        //   (b) else, scan all NT8 chart windows via Globals.AllWindows (more complete
        //       than Application.Current.Windows, which can miss floating/minimized
        //       charts). For each Chart window collect ChartControls from:
        //         - ActiveChartControl (focused/visible tab)
        //         - the private tabControl field's tab items (unfocused tabs still hold
        //           the ChartControl in their Content or ChartControl property)
        //         - a visual-tree walk of the window and its ParentGrid (covers any
        //           ChartControl not reachable via the tab API).
        //   GetMember swallows exceptions, so reflectively reading private fields on an
        //   unfocused or not-yet-loaded chart just yields null rather than failing.
        private bool FindChartControl(string instrument, out object cc, out object cb)
        {
            cc = null; cb = null;
            var want = Instrument.GetInstrument(instrument);
            string wantName = want?.FullName;
            string wantMaster = want?.MasterInstrument?.Name;

            // (a) via a running strategy (safe to read from any thread).
            foreach (Account a in Account.All)
                foreach (var s in a.Strategies)
                {
                    if (s == null) continue;
                    var c = GetMember(s, "ChartControl");
                    if (c == null) continue;
                    var cInstr = GetMember(c, "Instrument") as Instrument;
                    if (!InstrumentMatches(cInstr, wantName)
                        && !(wantMaster != null && InstrumentMatches(cInstr, wantMaster))) continue;
                    cc = c; cb = GetMember(s, "ChartBars") ?? FirstBars(c);
                    if (cb != null) return true;
                }

            // (b) via chart windows.  Each Chart window lives on its own WPF dispatcher,
            // so we enumerate Globals.AllWindows on the main dispatcher, then marshal
            // per-window inspection to that window's own dispatcher.
            var appDispatcher = System.Windows.Application.Current?.Dispatcher;
            if (appDispatcher == null) return false;

            var chartWindows = new List<System.Windows.Window>();
            // No-timeout Invoke((Action)) -- the 3-arg overload with a TimeSpan returns
            // without running the delegate if thread 1 is momentarily busy, and Background
            // priority is below input/normal so it can be starved by chart rendering.
            appDispatcher.Invoke((Action)(() =>
            {
                try
                {
                    System.Collections.IEnumerable windows = GetStaticMember(typeof(NinjaTrader.Core.Globals), "AllWindows") as System.Collections.IEnumerable;
                    if (windows == null)
                    {
                        try { windows = System.Windows.Application.Current.Windows; } catch { }
                    }
                    if (windows == null) return;
                    foreach (var w in windows)
                    {
                        if (w == null) continue;
                        var wType = w.GetType();
                        if (!wType.FullName.Contains("Chart") && !wType.Name.Contains("Chart")) continue;
                        chartWindows.Add(w as System.Windows.Window);
                    }
                }
                catch { }
            }));

            object foundCc = null, foundCb = null;
            foreach (var win in chartWindows)
            {
                if (win == null) continue;
                var winDispatcher = (win as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                if (winDispatcher == null) continue;

                bool matched = false;
                // Marshal to the chart window's OWN dispatcher (NT8 gives each chart its own
                // thread) and use the no-timeout overload so a busy chart thread doesn't fail
                // the lookup.
                winDispatcher.Invoke((Action)(() =>
                {
                    try
                    {
                        var controls = new List<object>();
                        CollectChartControlsFromWindow(win, controls);
                        foreach (var c in controls)
                        {
                            var cInstr = GetMember(c, "Instrument") as Instrument;
                            if (!InstrumentMatches(cInstr, wantName)
                                && !(wantMaster != null && InstrumentMatches(cInstr, wantMaster))) continue;
                            var first = FirstBars(c);
                            if (first != null) { foundCc = c; foundCb = first; matched = true; return; }
                        }
                    }
                    catch { }
                }));
                if (matched) { cc = foundCc; cb = foundCb; return true; }
            }
            return false;
        }

        private static object FirstBars(object chartControl)
        {
            var barsArr = GetMember(chartControl, "BarsArray") as System.Collections.IEnumerable;
            if (barsArr != null) foreach (var b in barsArr) return b;
            return null;
        }

        // Match a chart's instrument to the requested one by FullName, else MasterInstrument
        // name (so "MNQ 09-26" resolves regardless of the " Globex" suffix / exact expiry text).
        private static bool InstrumentMatches(Instrument chartInstr, string wantFullName)
        {
            if (chartInstr == null) return false;
            if (wantFullName == null) return true;
            if (chartInstr.FullName == wantFullName) return true;
            try
            {
                var wantMaster = wantFullName.Split(' ')[0];
                return string.Equals(chartInstr.MasterInstrument?.Name, wantMaster, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Enumerate all open chart windows and their instruments.  Each chart window
        // owns its own WPF dispatcher, so we inspect each window on its own thread.
        // Returns a list of { windowType, title, instrumentFullName, instrumentMaster,
        // isActive, isVisible, actualSize, dispatcherThreadId, error }.
        private List<object> ListOpenCharts()
        {
            var result = new List<object>();
            var appDispatcher = System.Windows.Application.Current?.Dispatcher;
            if (appDispatcher == null) return result;

            var chartWindows = new List<object>();
            bool enumRan = false; string enumError = null;
            Action enumAction = () =>
            {
                enumRan = true;
                try
                {
                    System.Collections.IEnumerable windows = GetStaticMember(typeof(NinjaTrader.Core.Globals), "AllWindows") as System.Collections.IEnumerable;
                    if (windows == null)
                    {
                        try { windows = System.Windows.Application.Current.Windows; } catch { }
                    }
                    if (windows == null) { enumError = "AllWindows is null"; return; }
                    foreach (var w in windows)
                    {
                        if (w == null) continue;
                        var wType = w.GetType();
                        if (!wType.FullName.Contains("Chart") && !wType.Name.Contains("Chart")) continue;
                        chartWindows.Add(w);
                    }
                }
                catch (Exception ex) { enumError = ex.Message; }
            };
            // If we're already on the UI thread, run inline (Invoke would deadlock).
            // Use the no-timeout Invoke((Action)) overload: NT8's main UI dispatcher (thread 1)
            // is responsive (dev/reflect ui:true completes in <50ms) but the 3-arg overload
            // with a TimeSpan timeout returns without running the delegate if thread 1 is
            // momentarily busy, producing a false "timed out" / 0-chart-windows result.
            // Additionally, each Chart window lives on its OWN dispatcher thread (NT8 spawns
            // a dedicated thread per chart window), so per-window property reads must be
            // marshaled to that window's Dispatcher -- not Application.Current.Dispatcher.
            var enumSw = System.Diagnostics.Stopwatch.StartNew();
            if (appDispatcher.CheckAccess()) enumAction();
            else appDispatcher.Invoke((Action)(enumAction));
            enumSw.Stop();
            if (!enumRan) enumError = "appDispatcher.Invoke did not run the delegate after " + enumSw.ElapsedMilliseconds + "ms";
            if (chartWindows.Count == 0)
                result.Add(new { windowType = "enumeration", error = enumError ?? ("enumRan=" + enumRan + " but 0 chart windows found"), chartWindowCount = chartWindows.Count, appDispatcherThread = appDispatcher.Thread?.ManagedThreadId });

            foreach (var w in chartWindows)
            {
                var wType = w.GetType();
                string title = null; string instrFull = null; string instrMaster = null; string error = null;
                bool? isActive = null, isVisible = null; double? actualW = null, actualH = null; int? dispatcherThreadId = null;
                bool invokeRan = false;

                var winDispatcher = (w as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                if (winDispatcher != null)
                {
                    dispatcherThreadId = winDispatcher.Thread?.ManagedThreadId;
                    Action winAction = () =>
                    {
                        invokeRan = true;
                        try
                        {
                            title = GetMember(w, "Title")?.ToString();
                            var win = w as System.Windows.Window;
                            isActive = win?.IsActive;
                            isVisible = win?.IsVisible;
                            actualW = win?.ActualWidth;
                            actualH = win?.ActualHeight;
                            var controls = new List<object>();
                            CollectChartControlsFromWindow(w, controls);
                            foreach (var c in controls)
                            {
                                var ci = GetMember(c, "Instrument") as Instrument;
                                if (ci != null)
                                {
                                    instrFull = ci.FullName;
                                    instrMaster = ci.MasterInstrument?.Name;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) { error = ex.Message + " | " + ex.InnerException?.Message; }
                    };
                    var winSw = System.Diagnostics.Stopwatch.StartNew();
                    if (winDispatcher.CheckAccess()) winAction();
                    else winDispatcher.Invoke((Action)(winAction));
                    winSw.Stop();
                    if (!invokeRan) error = "winDispatcher.Invoke did not run the delegate after " + winSw.ElapsedMilliseconds + "ms";
                }
                else
                {
                    error = "no dispatcher";
                }

                result.Add(new
                {
                    windowType = wType.FullName,
                    title,
                    instrumentFullName = instrFull,
                    instrumentMaster = instrMaster,
                    isActive,
                    isVisible,
                    actualSize = (actualW.HasValue && actualH.HasValue) ? actualW + "x" + actualH : null,
                    dispatcherThreadId,
                    invokeRan,
                    error
                });
            }
            return result;
        }

        // Find a compiled NinjaScript strategy Type by class name across loaded assemblies.
        // Handles both the default namespace (NinjaTrader.NinjaScript.Strategies.{name}) and
        // nested namespaces such as NinjaTrader.NinjaScript.Strategies.Vinay.{name}.
        private static Type FindStrategyType(string name)
        {
            string full = "NinjaTrader.NinjaScript.Strategies." + name;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(full, false) ?? asm.GetType(name, false); } catch { }
                if (t != null && !t.IsAbstract) return t;
            }
            // Fallback: scan all types in loaded assemblies by short or full name.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (!t.IsAbstract && !t.IsInterface && (t.Name == name || t.FullName == name || (t.FullName != null && t.FullName.EndsWith("." + name))))
                            return t;
                }
                catch { }
            }
            return null;
        }

        private static object InvokeStaticM(Type t, string name, params object[] args)
        {
            for (var bt = t; bt != null; bt = bt.BaseType)
            {
                var m = bt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == (args?.Length ?? 0));
                if (m != null) return m.Invoke(null, args);
            }
            return null;
        }

        private object DescribeStrategy(object s, string source)
        {
            var t = s.GetType();
            var chain = new List<string>();
            for (var b = t; b != null && b != typeof(object); b = b.BaseType) chain.Add(b.FullName);
            object instr = GetMember(s, "Instrument");
            object acct = GetMember(s, "Account");
            object pos = GetMember(s, "Position");
            return new
            {
                source,
                type = t.FullName,
                name = GetMember(s, "Name")?.ToString(),
                state = GetMember(s, "State")?.ToString(),
                isEnabled = GetMember(s, "IsEnabled")?.ToString(),
                account = (acct as Account)?.Name ?? GetMember(acct, "Name")?.ToString(),
                instrument = (instr as Instrument)?.FullName ?? instr?.ToString(),
                barsPeriod = GetMember(s, "BarsPeriod")?.ToString(),
                calculate = GetMember(s, "Calculate")?.ToString(),
                marketPosition = GetMember(pos, "MarketPosition")?.ToString(),
                quantity = GetMember(pos, "Quantity"),
            };
        }

        // Reflectively read a property or field (public or non-public); StrategyBase
        // exposes several members as protected so a direct cast isn't always enough.
        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var t = o.GetType();
            var p = t.GetProperty(name, BF);
            if (p != null) { try { return p.GetValue(o); } catch { return null; } }
            var f = t.GetField(name, BF);
            if (f != null) { try { return f.GetValue(o); } catch { return null; } }
            return null;
        }

        // Walk a window's visual tree collecting ChartControl instances (ActiveChartControl
        // is null unless the tab is focused, so we find them structurally instead).
        private static void CollectChartControlsStatic(System.Windows.DependencyObject node, List<object> found, HashSet<object> seen, int depth)
        {
            if (node == null || depth > 400 || seen.Contains(node)) return;
            seen.Add(node);
            if (node.GetType().FullName == "NinjaTrader.Gui.Chart.ChartControl") found.Add(node);
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); } catch { }
            for (int i = 0; i < n; i++)
                CollectChartControlsStatic(System.Windows.Media.VisualTreeHelper.GetChild(node, i), found, seen, depth + 1);
        }

        private void CollectChartControls(System.Windows.DependencyObject node, List<object> found, HashSet<object> seen, int depth)
        {
            CollectChartControlsStatic(node, found, seen, depth);
        }

        // Reflectively read a static property/field (public or non-public) off a type.
        private static object GetStaticMember(Type t, string name)
        {
            if (t == null) return null;
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var p = t.GetProperty(name, BF);
            if (p != null) { try { return p.GetValue(null); } catch { return null; } }
            var f = t.GetField(name, BF);
            if (f != null) { try { return f.GetValue(null); } catch { return null; } }
            return null;
        }

        private object PlaceOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            string reqAccount = req.GetValueOrDefault("account")?.ToString();
            Account account = null;
            if (!string.IsNullOrEmpty(reqAccount))
            {
                account = Account.All.FirstOrDefault(a => a.Name.Equals(reqAccount, StringComparison.OrdinalIgnoreCase));
            }
            if (account == null)
            {
                account = Account.All.FirstOrDefault(a => a.Name == "Sim101") 
                          ?? Account.All.FirstOrDefault(a => !a.Name.Equals("Backtest", StringComparison.OrdinalIgnoreCase))
                          ?? Account.All.FirstOrDefault();
            }
            if (account == null) return new { error = "no account available" };

            // SIM-first guard: refuse order placement on a LIVE account unless confirmLive=true is explicitly provided
            // P2-38: provider only. The name clause was OR'd in FRONT of the provider test, so
            // a funded account called "SimpsonFund" classified as simulated and slipped this
            // gate without confirmLive=true. Same root cause as P1-20, different blast radius.
            bool isSim = TradeCopierEngine.IsSimulationAccount(account);
            bool confirmLive = req.ContainsKey("confirmLive") && Convert.ToBoolean(req["confirmLive"]);
            if (!isSim && !confirmLive)
            {
                return new { error = $"Refusing to place order on LIVE account '{account.Name}' without confirmLive=true" };
            }

            // Reject order if account is locked out by RiskGuard or by EmergencyFlatten lockout.
            if (IsAccountLocked(account.Name))
            {
                return new { error = $"Order blocked: Account {account.Name} is locked out." };
            }

            var symbol = req.GetValueOrDefault("symbol")?.ToString();
            var actionStr = req.GetValueOrDefault("action")?.ToString();
            var orderTypeStr = req.GetValueOrDefault("orderType")?.ToString() ?? "Market";
            var quantity = Convert.ToInt32(req.GetValueOrDefault("quantity", 1));

            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(actionStr))
                return new { error = "symbol and action required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            var orderAction = actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase) ? OrderAction.Buy : OrderAction.Sell;
            var orderType = (OrderType)Enum.Parse(typeof(OrderType), orderTypeStr, true);

            var tifStr = req.GetValueOrDefault("timeInForce")?.ToString() ?? "Day";
            var tif = (TimeInForce)Enum.Parse(typeof(TimeInForce), tifStr, true);
            
            // Map both price and limitPrice
            double limitPrice = 0;
            if (req.ContainsKey("limitPrice"))
                limitPrice = Convert.ToDouble(req["limitPrice"]);
            else if (req.ContainsKey("price"))
                limitPrice = Convert.ToDouble(req["price"]);

            double stopPrice = Convert.ToDouble(req.GetValueOrDefault("stopPrice", 0));
            string oco = req.GetValueOrDefault("ocoId")?.ToString() ?? req.GetValueOrDefault("oco")?.ToString() ?? string.Empty;
            string name = req.GetValueOrDefault("name")?.ToString() ?? "McpBridge";

            // NT8.1: CreateOrder(instrument, action, type, timeInForce, qty, limit, stop, oco, name, customOrder)
            var order = account.CreateOrder(instrument, orderAction, orderType, tif, quantity, limitPrice, stopPrice, oco, name, null);
            account.Submit(new[] { order });
            return new { status = "submitted", id = order.Id.ToString(), orderId = order.OrderId, orderName = order.Name };
        }

        // Place a proper OCO bracket: market entry + stop + target with a shared OCO GUID.
        // NT8 requires OCO orders to share the same GUID string for the OCO group.
        private object PlaceOcoOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            string reqAccount = req.GetValueOrDefault("account")?.ToString();
            Account account = null;
            if (!string.IsNullOrEmpty(reqAccount))
                account = Account.All.FirstOrDefault(a => a.Name.Equals(reqAccount, StringComparison.OrdinalIgnoreCase));
            if (account == null)
                account = Account.All.FirstOrDefault(a => a.Name == "Sim101")
                          ?? Account.All.FirstOrDefault(a => !a.Name.Equals("Backtest", StringComparison.OrdinalIgnoreCase))
                          ?? Account.All.FirstOrDefault();
            if (account == null) return new { error = "no account available" };

            // SIM-first guard: refuse order placement on a LIVE account unless confirmLive=true is explicitly provided
            // P2-38: provider only. The name clause was OR'd in FRONT of the provider test, so
            // a funded account called "SimpsonFund" classified as simulated and slipped this
            // gate without confirmLive=true. Same root cause as P1-20, different blast radius.
            bool isSim = TradeCopierEngine.IsSimulationAccount(account);
            bool confirmLive = req.ContainsKey("confirmLive") && Convert.ToBoolean(req["confirmLive"]);
            if (!isSim && !confirmLive)
            {
                return new { error = $"Refusing to place order on LIVE account '{account.Name}' without confirmLive=true" };
            }

            if (RiskGuardAddOn.Instance != null && RiskGuardAddOn.Instance.IsAccountLocked(account.Name))
                return new { error = "Order blocked: Account " + account.Name + " is locked out by Risk Guard." };
            if (IsAccountLocked(account.Name))
                return new { error = "Order blocked: Account " + account.Name + " is locked out." };

            var symbol = req.GetValueOrDefault("symbol")?.ToString();
            var actionStr = req.GetValueOrDefault("action")?.ToString() ?? "Buy";
            var quantity = Convert.ToInt32(req.GetValueOrDefault("quantity", 1));
            var stopPrice = Convert.ToDouble(req.GetValueOrDefault("stopPrice", 0));
            var targetPrice = Convert.ToDouble(req.GetValueOrDefault("targetPrice", 0));

            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            if (stopPrice <= 0) return new { error = "stopPrice required" };
            if (targetPrice <= 0) return new { error = "targetPrice required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = "instrument not found: " + symbol };

            // Generate a proper OCO GUID (NT8 uses GUID strings for OCO groups)
            string ocoId = Guid.NewGuid().ToString();
            bool isBuy = actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase);
            var entryAction = isBuy ? OrderAction.Buy : OrderAction.Sell;
            var exitAction = isBuy ? OrderAction.Sell : OrderAction.Buy;
            string entryName = req.GetValueOrDefault("name")?.ToString() ?? "OcoEntry";

            // 1. Entry: Market order
            var entryOrder = account.CreateOrder(instrument, entryAction, OrderType.Market, TimeInForce.Day, quantity, 0, 0, string.Empty, entryName, null);

            // 2. Stop: StopMarket order (OCO linked)
            var stopOrder = account.CreateOrder(instrument, exitAction, OrderType.StopMarket, TimeInForce.Day, quantity, 0, stopPrice, ocoId, "Stop1", null);

            // 3. Target: Limit order (OCO linked)
            var targetOrder = account.CreateOrder(instrument, exitAction, OrderType.Limit, TimeInForce.Day, quantity, targetPrice, 0, ocoId, "Target1", null);

            // Submit all valid orders safely
            List<string> rejectedOrders = new List<string>();
            try
            {
                var validOrders = new[] { entryOrder, stopOrder, targetOrder }
                    .Where(o => o != null && o.OrderState != OrderState.CancelPending && o.OrderState != OrderState.Cancelled)
                    .ToArray();
                if (validOrders.Length > 0)
                {
                    account.Submit(validOrders);
                }
                // Check for rejected exit orders (NT8 may reject OCO children if no
                // position exists yet). Report the rejection so callers know the
                // bracket may not be live.
                foreach (var o in new[] { stopOrder, targetOrder })
                {
                    if (o != null && (o.OrderState == OrderState.Rejected || o.OrderState == OrderState.Cancelled))
                        rejectedOrders.Add(o.Name + " state=" + o.OrderState);
                }
            }
            catch (Exception ex)
            {
                Print(string.Format("[RiskGuard] OCO submission error: {0}", ex.Message));
            }

            return new
            {
                status = rejectedOrders.Count > 0 ? "partial_submit" : "submitted",
                ocoId = ocoId,
                entry = new { id = entryOrder.Id.ToString(), name = entryOrder.Name },
                stop = new { id = stopOrder.Id.ToString(), name = stopOrder.Name, stopPrice = stopPrice, state = stopOrder.OrderState.ToString() },
                target = new { id = targetOrder.Id.ToString(), name = targetOrder.Name, targetPrice = targetPrice, state = targetOrder.OrderState.ToString() },
                rejectedExitOrders = rejectedOrders.Count > 0 ? rejectedOrders : null,
                note = rejectedOrders.Count > 0 ? "Some exit orders were rejected (NT8 may reject OCO children without an open position). Verify position before relying on the bracket." : null
            };
        }

        private static bool OrderMatches(Order o, string key)
            => o.OrderId == key || o.Name == key || o.Id.ToString() == key;

        private object CancelOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var orderId = req.GetValueOrDefault("orderId")?.ToString();
            var ocoId = req.GetValueOrDefault("ocoId")?.ToString();

            if (!string.IsNullOrEmpty(ocoId))
            {
                int count = 0;
                foreach (Account account in Account.All)
                {
                    var toCancel = account.Orders
                        .Where(o => o.Oco == ocoId && o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled)
                        .ToList();
                    if (toCancel.Count > 0)
                    {
                        account.Cancel(toCancel);
                        count += toCancel.Count;
                    }
                }
                return new { status = "cancelled_oco", ocoId, count };
            }

            if (!string.IsNullOrEmpty(orderId))
            {
                foreach (Account account in Account.All)
                    foreach (Order order in account.Orders)
                        if (OrderMatches(order, orderId))
                        {
                            account.Cancel(new[] { order });
                            return new { status = "cancelled", orderId };
                        }
                return new { error = $"order not found: {orderId}" };
            }

            return new { error = "Either orderId or ocoId is required" };
        }

        private object ChangeOrder(string body)
        {
            var req = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var orderId = req.GetValueOrDefault("orderId")?.ToString();
            if (string.IsNullOrEmpty(orderId)) return new { error = "orderId required" };

            int quantity = req.ContainsKey("quantity") ? Convert.ToInt32(req["quantity"]) : 0;
            
            double limitPrice = -1.0;
            if (req.ContainsKey("limitPrice"))
                limitPrice = Convert.ToDouble(req["limitPrice"]);
            else if (req.ContainsKey("price"))
                limitPrice = Convert.ToDouble(req["price"]);

            double stopPrice = req.ContainsKey("stopPrice") ? Convert.ToDouble(req["stopPrice"]) : -1.0;

            foreach (Account account in Account.All)
            {
                foreach (Order order in account.Orders)
                {
                    if (OrderMatches(order, orderId))
                    {
                        order.Quantity = quantity > 0 ? quantity : order.Quantity;
                        if (limitPrice >= 0) order.LimitPrice = limitPrice;
                        if (stopPrice >= 0) order.StopPrice = stopPrice;

                        account.Change(new[] { order });
                        return new { status = "modified", orderId, quantity = order.Quantity, limitPrice = order.LimitPrice, stopPrice = order.StopPrice };
                    }
                }
            }
            return new { error = $"order not found: {orderId}" };
        }

        private object CancelAllOrders()
        {
            int count = 0;
            foreach (Account account in Account.All)
            {
                var toCancel = account.Orders
                    .Where(o => o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled).ToList();
                if (toCancel.Count > 0) { account.Cancel(toCancel); count += toCancel.Count; }
            }
            return new { status = "cancelled", count };
        }

        private object ClosePosition(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : (JsonConvert.DeserializeObject<Dictionary<string, object>>(body) ?? new Dictionary<string, object>());
            var symbol = req.GetValueOrDefault("symbol")?.ToString();
            if (string.IsNullOrEmpty(symbol)) symbol = "ALL";

            var reqAccount = req.GetValueOrDefault("account")?.ToString();

            int cancelledOrdersCount = 0;
            bool positionClosed = false;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return new { error = "no WPF dispatcher (NT8 UI down?)" };
            dispatcher.Invoke(() =>
            {
                foreach (Account account in Account.All)
                {
                    if (!string.IsNullOrEmpty(reqAccount) && !account.Name.Equals(reqAccount, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rootSymbol = symbol.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? "" : symbol.Split(' ')[0];
                    bool filterBySymbol = !string.IsNullOrEmpty(rootSymbol);

                    // 1. Cancel working orders for the requested symbol only
                    var toCancel = account.Orders
                        .Where(o => o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled
                                    && (!filterBySymbol || (o.Instrument != null && o.Instrument.FullName.StartsWith(rootSymbol, StringComparison.OrdinalIgnoreCase))))
                        .ToList();
                    if (toCancel.Count > 0)
                    {
                        try { account.Cancel(toCancel); } catch {}
                        cancelledOrdersCount += toCancel.Count;
                    }

                    // 2. Flatten active positions for the requested symbol only
                    foreach (Position pos in account.Positions)
                    {
                        if (pos.Instrument == null || pos.MarketPosition == MarketPosition.Flat) continue;
                        if (filterBySymbol && !pos.Instrument.FullName.StartsWith(rootSymbol, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            account.Flatten(new[] { pos.Instrument });
                            positionClosed = true;
                        }
                        catch
                        {
                            var closeAction = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                            var closeOrder = account.CreateOrder(pos.Instrument, closeAction, OrderType.Market, TimeInForce.Day, pos.Quantity, 0, 0, string.Empty, "McpClosePosition", null);
                            account.Submit(new[] { closeOrder });
                            positionClosed = true;
                        }
                    }
                }
            });

            return new { status = "flattened", symbol, positionClosed, cancelledOrdersCount };
        }

        private static readonly HashSet<string> _subscribedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _subLock = new object();

        private void EnsureSubscribed(Instrument instrument)
        {
            lock (_subLock)
            {
                if (!_subscribedSymbols.Contains(instrument.FullName))
                {
                    instrument.MarketData.Update += (sender, args) => {};
                    _subscribedSymbols.Add(instrument.FullName);
                    System.Threading.Thread.Sleep(200); // Give it a moment to initialize data stream
                }
            }
        }

        private object GetQuote(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            EnsureSubscribed(instrument);

            try
            {
                var md = instrument.MarketData;
                if (md == null) return new { symbol = instrument.FullName, error = "no market data (not subscribed)" };
                return new
                {
                    symbol = instrument.FullName,
                    last = md.Last?.Price ?? 0, bid = md.Bid?.Price ?? 0, ask = md.Ask?.Price ?? 0,
                    bidSize = md.Bid?.Volume ?? 0, askSize = md.Ask?.Volume ?? 0,
                    volume = md.DailyVolume?.Volume ?? 0,
                    high = md.DailyHigh?.Price ?? 0, low = md.DailyLow?.Price ?? 0,
                    time = md.Last?.Time ?? DateTime.MinValue,
                };
            }
            catch (Exception ex) { return new { symbol, error = $"no market data: {ex.Message}" }; }
        }

        private object GetBars(string symbol, string periodStr, int periodValue, int count)
        {
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            var periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), periodStr, true);
            var barsPeriod = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };

            // NT8.1: historical bars are fetched asynchronously via BarsRequest.
            string status = null;
            var done = new ManualResetEventSlim(false);
            Bars bars = null;
            using (var request = new BarsRequest(instrument, count) { BarsPeriod = barsPeriod })
            {
                request.Request((req, code, msg) =>
                {
                    status = code.ToString();
                    bars = req.Bars;
                    done.Set();
                });
                if (!done.Wait(TimeSpan.FromSeconds(30)))
                    return new { symbol, error = "bars request timed out" };

                if (bars == null || bars.Count == 0)
                    return new { symbol, period = periodStr, periodValue, status, bars = new List<object>() };

                var result = new List<object>();
                for (int i = Math.Max(0, bars.Count - count); i < bars.Count; i++)
                    result.Add(new
                    {
                        time = bars.GetTime(i), open = bars.GetOpen(i), high = bars.GetHigh(i),
                        low = bars.GetLow(i), close = bars.GetClose(i), volume = bars.GetVolume(i),
                    });
                return new { symbol, period = periodStr, periodValue, count = result.Count, bars = result };
            }
        }

        // Export historical OHLCV bars over a DATE RANGE to a CSV file (for large pulls that would be
        // impractical inline). Returns a summary + the file path/name; fetch the content via
        // GET /api/export?name=<file>. NT8 fetches missing history from the data provider on demand.
        private object ExportBars(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol");
            var periodStr = req.Str("period") ?? "Minute";
            int periodValue = req["periodValue"] != null ? (int)req["periodValue"] : 1;
            int timeoutSec = req["timeoutSec"] != null ? (int)req["timeoutSec"] : 180;
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };
            DateTime from;
            if (!DateTime.TryParse(req.Str("from"), out from)) return new { error = "from (YYYY-MM-DD) required" };
            DateTime to;
            if (!DateTime.TryParse(req.Str("to"), out to)) to = DateTime.Now;

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };
            var periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), periodStr, true);
            var bp = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };

            int pv = Math.Max(1, periodValue);
            // Continuous-contract merge policy. DoNotMerge = the resolved single contract (default).
            // MergeNonBackAdjusted = real front-month prices spliced at rolls, NO price adjustment
            // (matches a research-grade continuous series). MergeBackAdjusted shifts historical prices
            // by cumulative roll gaps - do NOT use it for log-ratio / spread work.
            var mergeStr = req.Str("merge") ?? "DoNotMerge";
            MergePolicy merge;
            if (!Enum.TryParse(mergeStr, true, out merge)) merge = MergePolicy.DoNotMerge;
            // Warm the subscription (harmless for live contracts; expired ones fall back to history).
            try { var _ = instrument.MarketData; } catch { }

            string status = null;
            var done = new ManualResetEventSlim(false);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var safe = System.Text.RegularExpressions.Regex.Replace(symbol, "[^A-Za-z0-9]", "_");
            var name = $"mcp_bars_{safe}_{periodStr}{pv}.csv";
            var path = Path.Combine(Globals.UserDataDir, name);

            // Direct DATE-RANGE request (from/to are local time). This downloads exactly the window
            // from the provider - no oversized count, no client-side filtering.
            using (var request = new BarsRequest(instrument, from, to) { BarsPeriod = bp, MergePolicy = merge })
            {
                request.Request((r, code, msg) => { status = code.ToString(); done.Set(); });
                if (!done.Wait(TimeSpan.FromSeconds(timeoutSec)))
                    return new { error = $"bars request timed out after {timeoutSec}s", symbol };

                // IMPORTANT: read Bars BEFORE the BarsRequest is disposed (dispose clears them).
                var bars = request.Bars;
                if (bars == null || bars.Count == 0)
                    return new { error = "no bars returned (provider may lack history for this range)", status, symbol };

                using (var w = new StreamWriter(path, false))
                {
                    w.WriteLine("time,open,high,low,close,volume");
                    for (int i = 0; i < bars.Count; i++)
                        w.WriteLine(string.Join(",",
                            bars.GetTime(i).ToString("yyyy-MM-ddTHH:mm:ss"),
                            bars.GetOpen(i).ToString(ci), bars.GetHigh(i).ToString(ci),
                            bars.GetLow(i).ToString(ci), bars.GetClose(i).ToString(ci),
                            bars.GetVolume(i).ToString(ci)));
                }
                return new
                {
                    symbol = instrument.FullName, period = periodStr, periodValue = pv,
                    merge = merge.ToString(),
                    rows = bars.Count,
                    first = bars.GetTime(0), last = bars.GetTime(bars.Count - 1),
                    timeNote = "bar CLOSE time in NT8's configured timezone",
                    file = name, path,
                    fetch = $"GET /api/export?name={name}",
                };
            }
        }

        // Return the content of an export CSV (whitelisted to mcp_*.csv in the NT8 user-data dir),
        // so exports/signal logs are pullable over the (private) network without file access.
        private object ReadExportFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new { error = "name required" };
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.StartsWith("mcp_") || !name.EndsWith(".csv"))
                return new { error = "only mcp_*.csv export files are readable" };
            var path = Path.Combine(Globals.UserDataDir, name);
            if (!File.Exists(path)) return new { error = $"not found: {name}" };
            return new { name, bytes = new FileInfo(path).Length, content = File.ReadAllText(path) };
        }

        private object SearchInstruments(string query)
        {
            if (string.IsNullOrEmpty(query)) return new List<object>();
            var results = new List<object>();
            // NT8.1: Instrument.All is the set of known/loaded instruments.
            foreach (var inst in Instrument.All
                .Where(i => i.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(20))
                results.Add(new
                {
                    name = inst.FullName, symbol = inst.MasterInstrument?.Name ?? inst.FullName,
                    exchange = inst.Exchange.ToString(), type = inst.MasterInstrument?.InstrumentType.ToString(),
                });
            return results;
        }

        // - MCP Feature Expansion Handlers -

        private object InspectStrategy(string name)
        {
            try
            {
                var asmList = AppDomain.CurrentDomain.GetAssemblies();
                var allStrategyTypes = new List<Type>();
                foreach (var asm in asmList)
                {
                    try
                    {
                        var types = asm.GetTypes().Where(t => t != null && t.IsClass && !t.IsAbstract
                            && !t.Name.StartsWith("<") && !t.Name.Contains("XmlSerialization")
                            && (t.Name.EndsWith("Strategy") || (t.Namespace != null && t.Namespace.Contains("Strategies"))));
                        allStrategyTypes.AddRange(types);
                    }
                    catch {}
                }

                if (string.IsNullOrEmpty(name) || name.Equals("LIST", StringComparison.OrdinalIgnoreCase))
                {
                    var names = allStrategyTypes.Select(t => t.Name).Distinct().OrderBy(n => n).ToList();
                    return new { success = true, count = names.Count, strategies = names };
                }

                Type strategyType = allStrategyTypes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (strategyType == null)
                {
                    var names = allStrategyTypes.Select(t => t.Name).Distinct().Take(10).ToList();
                    return new { error = $"Strategy '{name}' not found. Available strategies: {string.Join(", ", names)}" };
                }

                var props = new List<object>();
                foreach (var prop in strategyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    // Include inherited StrategyBase properties — these ARE the user-settable inputs
                    // (Qty, StopLoss, TakeProfit, AllowLong, etc.) that /api/strategy/param sets.

                    string desc = "";
                    var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), true).FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
                    if (descAttr != null) desc = descAttr.Description;

                    // Check if this property is a NinjaScript input (decorated with NinjaScriptProperty)
                    bool isInput = prop.IsDefined(typeof(NinjaTrader.NinjaScript.NinjaScriptPropertyAttribute), true);

                    props.Add(new
                    {
                        name = prop.Name,
                        type = prop.PropertyType.Name,
                        description = desc,
                        canWrite = prop.CanWrite,
                        isInput = isInput
                    });
                }

                // Also enumerate fields decorated with NinjaScriptProperty (some inputs are fields)
                var inputs = new List<object>();
                foreach (var field in strategyType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    bool isInput = field.IsDefined(typeof(NinjaTrader.NinjaScript.NinjaScriptPropertyAttribute), true);
                    if (isInput)
                        inputs.Add(new { name = field.Name, type = field.FieldType.Name });
                }

                return new { success = true, strategy = strategyType.FullName, properties = props, inputs = inputs };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private object GetDiagnosticLogs(string tab, int maxLines)
        {
            var logs = new List<string>();
            try
            {
                string logDir = Path.Combine(Globals.UserDataDir, "RiskGuard");
                string interventionsFile = Path.Combine(logDir, "interventions.jsonl");
                if (File.Exists(interventionsFile))
                {
                    var lines = File.ReadAllLines(interventionsFile);
                    logs.AddRange(lines.Skip(Math.Max(0, lines.Length - maxLines)));
                }

                string traceLog = Path.Combine(Globals.UserDataDir, "trace.log");
                if (File.Exists(traceLog))
                {
                    var traceLines = File.ReadAllLines(traceLog);
                    logs.AddRange(traceLines.Skip(Math.Max(0, traceLines.Length - maxLines)));
                }

                return new { success = true, tab, count = logs.Count, logs };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        private void SafeDispatch(Action action, int timeoutMs = 5000)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            var task = dispatcher.InvokeAsync(action);
            if (!task.Task.Wait(timeoutMs))
            {
                Log("Dispatcher action timed out after " + timeoutMs + "ms", LogLevel.Warning);
            }
        }

        private object ListCharts()
        {
            var charts = ListOpenCharts();
            return new { success = true, count = charts.Count, charts };
        }

        private object ChartDiagnostics()
        {
            var diag = new Dictionary<string, object>();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            diag["dispatcher_null"] = dispatcher == null;
            diag["dispatcher_checkaccess"] = dispatcher?.CheckAccess();

            var charts = ListOpenCharts();
            diag["openCharts"] = charts;
            diag["openChartCount"] = charts.Count;

            return new { success = true, diag };
        }

        private object CaptureChart(string symbol)
        {
            string base64Image = null;
            Exception errorEx = null;

            System.Windows.Window targetWindow = null;

            // Discover the target window on the UI thread but do not render yet.
            SafeDispatch(() =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(symbol))
                        targetWindow = FindChartWindow(symbol);
                    if (targetWindow == null)
                        targetWindow = FindAnyChartWindow();
                }
                catch (Exception ex) { errorEx = ex; }
            }, 5000);

            if (errorEx != null) return new { error = errorEx.Message, stack = errorEx.StackTrace };
            if (targetWindow == null) return new { error = "No active chart control found to capture." };

            // Activate and render on the window's own dispatcher to satisfy WPF thread-affinity.
            var dispatcher = targetWindow.Dispatcher;
            if (dispatcher == null) dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                try { (base64Image, errorEx) = RenderChartWindow(targetWindow, symbol); }
                catch (Exception ex) { errorEx = ex; }
            }
            else
            {
                var t = dispatcher.InvokeAsync(() => RenderChartWindow(targetWindow, symbol));
                if (!t.Task.Wait(5000))
                    return new { error = "Dispatcher action timed out after 5000ms" };
                (base64Image, errorEx) = t.Task.Result;
            }

            if (errorEx != null) return new { error = errorEx.Message, stack = errorEx.StackTrace };
            if (string.IsNullOrEmpty(base64Image)) return new { error = "No active chart control found to capture." };

            return new { success = true, symbol, format = "png", base64 = base64Image };
        }

        private (string, Exception) RenderChartWindow(System.Windows.Window targetWindow, string symbol)
        {
            try
            {
                ActivateChartWindow(targetWindow);
                System.Threading.Thread.Sleep(50);

                // Re-discover an arranged ChartControl for the active tab.
                var chartControl = FindChartControlInWindow(targetWindow, symbol);
                var fe = chartControl as System.Windows.FrameworkElement;

                // Primary path: Win32 PrintWindow on the chart's HWND. This works for
                // Direct2D/WPF interop and does not require the calling thread to own the
                // visual tree.
                var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(targetWindow).Handle);
                if (hwndSource != null)
                {
                    var hwnd = hwndSource.Handle;
                    var win32Bmp = CaptureWindowHwnd(hwnd);
                    if (!string.IsNullOrEmpty(win32Bmp)) return (win32Bmp, null);
                }

                // Fallback 1: NT8's built-in screenshot (works when called on the correct
                // thread and may include panel styling).
                var chartWindow = targetWindow as NinjaTrader.Gui.Chart.Chart;
                if (chartWindow != null)
                {
                    try
                    {
                        var bmp = chartWindow.GetScreenshot(NinjaTrader.NinjaScript.ShareScreenshotType.Chart, fe ?? targetWindow);
                        if (bmp != null)
                        {
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                            using (var ms = new System.IO.MemoryStream())
                            {
                                encoder.Save(ms);
                                return (Convert.ToBase64String(ms.ToArray()), null);
                            }
                        }
                    }
                    catch (InvalidOperationException) { }
                }

                // Fallback 2: WPF RenderTargetBitmap (works for pure WPF visuals).
                if (fe == null || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                    fe = targetWindow;

                int width = (int)(fe.ActualWidth > 0 ? fe.ActualWidth : 1280);
                int height = (int)(fe.ActualHeight > 0 ? fe.ActualHeight : 720);
                if (width <= 0 || height <= 0) { width = 1280; height = 720; }

                var fallbackBmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                fallbackBmp.Render(fe);

                var fallbackEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                fallbackEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fallbackBmp));
                using (var ms = new System.IO.MemoryStream())
                {
                    fallbackEncoder.Save(ms);
                    return (Convert.ToBase64String(ms.ToArray()), null);
                }
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }

        // Capture a WPF/Win32 window by HWND using PrintWindow. Works for Direct2D
        // surfaces and does not require the caller to be on the UI thread that owns the
        // visual tree. Returns base64 PNG or null on failure.
        private static string CaptureWindowHwnd(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            const int DESKTOPHORZRES = 118;
            const int HORZRES = 8;

            IntPtr hdcWindow = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                var rect = new System.Windows.Rect();
                if (!NativeGetWindowRect(hWnd, out rect)) return null;
                int width = (int)rect.Width;
                int height = (int)rect.Height;
                if (width <= 0 || height <= 0) return null;

                hdcWindow = GetWindowDC(hWnd);
                if (hdcWindow == IntPtr.Zero) return null;

                hdcMem = CreateCompatibleDC(hdcWindow);
                if (hdcMem == IntPtr.Zero) return null;

                int scale = 1;
                try
                {
                    int logical = GetDeviceCaps(hdcWindow, HORZRES);
                    int physical = GetDeviceCaps(hdcWindow, DESKTOPHORZRES);
                    if (logical > 0) scale = physical / logical;
                    if (scale < 1) scale = 1;
                }
                catch { }

                hBitmap = CreateCompatibleBitmap(hdcWindow, width * scale, height * scale);
                if (hBitmap == IntPtr.Zero) return null;

                hOld = SelectObject(hdcMem, hBitmap);
                bool ok = PrintWindow(hWnd, hdcMem, 3); // PW_RENDERFULLCONTENT
                if (!ok) ok = PrintWindow(hWnd, hdcMem, 2); // PW_CLIENTONLY
                if (!ok) ok = PrintWindow(hWnd, hdcMem, 0);
                if (!ok) return null;

                if (scale > 1)
                {
                    // Create a correctly-sized final bitmap and scale down.
                    var finalDc = CreateCompatibleDC(hdcWindow);
                    var finalBmp = CreateCompatibleBitmap(hdcWindow, width, height);
                    var finalOld = SelectObject(finalDc, finalBmp);
                    NativeStretchBlt(finalDc, 0, 0, width, height, hdcMem, 0, 0, width * scale, height * scale, 0x00CC0020);
                    SelectObject(finalDc, finalOld);
                    DeleteDC(finalDc);
                    DeleteObject(hBitmap);
                    hBitmap = finalBmp;
                }

                var bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmpSource));
                using (var ms = new System.IO.MemoryStream())
                {
                    encoder.Save(ms);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcMem, hOld);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                if (hdcWindow != IntPtr.Zero) ReleaseDC(hWnd, hdcWindow);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeGetWindowRect(IntPtr hWnd, out System.Windows.Rect lpRect);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool NativeStretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest,
            IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);

        // Return the first open chart window we can find. Must run on the WPF dispatcher.
        private System.Windows.Window FindAnyChartWindow()
        {
            System.Windows.Window found = null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return null;
            dispatcher.Invoke((Action)(() =>
            {
                try
                {
                    foreach (System.Windows.Window win in System.Windows.Application.Current.Windows)
                    {
                        if (win == null) continue;
                        var n = win.GetType().Name;
                        if (n.Contains("Chart") || n.Contains("ControlControl")) { found = win; return; }
                    }

                    System.Collections.IEnumerable windows = GetStaticMember(typeof(NinjaTrader.Core.Globals), "AllWindows") as System.Collections.IEnumerable;
                    if (windows != null)
                        foreach (var w in windows)
                        {
                            if (w == null) continue;
                            var wType = w.GetType();
                            if (wType.FullName.Contains("Chart") || wType.Name.Contains("Chart"))
                            {
                                var win = w as System.Windows.Window;
                                if (win != null) { found = win; return; }
                            }
                        }
                }
                catch { }
            }));
            return found;
        }

        // Find a chart window whose instrument matches the requested symbol. Must run on the WPF dispatcher.
        private System.Windows.Window FindChartWindow(string symbol)
        {
            var want = Instrument.GetInstrument(symbol);
            string wantName = want?.FullName;
            string wantMaster = want?.MasterInstrument?.Name;
            if (string.IsNullOrEmpty(wantName) && string.IsNullOrEmpty(wantMaster)) return null;

            System.Windows.Window found = null;
            var appDispatcher = System.Windows.Application.Current?.Dispatcher;
            if (appDispatcher == null) return null;

            // (1) Enumerate chart windows on the app dispatcher (thread 1) —
            // Globals.AllWindows and Application.Current.Windows are safe to read
            // from the app dispatcher.
            var chartWindows = new List<System.Windows.Window>();
            appDispatcher.Invoke((Action)(() =>
            {
                try
                {
                    System.Collections.IEnumerable windows = GetStaticMember(typeof(NinjaTrader.Core.Globals), "AllWindows") as System.Collections.IEnumerable;
                    if (windows == null)
                    {
                        try { windows = System.Windows.Application.Current.Windows; } catch { }
                    }
                    if (windows == null) return;
                    foreach (var w in windows)
                    {
                        if (w == null) continue;
                        var wType = w.GetType();
                        if (!wType.FullName.Contains("Chart") && !wType.Name.Contains("Chart")) continue;
                        var win = w as System.Windows.Window;
                        if (win != null) chartWindows.Add(win);
                    }
                }
                catch { }
            }));

            // (2) Inspect each chart window on its OWN dispatcher (thread 18/19)
            // — accessing ChartControl.Instrument from thread 1 throws the
            // cross-thread exception.
            foreach (var win in chartWindows)
            {
                if (win == null) continue;
                var winDispatcher = (win as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                if (winDispatcher == null) continue;
                bool matched = false;
                winDispatcher.Invoke((Action)(() =>
                {
                    try
                    {
                        var controls = new List<object>();
                        CollectChartControlsFromWindow(win, controls);
                        foreach (var c in controls)
                        {
                            var cInstr = GetMember(c, "Instrument") as Instrument;
                            if (InstrumentMatches(cInstr, wantName)
                                || (wantMaster != null && InstrumentMatches(cInstr, wantMaster)))
                            { found = win; matched = true; return; }
                        }
                    }
                    catch { }
                }));
                if (matched) break;
            }
            return found;
        }

        private void ActivateChartWindow(System.Windows.Window win)
        {
            if (win == null) return;
            try
            {
                if (win.WindowState == System.Windows.WindowState.Minimized)
                    win.WindowState = System.Windows.WindowState.Normal;
                win.Activate();
                win.Focus();
            }
            catch { }

            // For tabbed charts, ensure the matching tab is selected. The private field is
            // named "tabControl" on NinjaTrader.Gui.Chart.Chart.
            try
            {
                var tabControl = GetMember(win, "tabControl");
                var items = GetMember(tabControl, "Items") as System.Collections.IEnumerable;
                var selected = GetMember(tabControl, "SelectedItem");
                if (items != null && selected != null)
                {
                    // Already has a selected item; focus it if it has a selectable property.
                    var selMethod = tabControl.GetType().GetMethod("set_SelectedItem",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (selMethod != null) selMethod.Invoke(tabControl, new[] { selected });
                }
            }
            catch { }

            // Give the window a chance to render.
            try
            {
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }
            catch { }
        }

        private object FindChartControlInWindow(System.Windows.Window win, string symbol)
        {
            var controls = new List<object>();
            CollectChartControlsFromWindow(win, controls);

            // Prefer an actually-arranged control (non-zero ActualWidth).
            foreach (var c in controls)
            {
                var fe = c as System.Windows.FrameworkElement;
                if (fe != null && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                    return c;
            }

            // If symbol provided, prefer the matching instrument.
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var want = Instrument.GetInstrument(symbol);
                string wantName = want?.FullName;
                string wantMaster = want?.MasterInstrument?.Name;
                foreach (var c in controls)
                {
                    var cInstr = GetMember(c, "Instrument") as Instrument;
                    if (InstrumentMatches(cInstr, wantName)
                        || (wantMaster != null && InstrumentMatches(cInstr, wantMaster)))
                        return c;
                }
            }

            return controls.FirstOrDefault();
        }

        private void CollectChartControlsFromWindow(object win, List<object> controls)
        {
            if (win == null) return;
            // ActiveChartControl and tabControl are DispatcherObject properties that
            // throw cross-thread exceptions even inside winDispatcher.Invoke when the
            // window lives on a different thread than the caller.  Skip them and rely
            // on the visual-tree walk below, which uses VisualTreeHelper (safe cross-thread).
            if (win is System.Windows.DependencyObject dcoWin)
                CollectChartControls(dcoWin, controls, new HashSet<object>(), 0);
        }

        // Return any ChartControl reachable from an open chart window.
        // Tries focused/active tab first, then unfocused tabs, then a full visual-tree walk.
        // FindAnyChartControl removed — it was dead code that accessed chart
        // windows directly from the HTTP listener thread (no dispatcher marshaling),
        // which throws cross-thread exceptions. Use FindChartControl instead, which
        // correctly marshals to each window's own dispatcher.

        private object OpenChart(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new Dictionary<string, object>() : JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
            var symbol = req.GetValueOrDefault("symbol")?.ToString();
            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };

            bool instrumentExists = false;
            string fullName = null;
            SafeDispatch(() =>
            {
                try
                {
                    var inst = Instrument.GetInstrument(symbol);
                    if (inst != null) { instrumentExists = true; fullName = inst.FullName; }
                }
                catch { }
            }, 3000);

            if (!instrumentExists) return new { error = $"instrument not found: {symbol}" };

            // NinjaTrader 8 does NOT expose a supported public API to create a chart window
            // from an AddOn. The standard way to open a chart is via the Control Center GUI
            // shortcut Ctrl+Shift+N followed by typing the symbol. We therefore record the
            // request and return a clear best-effort / platform-gap response, while we also
            // attempt to focus the Control Center so an external automation layer can drive
            // the keyboard sequence if desired.
            //
            // MainWindow is a dispatcher-owned DependencyObject; the whole interaction must
            // run on the UI thread (SafeDispatch), otherwise we hit VerifyAccess().
            bool focused = false;
            SafeDispatch(() =>
            {
                var controlCenter = System.Windows.Application.Current.MainWindow;
                if (controlCenter == null) return;
                try
                {
                    if (controlCenter.WindowState == System.Windows.WindowState.Minimized)
                        controlCenter.WindowState = System.Windows.WindowState.Normal;
                    controlCenter.Activate();
                    focused = true;
                }
                catch { }
            }, 3000);

            return new
            {
                success = true,
                symbol,
                instrumentFullName = fullName,
                opened = false,
                focused,
                status = "best_effort",
                reason = "NinjaTrader 8 does not expose a public API to open a chart from an AddOn. Use the Control Center shortcut Ctrl+Shift+N and type the symbol, or drive the keyboard sequence from an external automation layer after calling this endpoint."
            };
        }


        private object GetFillEvents(string countStr)
        {
            int c;
            int count = int.TryParse(countStr, out c) ? c : 50;
            var fills = new List<object>();

            foreach (Account account in Account.All)
            {
                foreach (Execution exec in account.Executions)
                {
                    fills.Add(new
                    {
                        account = account.Name,
                        executionId = exec.ExecutionId,
                        orderId = exec.Order != null ? exec.Order.Id.ToString() : "",
                        instrument = exec.Instrument != null ? exec.Instrument.FullName : "",
                        quantity = exec.Quantity,
                        price = exec.Price,
                        marketPosition = exec.MarketPosition.ToString(),
                        time = exec.Time
                    });
                }
            }

            var result = fills.Skip(Math.Max(0, fills.Count - count)).ToList();
            return new { success = true, count = result.Count, fills = result };
        }

        // - v1.4.0 Expansion Endpoints -

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lockoutExpiry =
            new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Check both RiskGuard and the local EmergencyFlatten lockout.
        private bool IsAccountLocked(string accountName)
        {
            if (RiskGuardAddOn.Instance != null && RiskGuardAddOn.Instance.IsAccountLocked(accountName))
                return true;
            DateTime expiry;
            if (_lockoutExpiry.TryGetValue(accountName, out expiry) && DateTime.UtcNow < expiry)
                return true;
            return false;
        }

        private object EmergencyFlatten(string body)
        {
            JObject req;
            try
            {
                req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            }
            catch (Exception ex)
            {
                Log($"[EMERGENCY FLATTEN] Invalid JSON body: {ex.Message}", LogLevel.Error);
                return new { success = false, error = "Invalid JSON body" };
            }

            string accountFilter = req.Str("account");
            int lockoutMinutes = req["lockoutMinutes"] != null ? (int)req["lockoutMinutes"] : 60;
            string key = req.Str("idempotencyKey") ?? Guid.NewGuid().ToString();

            var errors = new List<string>();
            int cancelled = 0;
            int residualCancelled = 0;
            int flattened = 0;

            var activeStates = new[]
            {
                OrderState.Working,
                OrderState.Submitted,
                OrderState.Accepted,
                OrderState.ChangePending,
                OrderState.PartFilled
            };

            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    errors.Add("No UI dispatcher available.");
                    return new { success = false, actionId = key, cancelledOrders = 0, residualCancelled = 0, flattenedAccounts = 0, lockoutMinutes, errors };
                }

                dispatcher.Invoke(() =>
                {
                    var accounts = Account.All.ToList();
                    foreach (Account acc in accounts)
                    {
                        if (!string.IsNullOrEmpty(accountFilter) && !acc.Name.Equals(accountFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 1. Terminate automated strategies on this account (Snapshot to avoid live collection mutation)
                        try
                        {
                            var strategies = acc.Strategies?.ToList() ?? new List<NinjaTrader.NinjaScript.StrategyBase>();
                            foreach (NinjaTrader.NinjaScript.StrategyBase str in strategies)
                            {
                                try
                                {
                                    // Strategy.OnStateChange may touch ChartControl
                                    // during cleanup — marshal to the chart's own
                                    // dispatcher to avoid cross-thread exceptions.
                                    var strCc = GetMember(str, "ChartControl");
                                    var strDisp = (strCc as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                                    if (strDisp != null && !strDisp.CheckAccess())
                                        strDisp.Invoke(() => str.SetState(State.Terminated));
                                    else
                                        str.SetState(State.Terminated);
                                }
                                catch (Exception sex) { errors.Add($"[{acc.Name}] Strategy terminate failed: {sex.Message}"); }
                            }
                        }
                        catch (Exception aex) { errors.Add($"[{acc.Name}] Strategy enumeration failed: {aex.Message}"); }

                        // 2. First cancel pass: all active working/partfilled orders
                        var firstOrders = acc.Orders.Where(o => activeStates.Contains(o.OrderState)).ToList();
                        foreach (Order ord in firstOrders)
                        {
                            try
                            {
                                acc.Cancel(new[] { ord });
                                cancelled++;
                            }
                            catch (Exception cex) { errors.Add($"[{acc.Name}] Cancel order {ord.OrderId}: {cex.Message}"); }
                        }

                        // 3. Close open positions (Snapshot positions)
                        var positions = acc.Positions.ToList();
                        if (positions.Count > 0)
                        {
                            try
                            {
                                acc.Flatten(positions.Select(p => p.Instrument).ToList());
                                flattened++;
                            }
                            catch (Exception fex) { errors.Add($"[{acc.Name}] Flatten failed: {fex.Message}"); }
                        }

                        // 4. Second cancel pass for residual bracket/OCO orders
                        var residualOrders = acc.Orders.Where(o => activeStates.Contains(o.OrderState)).ToList();
                        foreach (Order ord in residualOrders)
                        {
                            try
                            {
                                acc.Cancel(new[] { ord });
                                residualCancelled++;
                            }
                            catch (Exception cex) { errors.Add($"[{acc.Name}] Residual cancel order {ord.OrderId}: {cex.Message}"); }
                        }

                        // 5. Apply lockout record
                        var until = DateTime.UtcNow.AddMinutes(lockoutMinutes);
                        _lockoutExpiry.AddOrUpdate(acc.Name, until, (_, __) => until);
                    }
                });
            }
            catch (Exception dex)
            {
                errors.Add($"Dispatcher invocation failed: {dex.Message}");
            }

            int totalCancelled = cancelled + residualCancelled;
            bool success = errors.Count == 0 || (totalCancelled + flattened) > 0;
            var level = errors.Count > 0 ? LogLevel.Error : LogLevel.Warning;

            Log($"[EMERGENCY FLATTEN AUDIT-NT8-001] Key={key} Cancelled={totalCancelled} Flattened={flattened} Lockout={lockoutMinutes}m Errors={errors.Count}", level);
            return new
            {
                success,
                actionId = key,
                cancelledOrders = totalCancelled,
                firstPassCancelled = cancelled,
                residualCancelled,
                flattenedAccounts = flattened,
                lockoutMinutes,
                errors
            };
        }

        private object ChartSnapshot(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol");
            int width = req["width"] != null ? (int)req["width"] : 1280;
            int height = req["height"] != null ? (int)req["height"] : 720;
            var markers = req["markers"] as JArray;
            var timeRange = req.Str("timeRange");

            var baseCapture = CaptureChart(symbol);
            var resDict = JObject.FromObject(baseCapture);

            string imageId = "snap_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            resDict["imageId"] = imageId;
            resDict["symbol"] = symbol ?? "active";
            resDict["width"] = width;
            resDict["height"] = height;
            if (markers != null) resDict["markersCount"] = markers.Count;
            if (!string.IsNullOrEmpty(timeRange)) resDict["timeRange"] = timeRange;

            return resDict;
        }

        private object TradeChart(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var executionId = req.Str("executionId");
            var symbol = req.Str("symbol");
            var account = req.Str("account");
            int width = req["width"] != null ? (int)req["width"] : 1280;
            int height = req["height"] != null ? (int)req["height"] : 720;

            Execution targetExec = null;
            if (!string.IsNullOrEmpty(executionId))
            {
                foreach (Account acc in Account.All)
                {
                    if (!string.IsNullOrEmpty(account) && !acc.Name.Equals(account, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (Execution exec in acc.Executions)
                    {
                        if (exec.ExecutionId == executionId)
                        {
                            targetExec = exec;
                            break;
                        }
                    }
                    if (targetExec != null) break;
                }
            }

            var baseCapture = CaptureChart(symbol ?? targetExec?.Instrument?.FullName);
            var resDict = JObject.FromObject(baseCapture);

            string imageId = "trade_" + (executionId ?? Guid.NewGuid().ToString("N"));
            resDict["imageId"] = imageId;
            resDict["executionId"] = executionId;
            resDict["symbol"] = targetExec?.Instrument?.FullName ?? symbol ?? "active";
            resDict["account"] = targetExec?.Account?.Name ?? account ?? "active";
            resDict["width"] = width;
            resDict["height"] = height;
            if (targetExec != null)
            {
                resDict["price"] = targetExec.Price;
                resDict["quantity"] = targetExec.Quantity;
                resDict["marketPosition"] = targetExec.MarketPosition.ToString();
                resDict["fillTime"] = targetExec.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }

            return resDict;
        }


        private static string CopierConfigFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "copier_config.json");
        private static string PropLimitsFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "prop_limits.json");

        private object CopierConfig(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var action = req.Str("action") ?? "get";
            string leader = req.Str("leaderAccount") ?? req.Str("LeaderAccountName") ?? "Sim101";
            bool confirmLive = req["confirmLive"] != null && (bool)req["confirmLive"];
            string groupName = req.Str("groupName") ?? req.Str("GroupName");

            if (action.Equals("get_groups", StringComparison.OrdinalIgnoreCase))
            {
                TradeCopierEngine.Instance.LoadFromDisk(CopierConfigFile);
                return new { success = true, action, groups = TradeCopierEngine.Instance.GetGroups() };
            }

            if (action.Equals("set_group", StringComparison.OrdinalIgnoreCase) || action.Equals("upsert_group", StringComparison.OrdinalIgnoreCase))
            {
                bool requestedArmed = req["armedForLive"] != null ? (bool)req["armedForLive"] : (req["ArmedForLive"] != null ? (bool)req["ArmedForLive"] : false);
                var followerList = new List<string>();
                if (req["followers"] is JArray arr)
                {
                    foreach (var tok in arr) followerList.Add(tok.ToString());
                }
                else if (req["followerAccounts"] is JArray arr2)
                {
                    foreach (var tok in arr2) followerList.Add(tok.ToString());
                }

                var grp = new CopierGroup
                {
                    GroupName = groupName ?? "DefaultGroup",
                    LeaderAccountName = leader,
                    IsEnabled = req["isEnabled"] != null ? (bool)req["isEnabled"] : (req["IsEnabled"] != null ? (bool)req["IsEnabled"] : true),
                    ArmedForLive = requestedArmed && confirmLive,
                    QuantityRatio = req["quantityRatio"] != null ? (double)req["quantityRatio"] : (req["QuantityRatio"] != null ? (double)req["QuantityRatio"] : 1.0),
                    FixedLotMode = req["fixedLotMode"] != null ? (bool)req["fixedLotMode"] : (req["FixedLotMode"] != null ? (bool)req["FixedLotMode"] : false),
                    FixedLotSize = req["fixedLotSize"] != null ? (int)req["fixedLotSize"] : (req["FixedLotSize"] != null ? (int)req["FixedLotSize"] : 1),
                    AutoSymbolConversion = req["autoSymbolConversion"] != null ? (bool)req["autoSymbolConversion"] : (req["AutoSymbolConversion"] != null ? (bool)req["AutoSymbolConversion"] : true),
                    MaxPositionSize = req["maxPositionSize"] != null ? (int)req["maxPositionSize"] : (req["MaxPositionSize"] != null ? (int)req["MaxPositionSize"] : 100),
                    DailyLossLimit = req["dailyLossLimit"] != null ? (double)req["dailyLossLimit"] : (req["DailyLossLimit"] != null ? (double)req["DailyLossLimit"] : 1000.0),
                    FollowerAccounts = followerList
                };

                TradeCopierEngine.Instance.UpsertGroup(grp, confirmLive);
                TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);
                return new { success = true, action, groupName = grp.GroupName, persisted = true, group = grp };
            }

            if (action.Equals("remove_group", StringComparison.OrdinalIgnoreCase) || action.Equals("delete_group", StringComparison.OrdinalIgnoreCase))
            {
                TradeCopierEngine.Instance.RemoveGroup(groupName);
                TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);
                return new { success = true, action, groupName, removed = true };
            }

            if (action.Equals("add_follower_to_group", StringComparison.OrdinalIgnoreCase))
            {
                string follower = req.Str("followerAccount") ?? req.Str("FollowerAccountName");
                bool added = TradeCopierEngine.Instance.AddFollowerToGroup(groupName, follower);
                if (added) TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);
                return new { success = added, action, groupName, followerAccount = follower };
            }

            if (action.Equals("remove_follower_from_group", StringComparison.OrdinalIgnoreCase))
            {
                string follower = req.Str("followerAccount") ?? req.Str("FollowerAccountName");
                bool removed = TradeCopierEngine.Instance.RemoveFollowerFromGroup(groupName, follower);
                if (removed) TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);
                return new { success = removed, action, groupName, followerAccount = follower };
            }

            if (action.Equals("remove", StringComparison.OrdinalIgnoreCase) || action.Equals("clear", StringComparison.OrdinalIgnoreCase) || action.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                string follower = req.Str("followerAccount") ?? req.Str("FollowerAccountName");
                TradeCopierEngine.Instance.RemoveRelationship(leader, follower);
                TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);
                return new { success = true, action, leaderAccount = leader, followerAccount = follower, removed = true };
            }

            if (action.Equals("set", StringComparison.OrdinalIgnoreCase) || action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                bool requestedArmed = req["armedForLive"] != null ? (bool)req["armedForLive"] : (req["ArmedForLive"] != null ? (bool)req["ArmedForLive"] : false);

                var rel = new CopierRelationship
                {
                    LeaderAccountName = leader,
                    FollowerAccountName = req.Str("followerAccount") ?? req.Str("FollowerAccountName") ?? "SimCopy2",
                    IsEnabled = req["isEnabled"] != null ? (bool)req["isEnabled"] : (req["IsEnabled"] != null ? (bool)req["IsEnabled"] : true),
                    ArmedForLive = requestedArmed && confirmLive,
                    QuantityRatio = req["quantityRatio"] != null ? (double)req["quantityRatio"] : (req["QuantityRatio"] != null ? (double)req["QuantityRatio"] : 1.0),
                    FixedLotMode = req["fixedLotMode"] != null ? (bool)req["fixedLotMode"] : (req["FixedLotMode"] != null ? (bool)req["FixedLotMode"] : false),
                    FixedLotSize = req["fixedLotSize"] != null ? (int)req["fixedLotSize"] : (req["FixedLotSize"] != null ? (int)req["FixedLotSize"] : 1),
                    AutoSymbolConversion = req["autoSymbolConversion"] != null ? (bool)req["autoSymbolConversion"] : (req["AutoSymbolConversion"] != null ? (bool)req["AutoSymbolConversion"] : true),
                    MaxPositionSize = req["maxPositionSize"] != null ? (int)req["maxPositionSize"] : (req["MaxPositionSize"] != null ? (int)req["MaxPositionSize"] : 100),
                    DailyLossLimit = req["dailyLossLimit"] != null ? (double)req["dailyLossLimit"] : (req["DailyLossLimit"] != null ? (double)req["DailyLossLimit"] : 1000.0),
                    IsQuarantined = req["isQuarantined"] != null ? (bool)req["isQuarantined"] : (req["IsQuarantined"] != null ? (bool)req["IsQuarantined"] : false)
                };

                TradeCopierEngine.Instance.UpsertRelationship(rel, confirmLive);
                TradeCopierEngine.Instance.SaveToDisk(CopierConfigFile);

                bool enforcing = rel.IsEnabled && rel.ArmedForLive;
                return new { success = true, action, leaderAccount = leader, persisted = true, loaded = true, enforcing = enforcing, config = rel };
            }
            else
            {
                TradeCopierEngine.Instance.LoadFromDisk(CopierConfigFile);
                var rels = TradeCopierEngine.Instance.GetRelationships();
                var groups = TradeCopierEngine.Instance.GetGroups();
                var rel = rels.FirstOrDefault(r => r.LeaderAccountName.Equals(leader, StringComparison.OrdinalIgnoreCase)) ?? new CopierRelationship { LeaderAccountName = leader };
                bool enforcing = rel.IsEnabled && rel.ArmedForLive;
                return new { success = true, action, leaderAccount = leader, persisted = File.Exists(CopierConfigFile), loaded = true, enforcing = enforcing, config = rel, relationships = rels, groups = groups };
            }
        }

        private object PropLimits(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var action = req.Str("action") ?? "get";
            bool confirmLive = req["confirmLive"] != null && (bool)req["confirmLive"];

            if (action.Equals("set", StringComparison.OrdinalIgnoreCase) || action.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = PropFirmProtectionSuite.Instance.ParseConfig(req);
                if (cfg.ArmedForLive && !confirmLive)
                {
                    cfg.ArmedForLive = false;
                }
                PropFirmProtectionSuite.Instance.UpdateConfig(cfg, confirmLive);
                PropFirmProtectionSuite.Instance.SaveToDisk(PropLimitsFile);

                bool enforcing = cfg.ArmedForLive;
                return new { success = true, action, persisted = true, loaded = true, enforcing = enforcing, config = cfg };
            }
            else
            {
                PropFirmProtectionSuite.Instance.LoadFromDisk(PropLimitsFile);
                var cfg = PropFirmProtectionSuite.Instance.Config;
                bool enforcing = cfg != null && cfg.ArmedForLive;
                return new { success = true, action, persisted = File.Exists(PropLimitsFile), loaded = true, enforcing = enforcing, config = cfg };
            }
        }

        private object HandleLockout(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var action = req.Str("action") ?? "status";
            string acctName = req.Str("account") ?? req.Str("Account") ?? "Sim101";

            if (action.Equals("unlock", StringComparison.OrdinalIgnoreCase) || action.Equals("reset", StringComparison.OrdinalIgnoreCase) || action.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (RiskGuardAddOn.Instance != null)
                {
                    RiskGuardAddOn.Instance.UnlockAccount(acctName);
                }
                // Also clear the local EmergencyFlatten lockout
                DateTime dummy;
                _lockoutExpiry.TryRemove(acctName, out dummy);
                return new { success = true, action, account = acctName, isLockedOut = false };
            }
            // Query lockout status
            bool locked = IsAccountLocked(acctName);
            return new { success = true, action, account = acctName, isLockedOut = locked };
        }

        private object ExtractTrades(string accountFilter, string format, string fromStr, string toStr, string limitStr)
        {
            int l;
            int limit = int.TryParse(limitStr, out l) ? l : 100;
            DateTime fromDt = DateTime.MinValue, toDt = DateTime.MaxValue;
            if (!string.IsNullOrEmpty(fromStr)) DateTime.TryParse(fromStr, out fromDt);
            if (!string.IsNullOrEmpty(toStr)) DateTime.TryParse(toStr, out toDt);

            var trades = new List<object>();

            foreach (Account acc in Account.All)
            {
                if (!string.IsNullOrEmpty(accountFilter) && !acc.Name.Equals(accountFilter, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Execution exec in acc.Executions)
                {
                    if (exec.Time < fromDt || exec.Time > toDt) continue;

                    string macroTag = exec.Time.TimeOfDay >= new TimeSpan(10, 50, 0) && exec.Time.TimeOfDay <= new TimeSpan(11, 10, 0) ? "macro_1050" : "regular";

                    long latencyMs = 0;
                    if (exec.Order != null && exec.Order.Time != DateTime.MinValue)
                    {
                        latencyMs = (long)Math.Max(0, (exec.Time - exec.Order.Time).TotalMilliseconds);
                    }

                    trades.Add(new
                    {
                        account = acc.Name,
                        executionId = exec.ExecutionId,
                        orderId = exec.Order?.Id.ToString() ?? "",
                        symbol = exec.Instrument?.FullName ?? "",
                        price = exec.Price,
                        quantity = exec.Quantity,
                        marketPosition = exec.MarketPosition.ToString(),
                        time = exec.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        macroTag,
                        latencyMs,
                        commission = exec.Commission,
                        mae = (double?)null,
                        mfe = (double?)null,
                        note = "MAE/MFE require Trade objects from a backtest SystemPerformance.AllTrades; account-level Executions do not carry them."
                    });
                }
            }

            var result = trades.Take(limit).ToList();
            return new { success = true, count = result.Count, format = format ?? "json", trades = result };
        }

        private object MonteCarlo(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            int iterations = req["iterations"] != null ? Math.Max(100, Math.Min(100000, (int)req["iterations"])) : 2000;
            var method = req.Str("method") ?? "block_bootstrap";
            int blockSize = req["blockSize"] != null ? Math.Max(1, (int)req["blockSize"]) : 5;
            var sizingModel = req.Str("sizingModel") ?? "fixed_lot";
            double initialCapital = req["initialCapital"] != null ? (double)req["initialCapital"] : 50000.0;
            double ruinThreshold = req["ruinThreshold"] != null ? (double)req["ruinThreshold"] : 45000.0;

            var inputTrades = req["trades"] as JArray;
            var pnlList = new List<double>();

            if (inputTrades != null && inputTrades.Count > 0)
            {
                foreach (var t in inputTrades)
                {
                    if (t["pnl"] != null) pnlList.Add((double)t["pnl"]);
                    else if (t["netProfit"] != null) pnlList.Add((double)t["netProfit"]);
                    else if (t["realizedPnl"] != null) pnlList.Add((double)t["realizedPnl"]);
                }
            }

            if (pnlList.Count == 0)
            {
                // Previously this code synthesized a placeholder P&L (qty * direction * 10)
                // from raw executions, which produced statistically meaningless results.
                // Computing real realized P&L from execution pairs requires position tracking
                // that the AddOn does not have. Return an honest error instead.
                return new { success = false, error = "No 'trades' array supplied in the request body. The fallback that synthesizes P&L from raw account executions has been removed because it produced garbage data. Supply a 'trades' array with 'pnl' / 'netProfit' / 'realizedPnl' fields, e.g. [{\"pnl\": 120.50}, {\"pnl\": -85.00}]." };
            }

            blockSize = Math.Min(blockSize, pnlList.Count);

            double meanPnl = pnlList.Average();
            double sumSq = pnlList.Sum(p => Math.Pow(p - meanPnl, 2));
            double stdDev = pnlList.Count > 1 ? Math.Sqrt(sumSq / (pnlList.Count - 1)) : 1.0;
            if (stdDev == 0) stdDev = 1.0;

            var finalEquities = new List<double>();
            var maxDrawdowns = new List<double>();
            var maxLosingStreaks = new List<int>();
            int ruinCount = 0;

            var rnd = new Random();

            for (int i = 0; i < iterations; i++)
            {
                double capital = initialCapital;
                double peak = capital;
                double maxDd = 0.0;
                int currentStreak = 0;
                int maxStreak = 0;

                var simPath = new List<double>();

                if (method.Equals("block_bootstrap", StringComparison.OrdinalIgnoreCase) && blockSize > 1)
                {
                    int needed = pnlList.Count;
                    while (simPath.Count < needed)
                    {
                        int startIdx = rnd.Next(0, pnlList.Count - blockSize + 1);
                        for (int b = 0; b < blockSize && simPath.Count < needed; b++)
                        {
                            simPath.Add(pnlList[startIdx + b]);
                        }
                    }
                }
                else
                {
                    for (int j = 0; j < pnlList.Count; j++)
                    {
                        simPath.Add(pnlList[rnd.Next(pnlList.Count)]);
                    }
                }

                for (int j = 0; j < simPath.Count; j++)
                {
                    double basePnl = simPath[j];
                    double tradePnl = basePnl;

                    if (sizingModel.Equals("fixed_fractional", StringComparison.OrdinalIgnoreCase))
                    {
                        double scale = capital / initialCapital;
                        tradePnl = basePnl * Math.Max(0.1, scale);
                    }
                    else if (sizingModel.Equals("volatility_scaled", StringComparison.OrdinalIgnoreCase))
                    {
                        double scale = 500.0 / stdDev;
                        tradePnl = basePnl * Math.Max(0.1, scale);
                    }

                    capital += tradePnl;
                    if (capital > peak) peak = capital;
                    double dd = capital - peak;
                    if (dd < maxDd) maxDd = dd;

                    if (tradePnl < 0)
                    {
                        currentStreak++;
                        if (currentStreak > maxStreak) maxStreak = currentStreak;
                    }
                    else
                    {
                        currentStreak = 0;
                    }

                    if (capital <= ruinThreshold)
                    {
                        ruinCount++;
                        break;
                    }
                }

                finalEquities.Add(capital - initialCapital);
                maxDrawdowns.Add(maxDd);
                maxLosingStreaks.Add(maxStreak);
            }

            finalEquities.Sort();
            maxDrawdowns.Sort();
            maxLosingStreaks.Sort();

            double riskOfRuinPct = Math.Round((double)ruinCount / iterations * 100.0, 2);

            int tail5Count = Math.Max(1, (int)(iterations * 0.05));
            int tail1Count = Math.Max(1, (int)(iterations * 0.01));

            double cvar95 = Math.Round(maxDrawdowns.Take(tail5Count).Average(), 2);
            double cvar99 = Math.Round(maxDrawdowns.Take(tail1Count).Average(), 2);

            double maxDrawdownP50 = Math.Round(maxDrawdowns[(int)(iterations * 0.50)], 2);
            double maxDrawdownP95 = Math.Round(maxDrawdowns[(int)(iterations * 0.05)], 2);
            double maxDrawdownP99 = Math.Round(maxDrawdowns[(int)(iterations * 0.01)], 2);

            double p10FinalEquity = Math.Round(finalEquities[(int)(iterations * 0.10)], 2);
            double p50FinalEquity = Math.Round(finalEquities[(int)(iterations * 0.50)], 2);
            double p90FinalEquity = Math.Round(finalEquities[(int)(iterations * 0.90)], 2);

            int maxStreakP95 = maxLosingStreaks[(int)(iterations * 0.95)];
            int maxStreakP99 = maxLosingStreaks[(int)(iterations * 0.99)];

            return new
            {
                success = true,
                iterations,
                method,
                blockSize,
                sizingModel,
                sampleTradesCount = pnlList.Count,
                riskOfRuinPct,
                cvar95,
                cvar99,
                maxDrawdownP50,
                maxDrawdownP95,
                maxDrawdownP99,
                p10FinalEquity,
                p50FinalEquity,
                p90FinalEquity,
                expectedEquityMedian = p50FinalEquity,
                maxStreakP95,
                maxStreakP99
            };
        }

        private object PlaceAtmOrder(string body)
        {
            JObject req;
            try { req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body); }
            catch (Exception ex) { return new { error = "Invalid JSON body: " + ex.Message }; }

            var symbol = req.Str("symbol");
            var action = req.Str("action");
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(action))
                return new { error = "symbol and action required" };

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null)
                return new { error = "instrument not found: " + symbol };

            // Validate this is a tradable contract, not a master instrument
            if (instrument.MasterInstrument != null && instrument.FullName.Equals(instrument.MasterInstrument.Name, StringComparison.OrdinalIgnoreCase))
                return new { error = "symbol '" + symbol + "' resolves to a master instrument, not a tradable contract. Use full futures format (e.g. NQ 09-26)." };

            string reqAccount = req.Str("account");
            Account account = null;
            if (!string.IsNullOrEmpty(reqAccount))
                account = Account.All.FirstOrDefault(a => a.Name.Equals(reqAccount, StringComparison.OrdinalIgnoreCase));
            if (account == null)
                account = Account.All.FirstOrDefault(a => a.Name == "Sim101")
                          ?? Account.All.FirstOrDefault(a => !a.Name.Equals("Backtest", StringComparison.OrdinalIgnoreCase))
                          ?? Account.All.FirstOrDefault();
            if (account == null) return new { error = "no account available" };

            // P2-38: this site was worse than the other three -- name prefix ONLY, with no
            // provider test at all to fall back on.
            bool isSim = TradeCopierEngine.IsSimulationAccount(account);
            bool confirmLive = req.Bool("confirmLive");
            if (!isSim && !confirmLive)
                return new { error = "Refusing to place order on LIVE account '" + account.Name + "' without confirmLive=true" };

            if (IsAccountLocked(account.Name))
                return new { error = "Order blocked: Account " + account.Name + " is locked out." };

            int quantity = req["quantity"]?.Value<int>() ?? 1;
            double tickSize = instrument.MasterInstrument.TickSize;
            double pointValue = instrument.MasterInstrument.PointValue;

            double currentPrice = 0;
            var md = instrument.MarketData;
            if (md != null && md.Last != null)
                currentPrice = md.Last.Price;
            if (currentPrice <= 0 && md != null && md.Ask != null)
                currentPrice = md.Ask.Price;
            if (currentPrice <= 0 && md != null && md.Bid != null)
                currentPrice = md.Bid.Price;
            if (currentPrice <= 0)
                return new { error = "could not get current price for " + symbol };

            var config = new AtmStrategyConfig();
            string strategyName = req.Str("strategyName") ?? "";
            if (!string.IsNullOrEmpty(strategyName))
            {
                try { config.Type = (AtmStrategyType)Enum.Parse(typeof(AtmStrategyType), strategyName, true); }
                catch { return new { error = "unknown strategy: " + strategyName + ". Valid: FixedTicks, AtrAdaptive, SwingPoint, DrawdownShield, ScaledRunner, VolatilityScaled, SessionAdaptive, KellyOptimal" }; }
            }
            else
            {
                var profile = DynamicAtmManager.GetProfile(instrument.MasterInstrument.Name);
                config.Type = profile != null ? profile.DefaultStrategy : AtmStrategyType.FixedTicks;
            }

            if (req["stopTicks"] != null) config.StopTicks = req["stopTicks"].Value<int>();
            if (req["targetTicks"] != null) config.TargetTicks = req["targetTicks"].Value<int>();
            if (req["stopLossTicks"] != null) config.StopTicks = req["stopLossTicks"].Value<int>();
            if (req["takeProfitTicks"] != null) config.TargetTicks = req["takeProfitTicks"].Value<int>();
            if (req["atrMultiplierSL"] != null) config.AtrMultiplierSL = req["atrMultiplierSL"].Value<double>();
            if (req["atrMultiplierTP"] != null) config.AtrMultiplierTP = req["atrMultiplierTP"].Value<double>();
            if (req["atrPeriod"] != null) config.AtrPeriod = req["atrPeriod"].Value<int>();
            if (req["swingLookbackBars"] != null) config.SwingLookbackBars = req["swingLookbackBars"].Value<int>();
            if (req["swingBufferTicks"] != null) config.SwingBufferTicks = req["swingBufferTicks"].Value<int>();
            if (req["breakevenTriggerTicks"] != null) config.BreakevenTriggerTicks = req["breakevenTriggerTicks"].Value<int>();
            if (req["breakevenOffsetTicks"] != null) config.BreakevenOffsetTicks = req["breakevenOffsetTicks"].Value<int>();
            if (req["partialProfitPct"] != null) config.PartialProfitPct = req["partialProfitPct"].Value<double>();
            if (req["trailMultiplier"] != null) config.TrailMultiplier = req["trailMultiplier"].Value<double>();
            if (req["riskPerTrade"] != null) config.RiskPerTrade = req["riskPerTrade"].Value<double>();
            if (req["kellyFraction"] != null) config.KellyFraction = req["kellyFraction"].Value<double>();
            if (req["winRate"] != null) config.WinRate = req["winRate"].Value<double>();
            if (req["avgRR"] != null) config.AvgRR = req["avgRR"].Value<double>();

            var result = DynamicAtmManager.Instance.PlaceBracket(
                account, instrument, action, quantity, config, currentPrice, tickSize, pointValue);

            // Normalize to camelCase so the wire contract matches every other
            // endpoint (BracketResult is a PascalCase POCO).
            return new
            {
                status = result.Status,
                bracketId = result.BracketId,
                ocoId = result.OcoId,
                entryOrderId = result.EntryOrderId,
                stopOrderId = result.StopOrderId,
                targetOrderId = result.TargetOrderId,
                stopPrice = result.StopPrice,
                targetPrice = result.TargetPrice,
                calculatedQuantity = result.CalculatedQuantity,
                strategyName = result.StrategyName,
                note = result.Note,
                error = result.Error
            };
        }

        private object GetAtmBracketStatus(string bracketId)
        {
            if (string.IsNullOrEmpty(bracketId))
            {
                var active = DynamicAtmManager.Instance.GetActiveBrackets();
                return new { count = active.Count, brackets = active.Select(b => new
                {
                    bracketId = b.BracketId,
                    symbol = b.Symbol,
                    account = b.AccountName,
                    isLong = b.IsLong,
                    strategy = b.Config?.Type.ToString() ?? "Unknown",
                    ageSeconds = (DateTime.UtcNow - b.CreatedAt).TotalSeconds,
                    breakevenTriggered = b.BreakevenTriggered,
                    partialProfitTaken = b.PartialProfitTaken
                }).ToList() };
            }
            return DynamicAtmManager.Instance.GetBracketStatus(bracketId);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 3) DrawChartLevel — draw a REAL NinjaTrader.NinjaScript.DrawingTools object
        //    onto the chart for the requested symbol.
        //
        //    Router (unchanged):
        //      case "/api/chart/draw": return Post(method, () => DrawChartLevel(body));
        //
        //    Uses FindChartControl(instrument, out cc, out cb) to get the live ChartControl,
        //    then adds a NinjaTrader.NinjaScript.DrawingTools object to ChartObjects on the
        //    UI dispatcher. If no chart for that instrument is open, returns not_implemented.
        //
        //    Supported shapeType: "HorizontalLine" (default), "Ray", "VerticalLine", "Line",
        //    "Rectangle".  price1 required; price2 used by Rectangle and Ray.
        // ─────────────────────────────────────────────────────────────────────────
        private object DrawChartLevel(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol");
            if (string.IsNullOrWhiteSpace(symbol)) return new { error = "symbol required" };

            var tag = req.Str("tag") ?? ("mcp_draw_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            if (req["price1"] == null) return new { error = "price1 required" };
            double price1 = (double)req["price1"];
            double price2 = req["price2"] != null ? (double)req["price2"] : price1;
            DateTime? time1 = req["time1"] != null ? (DateTime)req["time1"] : (DateTime?)null;
            DateTime? time2 = req["time2"] != null ? (DateTime)req["time2"] : (DateTime?)null;

            string shapeType = (req.Str("shapeType") ?? "HorizontalLine").Trim();
            string colorStr  = req.Str("color") ?? "#FF0000";
            int width = req["width"] != null ? (int)req["width"] : 2;
            string dashStyle = req.Str("dashStyle") ?? "Solid";

            object cc, cb;
            if (!FindChartControl(symbol, out cc, out cb) || cc == null)
            {
                // List available charts by enumerating windows on the app dispatcher
                // and marshaling each window's inspection to its own dispatcher.
                var available = new List<string>();
                try
                {
                    var appDisp = System.Windows.Application.Current?.Dispatcher;
                    if (appDisp != null)
                    {
                        var chartWins = new List<System.Windows.Window>();
                        appDisp.Invoke((Action)(() =>
                        {
                            var windows = GetStaticMember(typeof(NinjaTrader.Core.Globals), "AllWindows") as System.Collections.IEnumerable;
                            if (windows == null) { try { windows = System.Windows.Application.Current.Windows; } catch {} }
                            if (windows != null)
                                foreach (var w in windows)
                                {
                                    if (w == null) continue;
                                    var wType = w.GetType();
                                    if (!wType.FullName.Contains("Chart") && !wType.Name.Contains("Chart")) continue;
                                    chartWins.Add(w as System.Windows.Window);
                                }
                        }));
                        foreach (var win in chartWins)
                        {
                            var winDisp = (win as System.Windows.Threading.DispatcherObject)?.Dispatcher;
                            if (winDisp == null) continue;
                            winDisp.Invoke((Action)(() =>
                            {
                                var controls = new List<object>();
                                CollectChartControlsFromWindow(win, controls);
                                foreach (var c in controls)
                                {
                                    var ci = GetMember(c, "Instrument") as Instrument;
                                    if (ci != null) available.Add(ci.FullName);
                                }
                            }));
                        }
                    }
                }
                catch { }
                return new { status = "not_implemented", reason = $"no open chart found for '{symbol}'; open a chart first (nt_open_chart)", availableCharts = available };
            }

            string resultStatus = null;
            Exception drawErr = null;

            // Use the ChartControl's OWN dispatcher (each NT8 chart lives on its own
            // thread), NOT the app dispatcher.  Using the app dispatcher causes
            // "calling thread cannot access this object" because ChartControl was
            // created on thread 18/19, not thread 1.
            var chartDisp = (cc as System.Windows.Threading.DispatcherObject)?.Dispatcher;
            if (chartDisp == null) return new { status = "not_implemented", reason = "no chart dispatcher available" };

            chartDisp.Invoke((Action)(() =>
            {
                try
                {
                    var chartControl = (ChartControl)cc;
                    var chartBars    = (ChartBars)cb;
                    var bars         = chartBars.Bars;

                    // Resolve default times from the live series.
                    DateTime tLast  = bars.Count > 0 ? bars.GetTime(bars.Count - 1) : DateTime.Now;
                    DateTime tPrev  = bars.Count > 1 ? bars.GetTime(bars.Count - 2) : tLast.AddMinutes(-1);
                    DateTime tStart = time1 ?? tPrev;
                    DateTime tEnd   = time2 ?? tLast;

                    // Remove any prior object with the same tag (idempotent redraw).
                    var chartObjects = chartControl.ChartObjects;
                    for (int i = chartObjects.Count - 1; i >= 0; i--)
                    {
                        var existingTag = GetP(chartObjects[i], "Tag") as string;
                        if (existingTag == tag) chartObjects.RemoveAt(i);
                    }

                    // Parse color and dash style.
                    var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colorStr);
                    brush.Freeze();
                    var stroke = new Stroke { Brush = brush, Width = width };
                    try
                    {
                        var dashEnum = (DashStyleHelper)Enum.Parse(typeof(DashStyleHelper), dashStyle, true);
                        stroke.DashStyleHelper = dashEnum;
                    }
                    catch { }

                    DrawingTool drawTool = null;
                    string shapeLower = shapeType.ToLowerInvariant();

                    if (shapeLower == "rectangle")
                    {
                        var rect = new Rectangle();
                        rect.Tag = tag;
                        SetRectangleAnchors(rect, chartBars, price1, price2, tStart, tEnd);
                        try { var outlineStroke = stroke.Clone() as System.Windows.Media.Brush; if (outlineStroke != null) outlineStroke.Freeze(); SetP(rect, "OutlineStroke", outlineStroke ?? stroke.Clone()); } catch { }
                        try
                        {
                            var areaBrush = brush.Clone();
                            areaBrush.Opacity = 0.10;
                            areaBrush.Freeze();
                            SetP(rect, "AreaBrush", areaBrush);
                        }
                        catch { }
                        drawTool = rect;
                    }
                    else
                    {
                        var line = new Line { Tag = tag };
                        object lineType = null;
                        try
                        {
                            var ltEnum = typeof(Line).GetNestedType("ChartLineType", BindingFlags.NonPublic);
                            if (ltEnum != null && ltEnum.IsEnum)
                            {
                                var enumName = shapeLower switch
                                {
                                    "ray"          => "Ray",
                                    "verticalline" => "VerticalLine",
                                    "line"         => "Linear",
                                    _              => "HorizontalLine"
                                };
                                lineType = Enum.Parse(ltEnum, enumName, true);
                            }
                        }
                        catch { }
                        if (lineType != null)
                            try { SetP(line, "LineType", lineType); } catch { }

                        // Build anchors.  For HorizontalLine/Ray/Line we use two chart anchors;
                        // for VerticalLine both anchors share the same time and span price1..price2.
                        var startAnchor = new ChartAnchor
                        {
                            Price       = price1,
                            Time        = tStart,
                            DrawingTool = line,
                            BarsAgo     = 0
                        };
                        var endAnchor = new ChartAnchor
                        {
                            Price       = shapeLower == "verticalline" ? price2 : price1,
                            Time        = tEnd,
                            DrawingTool = line,
                            BarsAgo     = 0
                        };

                        line.StartAnchor = startAnchor;
                        line.EndAnchor   = endAnchor;
                        line.Stroke      = stroke;
                        drawTool = line;
                    }

                    if (drawTool != null)
                    {
                        chartObjects.Add(drawTool);
                        chartControl.InvalidateVisual();
                        resultStatus = "drawn";
                    }
                }
                catch (Exception ex) { drawErr = ex; }
            }));

            if (drawErr != null)
                return new { status = "not_implemented", reason = "draw failed: " + drawErr.Message };
            if (resultStatus != "drawn")
                return new { status = "not_implemented", reason = "chart present but drawing object could not be added" };

            return new { success = true, symbol, tag, shapeType, price1, price2, color = colorStr, width, dashStyle, status = "drawn" };
        }

        // Helper: set a Rectangle's two corner anchors.  Rectangle exposes the anchors through
        // its base type; we use the public properties directly where visible and fall back to
        // reflection if NT8's API surface changes in a future build.
        private static void SetRectangleAnchors(Rectangle rect, ChartBars chartBars, double price1, double price2, DateTime time1, DateTime time2)
        {
            var start = GetP(rect, "StartAnchor") as ChartAnchor ?? new ChartAnchor();
            var end   = GetP(rect, "EndAnchor")   as ChartAnchor ?? new ChartAnchor();

            start.Price       = price1;
            start.Time        = time1;
            start.DrawingTool = rect;
            start.BarsAgo     = 0;

            end.Price       = price2;
            end.Time        = time2;
            end.DrawingTool = rect;
            end.BarsAgo     = 0;

            try { rect.StartAnchor = start; } catch { }
            try { rect.EndAnchor   = end; }   catch { }
            try { SetP(rect, "StartAnchor", start); } catch { }
            try { SetP(rect, "EndAnchor",   end); }   catch { }
        }
                
        // ─────────────────────────────────────────────────────────────────────────
        // 1) GetIndicatorValues — compute common NT8 indicator values from fetched Bars
        //
        //    Router (unchanged):
        //      case "/api/indicator/values": return GetIndicatorValues(
        //          query["symbol"], query["indicatorName"], query["period"], query["barsBack"]);
        //
        //    Why direct calculation: Hosting a real NinjaScript indicator outside a
        //    chart/strategy engine requires driving the NinjaScriptBase lifecycle
        //    (SetState, InitializeBars, OnBarUpdate, ...). That is fragile and can
        //    crash NT8 when invoked from an AddOn HTTP thread. Instead, we fetch the
        //    real Bars history via BarsRequest and compute the common built-ins
        //    (SMA, EMA, RSI, ATR) directly from Close/High/Low. Results match the
        //    standard NT8 formulas and are safe to call from the bridge.
        // ─────────────────────────────────────────────────────────────────────────
        private object GetIndicatorValues(string symbol, string indicatorName, string periodStr, string barsBackStr)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return new { error = "symbol required" };
            if (string.IsNullOrWhiteSpace(indicatorName))
                return new { error = "indicatorName required (e.g. SMA, EMA, RSI, ATR)" };

            int p;
            int period   = int.TryParse(periodStr,   out p)  ? Math.Max(1, p)  : 14;
            int bb;
            int barsBack = int.TryParse(barsBackStr, out bb) ? Math.Max(1, bb) : 20;

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null) return new { error = $"instrument not found: {symbol}" };

            int need = barsBack + Math.Max(period * 4, 400);

            string status = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            Bars bars = null;
            var barsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 1 };

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return new { status = "not_implemented", reason = "no WPF dispatcher (NT8 UI down)" };

            disp.Invoke((Action)(() =>
            {
                using (var request = new BarsRequest(instrument, need) { BarsPeriod = barsPeriod })
                {
                    request.Request((req, code, msg) => { status = code.ToString(); bars = req.Bars; done.Set(); });
                    // Must wait INSIDE the using block — the async callback reads
                    // req.Bars after the provider returns data. Disposing the
                    // request before the callback fires returns empty/stale bars.
                    if (!done.Wait(TimeSpan.FromSeconds(30)))
                        status = "timeout";
                }
            }));
            if (status == "timeout")
                return new { status = "not_implemented", reason = "bars request timed out; no series to compute on" };
            if (bars == null || bars.Count == 0)
                return new { status = "not_implemented", reason = $"no bar data for '{symbol}' (status={status})" };

            string name = indicatorName.Trim();
            var values = new List<double>();

            switch (name.ToUpperInvariant())
            {
                case "SMA":
                    values = ComputeSma(bars, period, barsBack);
                    break;
                case "EMA":
                    values = ComputeEma(bars, period, barsBack);
                    break;
                case "RSI":
                    values = ComputeRsi(bars, period, barsBack);
                    break;
                case "ATR":
                    values = ComputeAtr(bars, period, barsBack);
                    break;
                default:
                    return new { status = "not_implemented", reason = $"indicator '{indicatorName}' is not implemented. Supported: SMA, EMA, RSI, ATR." };
            }

            if (values.Count == 0)
                return new { status = "not_implemented", reason = $"indicator '{name}' produced no values (needs more history?)" };

            return new { success = true, symbol, indicatorName = name, period, count = values.Count, values };
        }

        // Pull bar arrays from NT8 Bars. Bars.Get*(barsAgo): 0 = current/most-recent bar.
        // We return chronological arrays (oldest first) to keep indicator math simple.
        private static double[] GetCloses(Bars bars)
        {
            int n = bars.Count;
            var arr = new double[n];
            for (int i = 0; i < n; i++) arr[i] = bars.GetClose(n - 1 - i);
            return arr;
        }
        private static double[] GetHighs(Bars bars)
        {
            int n = bars.Count;
            var arr = new double[n];
            for (int i = 0; i < n; i++) arr[i] = bars.GetHigh(n - 1 - i);
            return arr;
        }
        private static double[] GetLows(Bars bars)
        {
            int n = bars.Count;
            var arr = new double[n];
            for (int i = 0; i < n; i++) arr[i] = bars.GetLow(n - 1 - i);
            return arr;
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

        private static List<double> ComputeSma(Bars bars, int period, int barsBack)
        {
            var c = GetCloses(bars);
            int n = c.Length;
            var s = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += c[i];
                if (i >= period) sum -= c[i - period];
                if (i >= period - 1) s[i] = sum / period;
                else s[i] = double.NaN;
            }
            return TakeLast(s, barsBack);
        }

        private static List<double> ComputeEma(Bars bars, int period, int barsBack)
        {
            var c = GetCloses(bars);
            int n = c.Length;
            var e = new double[n];
            double mult = 2.0 / (period + 1);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (i < period - 1)
                {
                    sum += c[i];
                    e[i] = double.NaN;
                }
                else if (i == period - 1)
                {
                    sum += c[i];
                    e[i] = sum / period;
                }
                else
                {
                    e[i] = c[i] * mult + e[i - 1] * (1 - mult);
                }
            }
            return TakeLast(e, barsBack);
        }

        private static List<double> ComputeRsi(Bars bars, int period, int barsBack)
        {
            var c = GetCloses(bars);
            int n = c.Length;
            var rsi = new double[n];
            double avgGain = 0, avgLoss = 0;
            for (int i = 1; i < n; i++)
            {
                double change = c[i] - c[i - 1];
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? -change : 0;

                if (i < period)
                {
                    avgGain += gain / period;
                    avgLoss += loss / period;
                    rsi[i] = double.NaN;
                }
                else if (i == period)
                {
                    avgGain += gain / period;
                    avgLoss += loss / period;
                    rsi[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + avgGain / avgLoss));
                }
                else
                {
                    avgGain = (avgGain * (period - 1) + gain) / period;
                    avgLoss = (avgLoss * (period - 1) + loss) / period;
                    rsi[i] = avgLoss == 0 ? 100 : 100 - (100 / (1 + avgGain / avgLoss));
                }
            }
            rsi[0] = double.NaN;
            return TakeLast(rsi, barsBack);
        }

        private static List<double> ComputeAtr(Bars bars, int period, int barsBack)
        {
            var c = GetCloses(bars);
            var h = GetHighs(bars);
            var l = GetLows(bars);
            int n = c.Length;
            var atr = new double[n];
            double avgTr = 0;
            for (int i = 1; i < n; i++)
            {
                double tr = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
                if (i < period)
                {
                    avgTr += tr / period;
                    atr[i] = double.NaN;
                }
                else if (i == period)
                {
                    avgTr += tr / period;
                    atr[i] = avgTr;
                }
                else
                {
                    avgTr = (avgTr * (period - 1) + tr) / period;
                    atr[i] = avgTr;
                }
            }
            atr[0] = double.NaN;
            return TakeLast(atr, barsBack);
        }
 

        private object ScriptExecute(string body)
        {
            if (!DevMode)
            {
                return new { error = "ScriptExecute is disabled. Enable DevMode by setting env var NT8_MCP_DEV=1 or creating mcp_dev.on in UserDataDir." };
            }

            if (string.IsNullOrEmpty(ServerToken))
            {
                return new { error = "ScriptExecute blocked: Requires an explicit NT8_MCP_TOKEN to be configured for security." };
            }

            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var snippet = req.Str("codeSnippet");
            if (string.IsNullOrWhiteSpace(snippet)) return new { error = "codeSnippet required" };

            Log("[ScriptExecute] Compiling and running C# snippet: " + snippet);

            try
            {
                string scriptClassName = "_ScriptEval_" + Math.Abs(Guid.NewGuid().GetHashCode());
                string scriptCode = $@"
using System;
using System.Linq;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Strategies
{{
    public class {scriptClassName} : Strategy
    {{
        public static object Run()
        {{
            {snippet}
        }}
    }}
}}";
                string tmpPath = Path.Combine(StrategiesDir, scriptClassName + ".cs");
                Directory.CreateDirectory(StrategiesDir);
                File.WriteAllText(tmpPath, scriptCode, new UTF8Encoding(false));

                var compileResult = CompileCore(false);
                var compJObj = JObject.FromObject(compileResult);
                bool compileSuccess = compJObj.Bool("success", false);

                if (!compileSuccess)
                {
                    try { File.Delete(tmpPath); } catch {}
                    return new { success = false, status = "compile_failed", errors = compJObj["errors"] };
                }

                var stratType = FindStrategyType(scriptClassName);
                object result = null;
                if (stratType != null)
                {
                    result = InvokeStaticM(stratType, "Run");
                }

                try { File.Delete(tmpPath); } catch {}
                return new { success = true, status = "executed", snippet, result = result?.ToString() ?? "null" };
            }
            catch (Exception ex)
            {
                return new { success = false, status = "execution_error", error = ex.Message, stack = ex.StackTrace };
            }
        }

        private void HandleSseStream(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.Add("Cache-Control", "no-cache");
                ctx.Response.Headers.Add("Connection", "keep-alive");
                ctx.Response.StatusCode = 200;

                using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
                {
                    // Send periodic heartbeats until the client disconnects.
                    while (_running)
                    {
                        var heartbeat = JsonConvert.SerializeObject(new { @event = "heartbeat", status = "connected", serverVersion = Version, timestamp = DateTime.UtcNow });
                        writer.WriteLine("data: " + heartbeat + "\n");
                        writer.Flush();
                        System.Threading.Thread.Sleep(15000); // 15s heartbeat interval
                    }
                }
            }
            catch { }
        }

        private static string ScheduledTasksFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "scheduled_tasks.json");
        private static string TradeJournalFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "trade_journal.json");
        private static string AlertsFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "alerts.json");
        private static string RiskGuardConfigFile => Path.Combine(Globals.UserDataDir, "RiskGuard", "riskguard_config.json");

        
        // ─────────────────────────────────────────────────────────────────────────
        // 2) PortfolioBacktest — REAL multi-symbol backtest + correlation + aggregate
        //
        //    Spec (nt_portfolio_backtest): run the compiled `strategy` across each of
        //    `symbols` over from->to, correlate per-symbol return series, aggregate a
        //    combined equity curve, and report portfolio net/gross/drawdown/Sharpe/PF/WR
        //    plus per-symbol breakdowns.
        //
        //    Implementation: reuse the existing single-symbol Backtest(body) driver
        //    once per symbol (it already drives the Strategy Analyzer + ExtractBacktest),
        //    then combine. Per-symbol daily returns for correlation are derived from each
        //    run's trade list (entryTime, profitCurrency) bucketed by date — time-aligned,
        //    not zipped by index.
        // ─────────────────────────────────────────────────────────────────────────
        private object PortfolioBacktest(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbolsArr = req["symbols"] as JArray;
            var symbols = symbolsArr != null
                ? symbolsArr.Select(s => s.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
                : new List<string>();
            string strategy = req.Str("strategy");
        
            if (symbols.Count == 0) return new { error = "at least one symbol required" };
            if (string.IsNullOrWhiteSpace(strategy))
                return new { status = "not_implemented", reason = "strategy is required; a portfolio backtest runs a compiled strategy per symbol" };
            if (FindStrategyType(strategy) == null)
                return new { status = "not_implemented", reason = $"strategy type not found (compiled?): {strategy}" };
        
            string period = req.Str("period") ?? "Minute";
            int periodValue = req["periodValue"] != null ? (int)req["periodValue"] : 1;
            string from = req.Str("from");
            string to   = req.Str("to");
            int perSymbolTimeoutSec = req["timeoutSec"] != null ? (int)req["timeoutSec"] : 180;
        
            // Per-symbol runs (sequential: the SA window is a single shared resource).
            var perSymbol = new List<object>();
            // date(string) -> summed P&L for that symbol that day, for correlation.
            var dailyBySymbol = new Dictionary<string, Dictionary<string, double>>();
        
            double totNet = 0, totGross = 0, totLoss = 0, totComm = 0;
            int totWinEntries = 0, totEntries = 0, ranOk = 0;
        
            foreach (var sym in symbols)
            {
                var subBody = new JObject
                {
                    ["strategy"]    = strategy,
                    ["symbol"]      = sym,
                    ["period"]      = period,
                    ["periodValue"] = periodValue,
                    ["timeoutSec"]  = perSymbolTimeoutSec,
                    ["maxTrades"]   = 100000,   // we need the full trade list to bucket daily
                };
                if (!string.IsNullOrWhiteSpace(from)) subBody["from"] = from;
                if (!string.IsNullOrWhiteSpace(to))   subBody["to"]   = to;
        
                object runResult;
                try { runResult = Backtest(subBody.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception ex) { perSymbol.Add(new { symbol = sym, error = ex.Message }); continue; }
        
                var rj = JObject.FromObject(runResult);
                if (rj["metrics"] == null)
                {
                    // Backtest returned an error/timeout object — surface it, keep going.
                    perSymbol.Add(new { symbol = sym, error = rj["error"]?.ToString() ?? rj["status"]?.ToString() ?? "no result" });
                    continue;
                }
        
                ranOk++;
                var mx = rj["metrics"];
                double net   = (double?)mx["netProfit"]   ?? 0;
                double gp    = (double?)mx["grossProfit"] ?? 0;
                double gl    = (double?)mx["grossLoss"]   ?? 0;
                double comm  = (double?)mx["totalCommission"] ?? 0;
                int entries  = (int?)mx["entries"] ?? 0;
                double wrPct = (double?)mx["entryWinRatePct"] ?? 0;
                int winEntries = (int)Math.Round(entries * wrPct / 100.0);
        
                totNet += net; totGross += gp; totLoss += gl; totComm += comm;
                totEntries += entries; totWinEntries += winEntries;
        
                // Bucket the per-entry P&L by calendar date (from the returned trade list).
                var daily = new Dictionary<string, double>();
                var trades = rj["trades"] as JArray;
                if (trades != null)
                    foreach (var t in trades)
                    {
                        var etStr = t["entryTime"]?.ToString();
                        var pc = (double?)t["profitCurrency"] ?? 0;
                        DateTime et;
                        if (DateTime.TryParse(etStr, out et))
                        {
                            var key = et.Date.ToString("yyyy-MM-dd");
                            double v;
                            daily[key] = (daily.TryGetValue(key, out v) ? v : 0) + pc;
                        }
                    }
                dailyBySymbol[sym] = daily;
        
                perSymbol.Add(new
                {
                    symbol = sym,
                    netProfit = net,
                    grossProfit = gp,
                    grossLoss = gl,
                    profitFactor = gl != 0 ? Math.Round(gp / Math.Abs(gl), 3) : (double?)null,
                    entries,
                    entryWinRatePct = wrPct,
                    maxDrawdown = (double?)mx["maxDrawdown"] ?? 0,
                    totalCommission = comm,
                });
            }
        
            if (ranOk == 0)
                return new { status = "not_implemented", reason = "no symbol produced a backtest result", perSymbol };
        
            // ── Correlation matrix on TIME-ALIGNED daily P&L (inner join on date) ──
            var syms = dailyBySymbol.Keys.ToList();
            var correlationMatrix = new Dictionary<string, double?>();
            for (int i = 0; i < syms.Count; i++)
                for (int j = i + 1; j < syms.Count; j++)
                {
                    var a = dailyBySymbol[syms[i]];
                    var b = dailyBySymbol[syms[j]];
                    var commonDates = a.Keys.Intersect(b.Keys).OrderBy(d => d).ToList();
                    string pairKey = $"{syms[i].Split(' ')[0]}_{syms[j].Split(' ')[0]}";
                    if (commonDates.Count < 2) { correlationMatrix[pairKey] = null; continue; }
        
                    var ra = commonDates.Select(d => a[d]).ToList();
                    var rb = commonDates.Select(d => b[d]).ToList();
                    double ma = ra.Average(), mb = rb.Average();
                    double cov = 0, va = 0, vb = 0;
                    for (int k = 0; k < ra.Count; k++)
                    {
                        double da = ra[k] - ma, db = rb[k] - mb;
                        cov += da * db; va += da * da; vb += db * db;
                    }
                    correlationMatrix[pairKey] = (va > 0 && vb > 0)
                        ? (double?)Math.Round(cov / (Math.Sqrt(va) * Math.Sqrt(vb)), 4)
                        : null;
                }
        
            // ── Combined portfolio equity curve from the union of all daily P&L ──
            var allDates = dailyBySymbol.Values.SelectMany(d => d.Keys).Distinct().OrderBy(d => d).ToList();
            var portDaily = allDates.Select(d =>
                dailyBySymbol.Values.Sum(sd => { double v; return sd.TryGetValue(d, out v) ? v : 0; })).ToList();
        
            // Drawdown on the cumulative dollar equity curve.
            double equity = 0, peak = 0, maxDd = 0;
            foreach (var pnl in portDaily)
            {
                equity += pnl;
                if (equity > peak) peak = equity;
                double dd = equity - peak;
                if (dd < maxDd) maxDd = dd;
            }
        
            // Portfolio Sharpe on daily P&L. Daily series => annualize by sqrt(252).
            // (The spec's sqrt(252*390) assumes per-MINUTE returns; these are per-DAY
            //  aggregates, so 252 is the correct trading-day annualization. If you
            //  instead want a per-minute Sharpe, compute on minute returns and use the
            //  instrument's true session length, NOT 390, which is RTH-equity only.)
            double? portfolioSharpe = null;
            if (portDaily.Count >= 2)
            {
                double mean = portDaily.Average();
                double sd = Math.Sqrt(portDaily.Select(r => (r - mean) * (r - mean)).Average());
                portfolioSharpe = sd > 0 ? (double?)Math.Round((mean / sd) * Math.Sqrt(252), 3) : null;
            }
        
            return new
            {
                success = true,
                runId = "pbt_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                strategy,
                symbolsRequested = symbols.Count,
                symbolsRan = ranOk,
                portfolio = new
                {
                    netProfit = Math.Round(totNet, 2),
                    grossProfit = Math.Round(totGross, 2),
                    grossLoss = Math.Round(totLoss, 2),
                    profitFactor = totLoss != 0 ? Math.Round(totGross / Math.Abs(totLoss), 3) : (double?)null,
                    totalCommission = Math.Round(totComm, 2),
                    entries = totEntries,
                    entryWinRatePct = totEntries > 0 ? Math.Round(100.0 * totWinEntries / totEntries, 1) : 0,
                    maxDrawdown = Math.Round(maxDd, 2),
                    portfolioSharpe,
                    tradingDays = portDaily.Count,
                },
                correlationMatrix,
                perSymbol,
            };
        }

        private object SyntheticData(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var scenario = req.Str("scenario") ?? "2020_covid_shock";
            var symbol = req.Str("symbol") ?? "NQ 09-26";
            int count = req["count"] != null ? (int)req["count"] : 1440;

            double basePrice = 20000.0;
            var barsRes = GetBars(symbol, "Minute", 1, 10);
            var barsObj = JObject.FromObject(barsRes);
            var barsArr = barsObj["bars"] as JArray;
            if (barsArr != null && barsArr.Count > 0 && barsArr.Last["close"] != null)
            {
                basePrice = (double)barsArr.Last["close"];
            }

            double volMult = 1.0;
            double driftPerBar = 0.0;
            double gapProb = 0.0;

            if (scenario.Contains("covid")) { volMult = 3.5; driftPerBar = -0.0003; gapProb = 0.02; }
            else if (scenario.Contains("2008") || scenario.Contains("gfc")) { volMult = 4.0; driftPerBar = -0.0005; gapProb = 0.03; }
            else if (scenario.Contains("gap")) { volMult = 2.0; driftPerBar = 0.0; gapProb = 0.05; }
            else { volMult = 2.5; driftPerBar = -0.0001; }

            var rnd = new Random();
            double price = basePrice;
            double peak = price;
            double maxDd = 0.0;

            var fileName = $"mcp_synthetic_{scenario}_{Regex.Replace(symbol, "[^A-Za-z0-9]", "_")}.csv";
            var filePath = Path.Combine(Globals.UserDataDir, fileName);

            using (var w = new StreamWriter(filePath, false))
            {
                w.WriteLine("time,open,high,low,close,volume");
                DateTime dt = DateTime.UtcNow.AddMinutes(-count);

                for (int i = 0; i < count; i++)
                {
                    double ret = (rnd.NextDouble() - 0.5) * 0.002 * volMult + driftPerBar;
                    if (rnd.NextDouble() < gapProb) ret += (rnd.NextDouble() - 0.5) * 0.02;

                    double open = price;
                    double close = Math.Max(1.0, open * (1.0 + ret));
                    double high = Math.Max(open, close) + Math.Abs(ret) * open * rnd.NextDouble();
                    double low = Math.Min(open, close) - Math.Abs(ret) * open * rnd.NextDouble();
                    long volume = rnd.Next(100, 5000);

                    price = close;
                    if (price > peak) peak = price;
                    double dd = price - peak;
                    if (dd < maxDd) maxDd = dd;

                    w.WriteLine($"{dt:yyyy-MM-ddTHH:mm:ss},{open:F2},{high:F2},{low:F2},{close:F2},{volume}");
                    dt = dt.AddMinutes(1);
                }
            }

            return new
            {
                success = true,
                scenario,
                symbol,
                generatedBars = count,
                basePrice = Math.Round(basePrice, 2),
                finalPrice = Math.Round(price, 2),
                stressMaxDrawdown = Math.Round(maxDd, 2),
                file = fileName,
                path = filePath,
                fetch = $"GET /api/export?name={fileName}"
            };
        }

        private object SignalBacktest(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol");
            var entryRule = req.Str("entryRule") ?? "sma_crossover";
            var timeframe = req.Str("timeframe") ?? "5m";

            if (string.IsNullOrEmpty(symbol)) return new { error = "symbol required" };

            // Only sma_crossover is implemented. Other rules are not supported.
            if (!entryRule.Equals("sma_crossover", StringComparison.OrdinalIgnoreCase))
                return new { error = $"entryRule '{entryRule}' is not supported. Only 'sma_crossover' is implemented." };

            var barsResult = GetBars(symbol, "Minute", 5, 500);
            var barsJObj = JObject.FromObject(barsResult);
            var barsArr = barsJObj["bars"] as JArray;
            if (barsArr == null || barsArr.Count < 50) return new { error = "insufficient bar data for signal backtest (minimum 50 bars required)" };

            var closes = barsArr.Select(b => (double)b["close"]).ToList();

            int periodShort = 10, periodLong = 30;
            var trades = new List<double>();
            bool inPosition = false;
            double entryPrice = 0;

            for (int i = periodLong; i < closes.Count; i++)
            {
                double smaShort = closes.Skip(i - periodShort + 1).Take(periodShort).Average();
                double smaLong = closes.Skip(i - periodLong + 1).Take(periodLong).Average();
                double prevSmaShort = closes.Skip(i - periodShort).Take(periodShort).Average();
                double prevSmaLong = closes.Skip(i - periodLong).Take(periodLong).Average();

                bool bullCross = prevSmaShort <= prevSmaLong && smaShort > smaLong;
                bool bearCross = prevSmaShort >= prevSmaLong && smaShort < smaLong;

                if (!inPosition && bullCross)
                {
                    inPosition = true;
                    entryPrice = closes[i];
                }
                else if (inPosition && bearCross)
                {
                    inPosition = false;
                    double pnl = closes[i] - entryPrice;
                    trades.Add(pnl);
                }
            }

            int totalTrades = trades.Count;
            int winners = trades.Count(t => t > 0);
            int losers = trades.Count(t => t < 0);
            double winRatePct = totalTrades > 0 ? Math.Round((double)winners / totalTrades * 100.0, 1) : 0.0;
            double grossProfit = trades.Where(t => t > 0).Sum();
            double grossLoss = trades.Where(t => t < 0).Sum();
            double profitFactor = grossLoss != 0 ? Math.Round(grossProfit / Math.Abs(grossLoss), 2) : 0.0;
            double netProfit = Math.Round(trades.Sum(), 2);

            return new
            {
                success = true,
                symbol,
                entryRule,
                timeframe,
                sampleBars = closes.Count,
                totalTrades,
                winners,
                losers,
                winRatePct,
                profitFactor,
                netProfit
            };
        }

        private object ScheduleTask(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var cron = req.Str("cronExpression") ?? "0 18 * * 0";
            var taskId = req.Str("taskId") ?? ("task_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            req["taskId"] = taskId;
            req["cronExpression"] = cron;
            req["status"] = "scheduled";

            lock (_scheduledTasks)
            {
                _scheduledTasks[taskId] = req;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ScheduledTasksFile));
                    File.WriteAllText(ScheduledTasksFile, JsonConvert.SerializeObject(_scheduledTasks));
                }
                catch {}
            }

            // Ensure the scheduler timer is running (starts on first task registration).
            EnsureScheduler();

            return new { success = true, taskId, cronExpression = cron, status = "scheduled", totalScheduled = _scheduledTasks.Count, note = "Task registered with the in-process scheduler. Fires the 'command' endpoint at the specified interval. Scheduler restarts on NT8 recompile." };
        }

        private object TradeJournal(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var action = req.Str("action") ?? "list";
            var id = req.Str("id") ?? Guid.NewGuid().ToString("N");

            // Always load from disk first to ensure we have the latest persisted state
            try
            {
                if (File.Exists(TradeJournalFile))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(TradeJournalFile));
                    if (loaded != null) foreach (var kv in loaded) _tradeJournal[kv.Key] = kv.Value;
                }
            }
            catch { }

            lock (_tradeJournal)
            {
                if (action.Equals("add", StringComparison.OrdinalIgnoreCase) || action.Equals("create", StringComparison.OrdinalIgnoreCase))
                {
                    _tradeJournal[id] = req;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(TradeJournalFile));
                        File.WriteAllText(TradeJournalFile, JsonConvert.SerializeObject(_tradeJournal));
                    }
                    catch {}
                }
                else if (action.Equals("delete", StringComparison.OrdinalIgnoreCase) || action.Equals("remove", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(req.Str("id")))
                        return new { error = "id required for delete action" };
                    _tradeJournal.Remove(req.Str("id"));
                    try { File.WriteAllText(TradeJournalFile, JsonConvert.SerializeObject(_tradeJournal)); } catch {}
                    return new { success = true, action, count = _tradeJournal.Count };
                }
                else if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(req.Str("id")))
                        return new { error = "id required for update action" };
                    _tradeJournal[req.Str("id")] = req;
                    try { File.WriteAllText(TradeJournalFile, JsonConvert.SerializeObject(_tradeJournal)); } catch {}
                    return new { success = true, action, count = _tradeJournal.Count };
                }
                return new { success = true, action, count = _tradeJournal.Count, entries = _tradeJournal.Values.ToList() };
            }
        }

        private object CreateAlert(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var symbol = req.Str("symbol") ?? "NQ 09-26";
            var alertId = req.Str("alertId") ?? ("alt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            req["alertId"] = alertId;
            req["symbol"] = symbol;
            req["status"] = "active";

            lock (_alerts)
            {
                _alerts[alertId] = req;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AlertsFile));
                    File.WriteAllText(AlertsFile, JsonConvert.SerializeObject(_alerts));
                }
                catch {}
            }

            // Register a price-level monitor that fires AlertCallback when the condition is met.
            // NT8's AlertCallback fires into the Alerts Log window (Control Center → New → Alerts Log).
            double priceLevel = req["price"] != null ? (double)req["price"] : 0;
            string condition = req.Str("condition") ?? "cross_above";
            string message = req.Str("message") ?? $"{symbol} {condition} {priceLevel}";

            if (priceLevel > 0)
            {
                try
                {
                    var inst = Instrument.GetInstrument(symbol);
                    if (inst != null)
                    {
                        _alertMonitors[alertId] = new AlertMonitor
                        {
                            Instrument = inst,
                            Level = priceLevel,
                            Condition = condition,
                            AlertId = alertId,
                            Message = message,
                            Triggered = false
                        };
                        return new { success = true, alertId, symbol, status = "active_with_monitor", totalAlerts = _alerts.Count, note = "Alert registered with price-level monitor. Fires NT8 AlertCallback into the Alerts Log when condition is met." };
                    }
                }
                catch { }
            }

            return new { success = true, alertId, symbol, status = "recorded", totalAlerts = _alerts.Count, note = "Alert persisted. Price-level monitor could not be registered (instrument not found or no price specified). Alert is a log entry only." };
        }

        // Alert price-level monitor
        private class AlertMonitor
        {
            public Instrument Instrument;
            public double Level;
            public string Condition; // "cross_above" or "cross_below"
            public string AlertId;
            public string Message;
            public bool Triggered;
            public double LastPrice;
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AlertMonitor> _alertMonitors =
            new System.Collections.Concurrent.ConcurrentDictionary<string, AlertMonitor>(StringComparer.OrdinalIgnoreCase);

        private object RiskGuardConfig(string body)
        {
            // GET (no body or empty body): return the current live config
            if (string.IsNullOrWhiteSpace(body))
            {
                if (RiskGuardAddOn.Instance == null)
                    return new { error = "RiskGuardAddOn not loaded" };
                return new { success = true, config = RiskGuardAddOn.Instance.Config };
            }

            var req = JObject.Parse(body);

            if (RiskGuardAddOn.Instance == null)
            {
                // Fallback: persist to riskguard_config.json (no live engine to apply to)
                string key = "global";
                lock (_riskGuardConfig)
                {
                    _riskGuardConfig[key] = req;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(RiskGuardConfigFile)); File.WriteAllText(RiskGuardConfigFile, JsonConvert.SerializeObject(_riskGuardConfig)); } catch {}
                }
                return new { success = true, status = "persisted_only", config = req, note = "RiskGuardAddOn not loaded. Config persisted to riskguard_config.json but NOT applied to a live engine." };
            }

            // Deserialize the JObject into a typed RiskConfig and apply via SaveAndReloadConfig.
            // This writes to RiskGuard/config.json (the correct file) AND reloads the live engine.
            try
            {
                // P2-41: MERGE onto the live config, never deserialize the body on its own.
                // req.ToObject<RiskConfig>() gives every omitted field its DEFAULT, and
                // SaveAndReloadConfig then wrote those defaults to disk and reloaded them. A
                // caller adding one entry to ExcludedAccounts also reset Mode to shadow,
                // MinShadowSessions to 0, EnableWindowGate to false and every StopGuard /
                // PnLRules / FirmMirror value -- destroying the live risk configuration while
                // replying "applied".
                JObject mergedJson;
                var cfg = RiskConfigMerge.Apply(RiskGuardAddOn.Instance.Config, req, out mergedJson);
                if (cfg == null)
                    return new { error = "Could not deserialize merged body to RiskConfig." };
                RiskGuardAddOn.Instance.SaveAndReloadConfig(cfg);
                // Echo the RESULT, not the request. The old reply looked identical whether the
                // fields the caller omitted survived or were flattened to their defaults.
                return new { success = true, status = "applied", config = RiskGuardAddOn.Instance.Config, requested = req, note = "Partial body merged onto the live config, written to RiskGuard/config.json and reloaded. `config` is the RESULTING live config; `requested` is what you sent." };
            }
            catch (Exception ex)
            {
                return new { error = "Failed to apply config: " + ex.Message };
            }
        }

        private object GetComplianceReport(string accountName)
        {
            double dailyPnL = 0.0;
            int totalTrades = 0;
            int maxPositionExposure = 0;
            string accName = accountName ?? "Sim101";
            double dailyLossLimit = -2500.0; // default fallback

            // Try to get the real prop-firm daily loss limit from PropFirmProtectionSuite
            try
            {
                var propConfig = PropFirmProtectionSuite.Instance?.Config;
                if (propConfig != null)
                {
                    // PropFirmProtectionConfig may have a DailyLossLimit field
                    var dll = GetMember(propConfig, "DailyLossLimit");
                    if (dll != null) dailyLossLimit = -Math.Abs(Convert.ToDouble(dll));
                }
            }
            catch { }

            bool accountFound = false;
            foreach (Account acc in Account.All)
            {
                if (!string.IsNullOrEmpty(accountName) && !acc.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase)) continue;
                accountFound = true;
                dailyPnL += AcctGet(acc, AccountItem.RealizedProfitLoss) + AcctGet(acc, AccountItem.UnrealizedProfitLoss);
                totalTrades += acc.Executions.Count;
                foreach (Position pos in acc.Positions)
                {
                    maxPositionExposure = Math.Max(maxPositionExposure, Math.Abs(pos.Quantity));
                }
            }

            if (!accountFound) return new { error = $"account '{accName}' not found" };

            string status = (dailyPnL >= dailyLossLimit) ? "COMPLIANT" : "VIOLATION_DAILY_LOSS_EXCEEDED";

            return new
            {
                success = true,
                account = accName,
                timestamp = DateTime.UtcNow,
                dailyPnL,
                totalTrades,
                maxPositionExposure,
                dailyLossLimit = dailyLossLimit,
                complianceStatus = status
            };
        }

        private object MultiAccountOrchestrator(string body)
        {
            var req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var action = req.Str("action") ?? "sync_hedge";
            var accList = req["accounts"] as JArray;
            var targetAccounts = accList != null ? accList.Select(a => a.ToString()).ToList() : Account.All.Select(a => a.Name).ToList();
            var orders = req["orders"] as JArray;
            if (orders == null || orders.Count == 0)
                return new { error = "orders array required, e.g. [{symbol,action,quantity,orderType}, ...]" };

            var results = new List<object>();
            foreach (string accName in targetAccounts)
            {
                Account account = Account.All.FirstOrDefault(a => a.Name.Equals(accName, StringComparison.OrdinalIgnoreCase));
                if (account == null) { results.Add(new { account = accName, status = "error", error = "account not found" }); continue; }

                foreach (var ord in orders)
                {
                    var symbol = ord["symbol"]?.ToString();
                    var actionStr = ord["action"]?.ToString() ?? "buy";
                    var quantity = ord["quantity"] != null ? (int)ord["quantity"] : 1;
                    var orderTypeStr = ord["orderType"]?.ToString() ?? "Market";
                    if (string.IsNullOrEmpty(symbol)) { results.Add(new { account = accName, status = "error", error = "symbol required" }); continue; }

                    var instrument = Instrument.GetInstrument(symbol);
                    if (instrument == null) { results.Add(new { account = accName, status = "error", error = "instrument not found: " + symbol }); continue; }

                    var orderAction = actionStr.Equals("sell", StringComparison.OrdinalIgnoreCase) ? OrderAction.Sell : OrderAction.Buy;
                    var orderType = orderTypeStr.Equals("Limit", StringComparison.OrdinalIgnoreCase) ? OrderType.Limit : OrderType.Market;
                    double limitPrice = ord["limitPrice"] != null ? (double)ord["limitPrice"] : 0;
                    double stopPrice = ord["stopPrice"] != null ? (double)ord["stopPrice"] : 0;

                    try
                    {
                        var order = account.CreateOrder(instrument, orderAction, orderType, TimeInForce.Day, quantity, limitPrice, stopPrice, string.Empty, "McpOrchestrator", null);
                        account.Submit(new[] { order });
                        results.Add(new { account = accName, symbol, action = actionStr, quantity, orderId = order.Id.ToString(), status = "submitted" });
                    }
                    catch (Exception ex) { results.Add(new { account = accName, symbol, status = "error", error = ex.Message }); }
                }
            }
            return new { success = true, action, targetAccounts, status = "executed", results };
        }

        // - Helpers -



        // Generic JSON store loader for persistent dictionaries.
        private static void LoadJsonStore(string path, Dictionary<string, JObject> store)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(File.ReadAllText(path));
                    if (loaded != null)
                        lock (store) { store.Clear(); foreach (var kv in loaded) store[kv.Key] = kv.Value; }
                }
            }
            catch { }
        }

        // ── Scheduler + Alert Monitor ──────────────────────────────────────────
        private System.Threading.Timer _schedulerTimer;
        private static readonly object _schedLock = new object();

        private void EnsureScheduler()
        {
            if (_schedulerTimer != null) return;
            lock (_schedLock)
            {
                if (_schedulerTimer != null) return;
                // Tick every 30s — cron resolution to the minute is sufficient.
                _schedulerTimer = new System.Threading.Timer(SchedulerTick, null, 30000, 30000);
                Log("Scheduler started (30s tick)");
            }
        }

        private void SchedulerTick(object _)
        {
            try { CheckScheduledTasks(); } catch { }
            try { CheckAlertMonitors(); } catch { }
        }

        private void CheckScheduledTasks()
        {
            if (_scheduledTasks.Count == 0) return;
            DateTime now = DateTime.Now;
            List<KeyValuePair<string, JObject>> due;
            lock (_scheduledTasks)
            {
                due = _scheduledTasks.Where(kv => IsTaskDue(kv.Value, now)).ToList();
            }
            foreach (var kv in due)
            {
                var task = kv.Value;
                var command = task.Str("command");
                if (string.IsNullOrEmpty(command)) continue;
                // Fire-and-forget loopback HTTP call to the command endpoint.
                try
                {
                    var url = "http://localhost:7890/" + command.TrimStart('/');
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Headers["Authorization"] = "Bearer " + ServerToken;
                    req.Timeout = 30000;
                    var args = task["args"]?.ToString() ?? "{}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(args);
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    using (var resp = req.GetResponse()) { }
                    // Update lastRun
                    task["lastRun"] = now.ToString("o");
                }
                catch (Exception ex)
                {
                    task["lastError"] = ex.Message;
                }
            }
        }

        // Simple interval-based scheduling: supports "interval" (seconds) or "cronExpression" (basic 5-field).
        // For cron, we evaluate minute/hour/day-of-month/month/day-of-week with * and */n support.
        private static bool IsTaskDue(JObject task, DateTime now)
        {
            // Interval-based (simpler, more reliable)
            int intervalSec;
            if (task["interval"] != null && int.TryParse(task["interval"].ToString(), out intervalSec) && intervalSec > 0)
            {
                DateTime lastRun;
                if (!DateTime.TryParse(task["lastRun"]?.ToString(), out lastRun))
                    return true; // never run → due now
                return (now - lastRun).TotalSeconds >= intervalSec;
            }

            // Cron-based (basic 5-field: minute hour dom month dow)
            var cron = task.Str("cronExpression");
            if (string.IsNullOrEmpty(cron)) return false;
            var parts = cron.Split(' ');
            if (parts.Length < 5) return false;
            DateTime lastRun2;
            DateTime.TryParse(task["lastRun"]?.ToString(), out lastRun2);
            // Check if current minute matches and hasn't been run this minute
            if (lastRun2 > now.AddMinutes(-1)) return false;
            return CronFieldMatches(parts[0], now.Minute, 0, 59)
                && CronFieldMatches(parts[1], now.Hour, 0, 23)
                && CronFieldMatches(parts[2], now.Day, 1, 31)
                && CronFieldMatches(parts[3], now.Month, 1, 12)
                && CronFieldMatches(parts[4], (int)now.DayOfWeek, 0, 6);
        }

        private static bool CronFieldMatches(string field, int value, int min, int max)
        {
            if (field == "*") return true;
            if (field.StartsWith("*/"))
            {
                int step;
                if (int.TryParse(field.Substring(2), out step) && step > 0)
                    return (value - min) % step == 0;
                return false;
            }
            int v;
            if (int.TryParse(field, out v)) return v == value;
            // Comma-separated list
            foreach (var part in field.Split(','))
            {
                if (int.TryParse(part, out v) && v == value) return true;
            }
            return false;
        }

        private void CheckAlertMonitors()
        {
            if (_alertMonitors.Count == 0) return;
            foreach (var kv in _alertMonitors.ToList())
            {
                var m = kv.Value;
                if (m.Triggered) continue;
                try
                {
                    // Get last price from the instrument's market data
                    double lastPrice = 0;
                    var md = m.Instrument?.MarketData;
                    if (md != null && md.Last != null)
                        lastPrice = md.Last.Price;
                    if (lastPrice <= 0) continue;

                    bool trigger = false;
                    if (m.Condition == "cross_above" && m.LastPrice > 0 && m.LastPrice < m.Level && lastPrice >= m.Level)
                        trigger = true;
                    else if (m.Condition == "cross_below" && m.LastPrice > 0 && m.LastPrice > m.Level && lastPrice <= m.Level)
                        trigger = true;

                    m.LastPrice = lastPrice;

                    if (trigger)
                    {
                        m.Triggered = true;
                        // Fire NT8 AlertCallback into the Alerts Log
                        try
                        {
                            NinjaTrader.NinjaScript.Alert.AlertCallback(
                                m.Instrument, this, m.AlertId, NinjaTrader.Core.Globals.Now,
                                NinjaTrader.NinjaScript.Priority.High, m.Message,
                                NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Yellow),
                                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black),
                                0);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private void WriteResponse(HttpListenerContext ctx, int code, object data)
        {
            try
            {
                if (ctx == null || ctx.Response == null) return;
                var json = JsonConvert.SerializeObject(data);
                var buffer = Encoding.UTF8.GetBytes(json);
                try { ctx.Response.Headers.Add("X-NT8-MCP-Version", Version); } catch {}
                try { ctx.Response.StatusCode = code; } catch {}
                try { ctx.Response.ContentType = "application/json"; } catch {}
                try { ctx.Response.ContentLength64 = buffer.Length; } catch {}
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
            }
            catch {}
        }


        private void Log(string message, LogLevel level = LogLevel.Information)
            => NinjaTrader.Code.Output.Process("[McpBridge] " + message, PrintTo.OutputTab1);
    }
}

public static class DictionaryExtensions
{
    public static object GetValueOrDefault(this Dictionary<string, object> dict, string key, object defaultValue = null)
        { object val; return dict.TryGetValue(key, out val) ? val : defaultValue; }
}

// Safe JObject accessors. JObject.Value<T>(string) resolves to the IEnumerable<JToken>
// extension and throws "Cannot cast JObject to JToken", so use indexer access instead.
public static class JObjectExtensions
{
    public static string Str(this Newtonsoft.Json.Linq.JObject o, string key)
    {
        var t = o?[key];
        return (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) ? null : t.ToString();
    }

    public static bool Bool(this Newtonsoft.Json.Linq.JObject o, string key, bool dflt = false)
    {
        var t = o?[key];
        if (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) return dflt;
        try { return (bool)t; } catch { return dflt; }
    }
}
