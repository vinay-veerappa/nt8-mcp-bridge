using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Code;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
    public enum AtmStrategyType
    {
        FixedTicks,
        AtrAdaptive,
        SwingPoint,
        DrawdownShield,
        ScaledRunner,
        VolatilityScaled,
        SessionAdaptive,
        KellyOptimal
    }

    public class AtmInstrumentProfile
    {
        public string Symbol { get; set; }
        public double TickSize { get; set; }
        public double PointValue { get; set; }
        public double DefaultATR { get; set; }
        public AtmStrategyType DefaultStrategy { get; set; }
        public int MaxContracts { get; set; }
        public double RiskPerTradePct { get; set; }
        public double RthMultiplier { get; set; }
        public double EthMultiplier { get; set; }
    }

    public class AtmStrategyConfig
    {
        public string Name { get; set; } = "PropFirm_Standard";
        public AtmStrategyType Type { get; set; } = AtmStrategyType.DrawdownShield;
        public int StopTicks { get; set; }
        public int TargetTicks { get; set; }
        public double AtrMultiplierSL { get; set; } = 1.5;
        public double AtrMultiplierTP { get; set; } = 2.5;
        public int AtrPeriod { get; set; } = 14;
        public int SwingLookbackBars { get; set; } = 5;
        public int SwingBufferTicks { get; set; } = 4;
        public int BreakevenTriggerTicks { get; set; } = 12;
        public int BreakevenOffsetTicks { get; set; } = 2;
        public double PartialProfitPct { get; set; } = 0.50;
        public double TrailMultiplier { get; set; } = 2.0;
        public double RiskPerTrade { get; set; } = 200.0;
        public double KellyFraction { get; set; } = 0.25;
        public double WinRate { get; set; } = 0.55;
        public double AvgRR { get; set; } = 2.0;
    }

    public class ActiveBracket
    {
        public string BracketId { get; set; }
        public string Symbol { get; set; }
        public string AccountName { get; set; }
        public bool IsLong { get; set; }
        public double EntryPrice { get; set; }
        public int Quantity { get; set; }
        public AtmStrategyConfig Config { get; set; }
        public string OcoId { get; set; }
        public string EntryOrderId { get; set; }
        public string StopOrderId { get; set; }
        public string TargetOrderId { get; set; }
        public double CurrentStopPrice { get; set; }
        public double CurrentTargetPrice { get; set; }
        public bool BreakevenTriggered { get; set; }
        public bool PartialProfitTaken { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsComplete { get; set; }
    }

    public class BracketResult
    {
        public string Status { get; set; }
        public string BracketId { get; set; }
        public string OcoId { get; set; }
        public string EntryOrderId { get; set; }
        public string StopOrderId { get; set; }
        public string TargetOrderId { get; set; }
        public double StopPrice { get; set; }
        public double TargetPrice { get; set; }
        public int CalculatedQuantity { get; set; }
        public string StrategyName { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    internal class BarData
    {
        public double[] High { get; set; }
        public double[] Low { get; set; }
        public double[] Close { get; set; }
        public double[] Open { get; set; }
        public long[] Volume { get; set; }
        public DateTime[] Time { get; set; }
        public int Count { get; set; }
    }

    public class DynamicAtmManager
    {
        private static readonly Lazy<DynamicAtmManager> _instance = new Lazy<DynamicAtmManager>(() => new DynamicAtmManager());
        public static DynamicAtmManager Instance { get { return _instance.Value; } }

        private readonly Dictionary<string, ActiveBracket> _activeBrackets;
        private readonly object _bracketLock;
        private Timer _monitorTimer;
        private bool _monitoring;

        private static readonly Dictionary<string, AtmInstrumentProfile> _profiles;
        private static readonly object _profileLock = new object();

        static DynamicAtmManager()
        {
            _profiles = new Dictionary<string, AtmInstrumentProfile>(StringComparer.OrdinalIgnoreCase)
            {
                { "ES", new AtmInstrumentProfile { Symbol = "ES", TickSize = 0.25, PointValue = 50.0, DefaultATR = 8.0, DefaultStrategy = AtmStrategyType.SwingPoint, MaxContracts = 20, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.5 } },
                { "NQ", new AtmInstrumentProfile { Symbol = "NQ", TickSize = 0.25, PointValue = 20.0, DefaultATR = 30.0, DefaultStrategy = AtmStrategyType.AtrAdaptive, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 2.0 } },
                { "MES", new AtmInstrumentProfile { Symbol = "MES", TickSize = 0.25, PointValue = 5.0, DefaultATR = 8.0, DefaultStrategy = AtmStrategyType.SwingPoint, MaxContracts = 50, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.5 } },
                { "MNQ", new AtmInstrumentProfile { Symbol = "MNQ", TickSize = 0.25, PointValue = 2.0, DefaultATR = 30.0, DefaultStrategy = AtmStrategyType.AtrAdaptive, MaxContracts = 50, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 2.0 } },
                { "CL", new AtmInstrumentProfile { Symbol = "CL", TickSize = 0.01, PointValue = 1000.0, DefaultATR = 0.80, DefaultStrategy = AtmStrategyType.AtrAdaptive, MaxContracts = 5, RiskPerTradePct = 0.005, RthMultiplier = 1.0, EthMultiplier = 1.0 } },
                { "GC", new AtmInstrumentProfile { Symbol = "GC", TickSize = 0.1, PointValue = 100.0, DefaultATR = 12.0, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.2 } },
                { "RTY", new AtmInstrumentProfile { Symbol = "RTY", TickSize = 0.1, PointValue = 50.0, DefaultATR = 20.0, DefaultStrategy = AtmStrategyType.AtrAdaptive, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.5 } },
                { "M2K", new AtmInstrumentProfile { Symbol = "M2K", TickSize = 0.1, PointValue = 5.0, DefaultATR = 20.0, DefaultStrategy = AtmStrategyType.AtrAdaptive, MaxContracts = 50, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.5 } },
                { "YM", new AtmInstrumentProfile { Symbol = "YM", TickSize = 1.0, PointValue = 5.0, DefaultATR = 150.0, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.3 } },
                { "MYM", new AtmInstrumentProfile { Symbol = "MYM", TickSize = 1.0, PointValue = 0.5, DefaultATR = 150.0, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 50, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.3 } },
                { "ZB", new AtmInstrumentProfile { Symbol = "ZB", TickSize = 0.03125, PointValue = 31.25, DefaultATR = 0.5, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.0 } },
                { "ZN", new AtmInstrumentProfile { Symbol = "ZN", TickSize = 0.015625, PointValue = 15.625, DefaultATR = 0.3, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.0 } },
                { "6E", new AtmInstrumentProfile { Symbol = "6E", TickSize = 0.00005, PointValue = 6.25, DefaultATR = 0.002, DefaultStrategy = AtmStrategyType.FixedTicks, MaxContracts = 10, RiskPerTradePct = 0.01, RthMultiplier = 1.0, EthMultiplier = 1.0 } }
            };
        }

        public DynamicAtmManager()
        {
            _activeBrackets = new Dictionary<string, ActiveBracket>();
            _bracketLock = new object();
        }

        public static AtmInstrumentProfile GetProfile(string rootSymbol)
        {
            AtmInstrumentProfile profile;
            lock (_profileLock)
            {
                if (_profiles.TryGetValue(rootSymbol, out profile))
                    return profile;
            }
            return null;
        }

        public static void RegisterProfile(AtmInstrumentProfile profile)
        {
            lock (_profileLock)
            {
                _profiles[profile.Symbol] = profile;
            }
        }

        public BracketResult PlaceBracket(
            Account account,
            Instrument instrument,
            string actionStr,
            int quantity,
            AtmStrategyConfig config,
            double currentPrice,
            double tickSize,
            double pointValue)
        {
            var result = new BracketResult();
            bool isLong = actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase);
            string symbol = instrument.MasterInstrument.Name;

            AtmInstrumentProfile profile = GetProfile(symbol);
            if (profile == null)
            {
                profile = new AtmInstrumentProfile
                {
                    Symbol = symbol,
                    TickSize = tickSize,
                    PointValue = pointValue,
                    DefaultATR = 10.0 * tickSize,
                    DefaultStrategy = AtmStrategyType.FixedTicks,
                    MaxContracts = 10,
                    RiskPerTradePct = 0.01,
                    RthMultiplier = 1.0,
                    EthMultiplier = 1.0
                };
            }

            double stopPrice = 0;
            double targetPrice = 0;
            int calculatedQty = quantity;

            switch (config.Type)
            {
                case AtmStrategyType.FixedTicks:
                {
                    int stopTicks = config.StopTicks > 0 ? config.StopTicks : 8;
                    int targetTicks = config.TargetTicks > 0 ? config.TargetTicks : 16;
                    stopPrice = isLong ? (currentPrice - stopTicks * tickSize) : (currentPrice + stopTicks * tickSize);
                    targetPrice = isLong ? (currentPrice + targetTicks * tickSize) : (currentPrice - targetTicks * tickSize);
                    result.StrategyName = "FixedTicks";
                    break;
                }

                case AtmStrategyType.AtrAdaptive:
                {
                    double atr = GetATR(instrument, config.AtrPeriod);
                    if (atr <= 0) atr = profile.DefaultATR * tickSize;
                    double slDist = atr * config.AtrMultiplierSL;
                    double tpDist = atr * config.AtrMultiplierTP;
                    stopPrice = isLong ? (currentPrice - slDist) : (currentPrice + slDist);
                    targetPrice = isLong ? (currentPrice + tpDist) : (currentPrice - tpDist);
                    result.StrategyName = "AtrAdaptive";
                    break;
                }

                case AtmStrategyType.SwingPoint:
                {
                    double swing = FindSwingPoint(instrument, isLong, config.SwingLookbackBars);
                    if (swing > 0)
                    {
                        double buffer = config.SwingBufferTicks * tickSize;
                        stopPrice = isLong ? (swing - buffer) : (swing + buffer);
                    }
                    else
                    {
                        int fallbackTicks = 10;
                        stopPrice = isLong ? (currentPrice - fallbackTicks * tickSize) : (currentPrice + fallbackTicks * tickSize);
                    }
                    targetPrice = isLong
                        ? (currentPrice + (currentPrice - stopPrice) * 2.0)
                        : (currentPrice - (stopPrice - currentPrice) * 2.0);
                    result.StrategyName = "SwingPoint";
                    break;
                }

                case AtmStrategyType.DrawdownShield:
                {
                    int stopTicks = config.StopTicks > 0 ? config.StopTicks : 10;
                    int targetTicks = config.TargetTicks > 0 ? config.TargetTicks : 20;
                    stopPrice = isLong ? (currentPrice - stopTicks * tickSize) : (currentPrice + stopTicks * tickSize);
                    targetPrice = isLong ? (currentPrice + targetTicks * tickSize) : (currentPrice - targetTicks * tickSize);
                    result.StrategyName = "DrawdownShield";
                    break;
                }

                case AtmStrategyType.ScaledRunner:
                {
                    int stopTicks = config.StopTicks > 0 ? config.StopTicks : 8;
                    int targetTicks = config.TargetTicks > 0 ? config.TargetTicks : 30;
                    stopPrice = isLong ? (currentPrice - stopTicks * tickSize) : (currentPrice + stopTicks * tickSize);
                    targetPrice = isLong ? (currentPrice + targetTicks * tickSize) : (currentPrice - targetTicks * tickSize);
                    result.StrategyName = "ScaledRunner";
                    break;
                }

                case AtmStrategyType.VolatilityScaled:
                {
                    double atr = GetATR(instrument, config.AtrPeriod);
                    if (atr <= 0) atr = profile.DefaultATR * tickSize;
                    double riskPerContract = atr * config.AtrMultiplierSL * pointValue;
                    if (riskPerContract > 0)
                    {
                        calculatedQty = (int)Math.Floor(config.RiskPerTrade / riskPerContract);
                        if (calculatedQty < 1) calculatedQty = 1;
                        if (calculatedQty > profile.MaxContracts) calculatedQty = profile.MaxContracts;
                    }
                    double slDist = atr * config.AtrMultiplierSL;
                    double tpDist = atr * config.AtrMultiplierTP;
                    stopPrice = isLong ? (currentPrice - slDist) : (currentPrice + slDist);
                    targetPrice = isLong ? (currentPrice + tpDist) : (currentPrice - tpDist);
                    result.StrategyName = "VolatilityScaled";
                    break;
                }

                case AtmStrategyType.SessionAdaptive:
                {
                    bool isRTH = IsRTH(GetEasternTime());
                    double multiplier = isRTH ? profile.RthMultiplier : profile.EthMultiplier;
                    int baseStopTicks = config.StopTicks > 0 ? config.StopTicks : 8;
                    int baseTargetTicks = config.TargetTicks > 0 ? config.TargetTicks : 16;
                    int stopTicks = (int)Math.Round(baseStopTicks * multiplier);
                    int targetTicks = (int)Math.Round(baseTargetTicks * multiplier);
                    stopPrice = isLong ? (currentPrice - stopTicks * tickSize) : (currentPrice + stopTicks * tickSize);
                    targetPrice = isLong ? (currentPrice + targetTicks * tickSize) : (currentPrice - targetTicks * tickSize);
                    result.StrategyName = "SessionAdaptive";
                    break;
                }

                case AtmStrategyType.KellyOptimal:
                {
                    double kellyPct = config.KellyFraction * (config.WinRate - (1.0 - config.WinRate) / config.AvgRR);
                    if (kellyPct < 0) kellyPct = 0.01;
                    double atr = GetATR(instrument, config.AtrPeriod);
                    if (atr <= 0) atr = profile.DefaultATR * tickSize;
                    double riskPerContract = atr * config.AtrMultiplierSL * pointValue;
                    if (riskPerContract > 0)
                    {
                        calculatedQty = (int)Math.Floor((config.RiskPerTrade * kellyPct) / riskPerContract);
                        if (calculatedQty < 1) calculatedQty = 1;
                        if (calculatedQty > profile.MaxContracts) calculatedQty = profile.MaxContracts;
                    }
                    double slDist = atr * config.AtrMultiplierSL;
                    double tpDist = atr * config.AtrMultiplierTP;
                    stopPrice = isLong ? (currentPrice - slDist) : (currentPrice + slDist);
                    targetPrice = isLong ? (currentPrice + tpDist) : (currentPrice - tpDist);
                    result.StrategyName = "KellyOptimal";
                    break;
                }
            }

            if (stopPrice <= 0 || targetPrice <= 0)
            {
                result.Status = "error";
                result.Error = "Could not calculate stop/target prices";
                return result;
            }

            string ocoId = Guid.NewGuid().ToString();
            string bracketId = Guid.NewGuid().ToString().Substring(0, 8);
            string entryName = "AtmEntry_" + bracketId;

            var entryAction = isLong ? OrderAction.Buy : OrderAction.Sell;
            var exitAction = isLong ? OrderAction.Sell : OrderAction.Buy;

            try
            {
                var entryOrder = account.CreateOrder(instrument, entryAction, OrderType.Market, TimeInForce.Day, calculatedQty, 0, 0, string.Empty, entryName, null);
                if (entryOrder == null)
                {
                    result.Status = "error";
                    result.Error = "Failed to create entry order";
                    return result;
                }
                var stopOrder = account.CreateOrder(instrument, exitAction, OrderType.StopMarket, TimeInForce.Day, calculatedQty, 0, stopPrice, ocoId, "Stop_" + bracketId, null);
                var targetOrder = account.CreateOrder(instrument, exitAction, OrderType.Limit, TimeInForce.Day, calculatedQty, targetPrice, 0, ocoId, "Target_" + bracketId, null);

                var validOrders = new[] { entryOrder, stopOrder, targetOrder }
                    .Where(o => o != null && o.OrderState != OrderState.CancelPending && o.OrderState != OrderState.Cancelled)
                    .ToArray();

                if (validOrders.Length > 0)
                {
                    account.Submit(validOrders);
                }

                result.Status = "submitted";
                result.BracketId = bracketId;
                result.OcoId = ocoId;
                result.EntryOrderId = entryOrder.OrderId;
                result.StopOrderId = stopOrder != null ? stopOrder.OrderId : null;
                result.TargetOrderId = targetOrder != null ? targetOrder.OrderId : null;
                result.StopPrice = stopPrice;
                result.TargetPrice = targetPrice;
                result.CalculatedQuantity = calculatedQty;

                bool needsMonitor = (config.Type == AtmStrategyType.DrawdownShield || config.Type == AtmStrategyType.ScaledRunner);
                if (needsMonitor)
                {
                    var bracket = new ActiveBracket
                    {
                        BracketId = bracketId,
                        Symbol = symbol,
                        AccountName = account.Name,
                        IsLong = isLong,
                        EntryPrice = currentPrice,
                        Quantity = calculatedQty,
                        Config = config,
                        OcoId = ocoId,
                        EntryOrderId = entryOrder.OrderId,
                        StopOrderId = stopOrder != null ? stopOrder.OrderId : null,
                        TargetOrderId = targetOrder != null ? targetOrder.OrderId : null,
                        CurrentStopPrice = stopPrice,
                        CurrentTargetPrice = targetPrice,
                        BreakevenTriggered = false,
                        PartialProfitTaken = false,
                        CreatedAt = DateTime.UtcNow,
                        IsComplete = false
                    };
                    RegisterBracket(bracket);
                    EnsureMonitor();
                    result.Note = "Bracket registered for breakeven/trailing monitoring";
                }

                List<string> rejectedOrders = new List<string>();
                foreach (var o in new[] { stopOrder, targetOrder })
                {
                    if (o != null && (o.OrderState == OrderState.Rejected || o.OrderState == OrderState.Cancelled))
                        rejectedOrders.Add(o.Name + " state=" + o.OrderState);
                }
                if (rejectedOrders.Count > 0)
                {
                    result.Status = "partial_submit";
                    result.Note = (result.Note ?? "") + " Some exit orders rejected: " + string.Join(", ", rejectedOrders);
                }
            }
            catch (Exception ex)
            {
                result.Status = "error";
                result.Error = ex.Message;
            }

            return result;
        }

        public void RegisterBracket(ActiveBracket bracket)
        {
            lock (_bracketLock)
            {
                _activeBrackets[bracket.BracketId] = bracket;
            }
        }

        public void RemoveBracket(string bracketId)
        {
            lock (_bracketLock)
            {
                _activeBrackets.Remove(bracketId);
            }
        }

        public List<ActiveBracket> GetActiveBrackets()
        {
            lock (_bracketLock)
            {
                return _activeBrackets.Values.Where(b => !b.IsComplete).ToList();
            }
        }

        public object GetBracketStatus(string bracketId)
        {
            lock (_bracketLock)
            {
                ActiveBracket b;
                if (_activeBrackets.TryGetValue(bracketId, out b))
                {
                    return new
                    {
                        bracketId = b.BracketId,
                        symbol = b.Symbol,
                        account = b.AccountName,
                        isLong = b.IsLong,
                        entryPrice = b.EntryPrice,
                        quantity = b.Quantity,
                        strategy = b.Config != null ? b.Config.Type.ToString() : "Unknown",
                        currentStop = b.CurrentStopPrice,
                        currentTarget = b.CurrentTargetPrice,
                        breakevenTriggered = b.BreakevenTriggered,
                        partialProfitTaken = b.PartialProfitTaken,
                        isComplete = b.IsComplete,
                        ageSeconds = (DateTime.UtcNow - b.CreatedAt).TotalSeconds
                    };
                }
                return new { error = "bracket not found" };
            }
        }

        private void EnsureMonitor()
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(MonitorTick, null, 5000, 5000);
        }

        private void MonitorTick(object _)
        {
            try
            {
                // Marshal the entire monitoring logic to the NT8 UI dispatcher.
                // NT8 Account/Order/Position objects are NOT thread-safe.
#if TESTING
                MonitorTickCore();
#else
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                dispatcher.InvokeAsync(() => MonitorTickCore());
#endif
            }
            catch (Exception ex)
            {
                try { NinjaTrader.Code.Output.Process("[AtmMonitor] Dispatcher error: " + ex.Message, PrintTo.OutputTab1); } catch { }
            }
        }

        private void MonitorTickCore()
        {
            List<ActiveBracket> toRemove = new List<ActiveBracket>();
            List<ActiveBracket> active;

            lock (_bracketLock)
            {
                active = _activeBrackets.Values.Where(b => !b.IsComplete).ToList();
            }

            foreach (var bracket in active)
            {
                try
                {
                    Account account = Account.All.FirstOrDefault(a => a.Name.Equals(bracket.AccountName, StringComparison.OrdinalIgnoreCase));
                    if (account == null)
                    {
                        toRemove.Add(bracket);
                        continue;
                    }

                    Position position = account.Positions.FirstOrDefault(p =>
                        p.Instrument.MasterInstrument.Name.Equals(bracket.Symbol, StringComparison.OrdinalIgnoreCase));

                    if (position == null || Math.Abs(position.Quantity) == 0)
                    {
                        bool entryStillWorking = account.Orders.Any(o =>
                            o.OrderId == bracket.EntryOrderId &&
                            (o.OrderState == OrderState.Working || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.Accepted));
                        if (!entryStillWorking)
                        {
                            toRemove.Add(bracket);
                        }
                        continue;
                    }

                    double currentPrice = 0;
                    var md = position.Instrument.MarketData;
                    if (md != null && md.Last != null)
                        currentPrice = md.Last.Price;
                    if (currentPrice <= 0 && md != null && md.Ask != null)
                        currentPrice = md.Ask.Price;
                    if (currentPrice <= 0 && md != null && md.Bid != null)
                        currentPrice = md.Bid.Price;
                    if (currentPrice <= 0) continue;
                    double tickSize = position.Instrument.MasterInstrument.TickSize;
                    bool isLong = bracket.IsLong;
                    double entryPrice = bracket.EntryPrice;

                    if (bracket.Config.Type == AtmStrategyType.DrawdownShield)
                    {
                        if (!bracket.BreakevenTriggered && ShouldTriggerBreakeven(bracket.Config, entryPrice, currentPrice, isLong, tickSize))
                        {
                            double beStop = CalculateBreakevenStopPrice(entryPrice, isLong, tickSize, bracket.Config.BreakevenOffsetTicks);
                            ModifyStopPrice(account, bracket.StopOrderId, beStop);
                            bracket.CurrentStopPrice = beStop;
                            bracket.BreakevenTriggered = true;
                        }

                        if (!bracket.PartialProfitTaken && bracket.BreakevenTriggered)
                        {
                            double partialTarget = isLong
                                ? (entryPrice + (bracket.CurrentTargetPrice - entryPrice) * bracket.Config.PartialProfitPct)
                                : (entryPrice - (entryPrice - bracket.CurrentTargetPrice) * bracket.Config.PartialProfitPct);
                            bool partialHit = isLong ? (currentPrice >= partialTarget) : (currentPrice <= partialTarget);
                            if (partialHit)
                            {
                                int partialQty = (int)Math.Floor(bracket.Quantity * bracket.Config.PartialProfitPct);
                                if (partialQty > 0)
                                {
                                    var exitAction = isLong ? OrderAction.Sell : OrderAction.Buy;
                                    var partialOrder = account.CreateOrder(position.Instrument, exitAction, OrderType.Limit, TimeInForce.Day, partialQty, partialTarget, 0, bracket.OcoId, "Partial_" + bracket.BracketId, null);
                                    account.Submit(new[] { partialOrder });
                                }
                                bracket.PartialProfitTaken = true;
                            }
                        }
                    }

                    if (bracket.Config.Type == AtmStrategyType.ScaledRunner)
                    {
                        if (!bracket.BreakevenTriggered && ShouldTriggerBreakeven(bracket.Config, entryPrice, currentPrice, isLong, tickSize))
                        {
                            double beStop = CalculateBreakevenStopPrice(entryPrice, isLong, tickSize, bracket.Config.BreakevenOffsetTicks);
                            ModifyStopPrice(account, bracket.StopOrderId, beStop);
                            bracket.CurrentStopPrice = beStop;
                            bracket.BreakevenTriggered = true;
                        }

                        if (bracket.BreakevenTriggered)
                        {
                            double trailDist = tickSize * bracket.Config.StopTicks * bracket.Config.TrailMultiplier;
                            double newStop = isLong
                                ? (currentPrice - trailDist)
                                : (currentPrice + trailDist);
                            bool stopMoved = isLong ? (newStop > bracket.CurrentStopPrice) : (newStop < bracket.CurrentStopPrice);
                            if (stopMoved)
                            {
                                ModifyStopPrice(account, bracket.StopOrderId, newStop);
                                bracket.CurrentStopPrice = newStop;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { NinjaTrader.Code.Output.Process("[AtmMonitor] Error monitoring bracket " + bracket.BracketId + ": " + ex.Message, PrintTo.OutputTab1); } catch { }
                }
            }

            if (toRemove.Count > 0)
            {
                lock (_bracketLock)
                {
                    foreach (var b in toRemove)
                        _activeBrackets.Remove(b.BracketId);
                }
            }
        }

        private void ModifyStopPrice(Account account, string orderId, double newStopPrice)
        {
            try
            {
                foreach (Order order in account.Orders)
                {
                    if (order.OrderId == orderId && order.OrderState == OrderState.Working)
                    {
                        order.StopPrice = newStopPrice;
                        account.Change(new[] { order });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                try { NinjaTrader.Code.Output.Process("[AtmMonitor] ModifyStopPrice failed: " + ex.Message, PrintTo.OutputTab1); } catch { }
            }
        }

        public bool ShouldTriggerBreakeven(AtmStrategyConfig config, double entryPrice, double currentPrice, bool isLong, double tickSize)
        {
            double diff = isLong ? (currentPrice - entryPrice) : (entryPrice - currentPrice);
            double ticksGain = diff / tickSize;
            return ticksGain >= config.BreakevenTriggerTicks;
        }

        public double CalculateBreakevenStopPrice(double entryPrice, bool isLong, double tickSize, int offsetTicks)
        {
            double offset = offsetTicks * tickSize;
            return isLong ? (entryPrice + offset) : (entryPrice - offset);
        }

        private double GetATR(Instrument instrument, int period)
        {
            try
            {
                if (period <= 0) period = 14;
                BarData bars = FetchBars(instrument, BarsPeriodType.Minute, 1, period + 5);
                if (bars == null || bars.Count < period + 1) return 0;

                double sum = 0;
                int count = 0;
                for (int i = bars.Count - period; i < bars.Count; i++)
                {
                    double high = bars.High[i];
                    double low = bars.Low[i];
                    double prevClose = i > 0 ? bars.Close[i - 1] : bars.Close[i];
                    double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                    sum += tr;
                    count++;
                }
                return count > 0 ? sum / count : 0;
            }
            catch (Exception ex)
            {
                try { NinjaTrader.Code.Output.Process("[AtmMonitor] GetATR error: " + ex.Message, PrintTo.OutputTab1); } catch { }
                return 0;
            }
        }

        private double FindSwingPoint(Instrument instrument, bool isLong, int lookback)
        {
            try
            {
                BarData bars = FetchBars(instrument, BarsPeriodType.Minute, 5, lookback + 5);
                if (bars == null || bars.Count < lookback + 2) return 0;

                if (isLong)
                {
                    double lowest = double.MaxValue;
                    for (int i = bars.Count - lookback; i < bars.Count; i++)
                    {
                        double low = bars.Low[i];
                        if (low < lowest) lowest = low;
                    }
                    return lowest;
                }
                else
                {
                    double highest = double.MinValue;
                    for (int i = bars.Count - lookback; i < bars.Count; i++)
                    {
                        double high = bars.High[i];
                        if (high > highest) highest = high;
                    }
                    return highest;
                }
            }
            catch (Exception ex)
            {
                try { NinjaTrader.Code.Output.Process("[AtmMonitor] FindSwingPoint error: " + ex.Message, PrintTo.OutputTab1); } catch { }
                return 0;
            }
        }

        private static BarData FetchBars(Instrument instrument, BarsPeriodType periodType, int periodValue, int count)
        {
            BarData result = null;
            var done = new ManualResetEventSlim(false);
            var barsPeriod = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };
            var request = new BarsRequest(instrument, count) { BarsPeriod = barsPeriod };
            request.Request((req, code, msg) =>
            {
                if (code == ErrorCode.NoError && req.Bars != null)
                {
                    var bars = req.Bars;
                    int n = bars.Count;
                    int start = Math.Max(0, n - count);
                    int copied = n - start;
                    result = new BarData
                    {
                        High = new double[copied],
                        Low = new double[copied],
                        Close = new double[copied],
                        Open = new double[copied],
                        Volume = new long[copied],
                        Time = new DateTime[copied],
                        Count = copied
                    };
                    for (int i = 0; i < copied; i++)
                    {
                        int src = start + i;
                        result.High[i] = bars.GetHigh(src);
                        result.Low[i] = bars.GetLow(src);
                        result.Close[i] = bars.GetClose(src);
                        result.Open[i] = bars.GetOpen(src);
                        result.Volume[i] = bars.GetVolume(src);
                        result.Time[i] = bars.GetTime(src);
                    }
                }
                done.Set();
            });
            if (!done.Wait(TimeSpan.FromSeconds(10)))
                return null;
            request.Dispose();
            return result;
        }

        private static DateTime GetEasternTime()
        {
            try
            {
                return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Eastern Standard Time");
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private static bool IsRTH(DateTime time)
        {
            if (time.DayOfWeek == DayOfWeek.Saturday || time.DayOfWeek == DayOfWeek.Sunday)
                return false;
            int hour = time.Hour;
            int minute = time.Minute;
            int totalMinutes = hour * 60 + minute;
            return totalMinutes >= 570 && totalMinutes < 960;
        }
    }
}
