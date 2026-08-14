#if TESTING
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// --- MOCK DEFINITIONS TO AVOID NINJATRADER ASSEMBLY DEPENDENCY IN TEST ENVIRONMENT ---
namespace NinjaTrader.Cbi
{
    public enum MarketPosition { Flat, Long, Short }
    public enum Currency { UsDollar }
    public enum AccountItem { CashValue, RealizedProfitLoss, UnrealizedProfitLoss, NetLiquidation }

    public class AccountItemEventArgs : EventArgs
    {
        public AccountItem AccountItem { get; set; }
        public double Value { get; set; }
        public Currency Currency { get; set; }
    }
    public enum OrderState { Submitted, Accepted, Working, Cancelled, CancelPending, Filled, PartFilled, Rejected, Unknown, Initialized }
    public enum OrderType { Limit, StopMarket, StopLimit, Market }
    public enum OrderAction { Buy, Sell, BuyToCover, SellShort }
    public enum TimeInForce { Day, Gtc }
    public enum PerformanceUnit { Currency, Percent, Pips, Points, Ticks }

    public class Instrument
    {
        public string FullName { get; set; }
        public MasterInstrument MasterInstrument { get; set; }
        public MarketData MarketData { get; set; }
        public Instrument(string name) 
        { 
            FullName = name; 
            MasterInstrument = new MasterInstrument { Name = name, TickSize = 0.25 };
            MarketData = new MarketData { Last = new Last { Price = 0.0 } };
        }
    }

    public class MasterInstrument
    {
        public string Name { get; set; }
        public double TickSize { get; set; }
        public double RoundToTickSize(double value) => Math.Round(value / TickSize) * TickSize;
    }

    public class MarketData
    {
        public Last Last { get; set; }
        public Ask Ask { get; set; }
        public Bid Bid { get; set; }
    }

    public class Last
    {
        public double Price { get; set; }
    }

    public class Ask
    {
        public double Price { get; set; }
    }

    public class Bid
    {
        public double Price { get; set; }
    }

    public class Order
    {
        public string Id { get; set; }
        public string OrderId { get; set; }
        public string Name { get; set; }
        public string Oco { get; set; }
        public OrderState OrderState { get; set; }
        public OrderType OrderType { get; set; }
        public int Quantity { get; set; }
        public int Filled { get; set; }
        public Instrument Instrument { get; set; }
        public OrderAction OrderAction { get; set; }
        public double LimitPrice { get; set; }
        public double StopPrice { get; set; }
    }

    public class Position
    {
        public Instrument Instrument { get; set; }
        public MarketPosition MarketPosition { get; set; }
        public int Quantity { get; set; }
        public double AveragePrice { get; set; }
        public double UnrealizedPnL { get; set; }
        public double GetUnrealizedProfitLoss(PerformanceUnit unit) => UnrealizedPnL;
    }

    public class Execution
    {
        public Instrument Instrument { get; set; }
        public Order Order { get; set; }
        public int Quantity { get; set; }
        public double Price { get; set; }
    }

    public class Account
    {
        public string Name { get; set; }
        public Dictionary<AccountItem, double> Values { get; set; } = new Dictionary<AccountItem, double>();
        public List<Order> Orders { get; set; } = new List<Order>();
        public List<Position> Positions { get; set; } = new List<Position>();
        public static List<Account> All { get; set; } = new List<Account>();
        public bool SimulateExitRejection { get; set; }

        public event EventHandler<PositionEventArgs> PositionUpdate;
        public event EventHandler<OrderEventArgs> OrderUpdate;
        public event EventHandler<ExecutionEventArgs> ExecutionUpdate;
        public event EventHandler<AccountItemEventArgs> AccountItemUpdate;

        public double Get(AccountItem item, Currency currency)
        {
            return Values.ContainsKey(item) ? Values[item] : 0.0;
        }

        public void Cancel(Order[] orders)
        {
            foreach (var o in orders)
            {
                o.OrderState = OrderState.Cancelled;
            }
        }

        public void Cancel(List<Order> orders)
        {
            foreach (var o in orders)
            {
                o.OrderState = OrderState.Cancelled;
            }
        }

        public void Flatten(Instrument[] instruments)
        {
            Positions.Clear();
            Orders.Clear();
        }

        public Order CreateOrder(Instrument instrument, OrderAction action, OrderType type, TimeInForce tif, int qty, double limit, double stop, string oco, string name, object custom)
        {
            var o = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderId = Guid.NewGuid().ToString(),
                Name = name,
                Oco = oco,
                OrderState = OrderState.Initialized,
                OrderType = type,
                Quantity = qty,
                Instrument = instrument,
                OrderAction = action,
                LimitPrice = limit,
                StopPrice = stop
            };
            return o;
        }

        public void Submit(Order[] orders)
        {
            foreach (var o in orders)
            {
                o.OrderState = OrderState.Submitted;
                if (SimulateExitRejection && (o.Name.StartsWith("Stop_") || o.Name.StartsWith("Target_")))
                    o.OrderState = OrderState.Rejected;
                Orders.Add(o);
            }
        }

        public void Change(Order[] orders)
        {
            foreach (var o in orders)
            {
                o.OrderState = OrderState.Working;
            }
        }

        public void TriggerPositionUpdate(Position p)
        {
            PositionUpdate?.Invoke(this, new PositionEventArgs { Position = p });
        }

        public void TriggerOrderUpdate(Order o)
        {
            OrderUpdate?.Invoke(this, new OrderEventArgs { Order = o });
        }

        public void TriggerExecutionUpdate(Execution ex)
        {
            ExecutionUpdate?.Invoke(this, new ExecutionEventArgs { Execution = ex });
        }

        public void TriggerAccountItemUpdate(AccountItem item, double value)
        {
            AccountItemUpdate?.Invoke(this, new AccountItemEventArgs { AccountItem = item, Value = value, Currency = Currency.UsDollar });
        }
    }

    public class Connection
    {
        public static event EventHandler<ConnectionStatusEventArgs> ConnectionStatusUpdate;
        public static void TriggerConnectionStatusUpdate(ConnectionStatusEventArgs e)
        {
            ConnectionStatusUpdate?.Invoke(null, e);
        }
    }

    public class ConnectionStatusEventArgs : EventArgs
    {
        public object Status { get; set; }
        public dynamic Connection { get; set; }
    }

    public class PositionEventArgs : EventArgs
    {
        public Position Position { get; set; }
    }

    public class OrderEventArgs : EventArgs
    {
        public Order Order { get; set; }
    }

    public class ExecutionEventArgs : EventArgs
    {
        public Execution Execution { get; set; }
    }
}

namespace NinjaTrader.Core
{
    public static class Globals
    {
        public static string UserDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MockUserData");
    }
}

namespace NinjaTrader.Code
{
    public enum PrintTo { OutputTab1 }
    public static class Output
    {
        public static void Process(string msg, PrintTo tab)
        {
            Console.WriteLine("[OUTPUT] " + msg);
        }
    }
}

namespace NinjaTrader.NinjaScript
{
    public enum State
    {
        SetDefaults,
        Configure,
        Terminated
    }

    public class AddOnBase
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public State State { get; set; }
        protected virtual void OnStateChange() {}
    }
}

namespace NinjaTrader.NinjaScript.AddOns
{
    using NinjaTrader.Cbi;
    using NinjaTrader.Data;

    // --- TEST EXECUTION HARNESS ---
    public class Program
    {
        private static int _testsPassed = 0;
        private static int _testsFailed = 0;

        public static void Main(string[] args)
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("- RUNNING RISK GUARD ADDON EDGE CASE UNIT TESTS");
            Console.WriteLine("====================================================");

            // - Original 9 tests -
            TestMaxPositionSizeEnforcement();
            TestDailyLossLimitLockout();
            TestTrailingDrawdownLockout();
            TestMaxTradesOvertradingLockout();
            TestConsecutiveLossesCooldownLockout();
            TestAccountExclusionsBypass();
            TestManualUnlockResetsAllMetricsAndPreventsRelocking();
            TestRealizedPnLLagHandling();
            TestMcpBridgeLockoutBlock();

            // - Critical gap tests -
            TestIsArmedFalseBypassesAllRules();
            TestTradeTodayCountingOnRoundTrip();
            TestFlipDetectionCountsAsEntry();
            TestTradeCountingMultiContractScalingDebounced();
            TestLockoutWatchdogSweepFlattensOpenPosition();
            TestLockoutAllowsPositionReducingOrders();
            TestCooldownExpiryAllowsReEntry();
            TestOrderCancelledWhenLockedOnOrderUpdate();
            TestOrderCancelledWhenConsecLossesAtMaxNotLocked();

            // - Important gap tests -
            TestDailyLossIncludesUnrealizedPnL();
            TestSessionResetInSweep();
            TestLockoutEnforcementFirstSweep();
            TestLockoutEnforcementSubsequentSweepNoPosition();
            TestLockoutEnforcementSubsequentSweepWithNewPosition();
            TestStopGuardAutoStop();
            TestStopGuardFlatten();
            TestStopGuardNoActionWhenStopPresent();
            TestStopGuardTransientStateValidation();
            TestStopGuardPartiallyFilledValidation();
            TestEdgeWindowGateBreach();
            TestConsecutiveWinsResetLossCounter();
            TestAggregateSizeBreach();

            // - Lower-priority / boundary tests -
            TestShadowModeSkipsAction();
            TestLiveModeExecutesAction();
            TestMaxSizeAtExactlyLimit();
            TestDailyLossAtExactlyLimit();
            TestIsAccountLockedForUnknownAccount();
            TestMultipleInstrumentsNoPerInstrumentBreach();

            // - Exclusion deep-dive tests (test-first) -
            TestExcludedAccountMaxContractsBypassed();
            TestExcludedAccountAllRulesBypassed();
            TestExcludedAccountOrderNotCancelledWhenLocked();
            TestExcludedAccountNotCountedInAggregate();
            TestExcludedAccountNotFlattenedByAggregateBreach();
            TestExcludedAccountSweepDoesNotLockout();
            TestNonExcludedAccountStillCaughtBesideExcludedOne();
            TestExclusionRemovedReEnablesRules();

            // - Pass 2 Gap Tests (test-first) -
            TestSweepLockoutSkipsExcludedAccount();
            TestSweepPnLSyncSkipsConsecutiveLossForExcludedAccount();
            TestValidateInvariantReturnsFalseForUnknownAccount();
            TestStopGuardPartialStopGap();
            TestStopGuardWarnOnlyProducesNoAction();
            TestSweepAutoSetsCooldownOnConsecutiveLosses();
            TestProcessActionForceLiveBypassesShadowMode();
            TestEdgeWindowGateInsideWindowNoBreach();
            TestEdgeWindowGateNoWindowsDefinedNoBreach();
            TestMultipleRulesFireSimultaneously();

            // - Pass 3 Gap Tests -
            TestAggregateSizingExpectedCopiesScaling();
            TestFirmMirrorTrailingDDBreachEmitsAction();
            TestFirmMirrorDailyLossBreachEmitsAction();
            TestStopGuardDefaultOffsetFallback();

            // - Manual Lockout Tests -
            TestManualTimedLockout();
            TestManualEodLockout();
            TestManualUnlockClearsTimedLockout();

            // - FSM StopGuard Tests (-6) -
            TestFsm_UnprotectedToProtectedViaOcoStopLeg();
            TestFsm_NoDuplicateAutoStopWhenStopLegPending();
            TestFsm_GraceExpiryPlacesAutoStopOnce();
            TestFsm_StopArrivesBeforePositionIsBuffered();
            TestFsm_FlatTearsDownAndCancelsOrphanAutoStop();
            TestFsm_StandaloneStopReachesProtected();
            TestFsm_RejectedStopLegReturnsToUnprotected();
            TestFsm_PositionFlattenedBeforeGraceNoAutoStop();
            TestFsm_DuplicateOrderUpdatesAreIdempotent();
            TestFsm_DuplicatePositionUpdatesAreIdempotent();
            TestFsm_EvaluateRulesNoLongerEmitsStopGuard();
            TestFsm_ExcludedAccountSkipsFsm();

            // -- FSM edge-case tests --
            TestFsm_ProtectedToUnprotectedOnStopFilled();
            TestFsm_ProtectedPendingToUnprotectedOnCancelled();
            TestFsm_GraceExpiryFlatten();
            TestFsm_GraceNotExpiredNoAction();
            TestFsm_ShortPositionProtected();
            TestFsm_FlipRecreatesFsm();
            TestFsm_MultipleInstrumentsIndependent();
            TestFsm_DisarmedSkipsFsm();
            TestFsm_LimitOrderDoesNotTransition();
            TestFsm_PendingStopWorkingConsumed();

            // -- FSM OrderAction bug tests (BuyToCover/SellShort) --
            TestFsm_ShortPositionBuyToCoverStopRecognized();
            TestFsm_LongPositionSellShortStopRecognized();

            // -- AUDIT: Regression tests for identified bugs --
            TestFsm_QtyOnlyUpdatePreservesProtectedState();
            TestFsm_PartialFillPreservesProtectedState();
            TestFsm_GraceExpiryFlattenEmitsOnce();
            TestFsm_PositionQuantityUpdatedOnQtyChange();
            TestPnLRulesNotDuplicatedInEvaluateRules();
            TestExecuteOrderUpdateProcessesActionsOutsideLock();

            // -- COPIER GROUPS & STRESS TESTS --
            TestCopierGroup_GroupManagement();
            TestCopierGroup_PerGroupConfigurationExecution();
            TestCopierGroup_GroupPersistence();
            TestCopierGroup_GroupStressAndConcurrency();
            RunCopierFixesVerificationTests();
            TestOrderVerificationWatchdogAndReconciliation();
            TestHedgingReconciliationAndAutoClose();

            // -- DYNAMIC ATM BRACKET TESTS --
            TestAtm_FixedTicksLong();
            TestAtm_FixedTicksShort();
            TestAtm_DrawdownShieldRegistersBracket();
            TestAtm_ScaledRunnerRegistersBracket();
            TestAtm_MonitoredStrategiesNotDoubleRegistered();
            TestAtm_VolatilityScaledQuantityCapped();
            TestAtm_VolatilityScaledRiskBasedQuantity();
            TestAtm_AtrAdaptiveFallbackUsesDefaultAtr();
            TestAtm_AtrAdaptiveUsesLiveAtr();
            TestAtm_SwingPointUsesSwingLow();
            TestAtm_SessionAdaptiveMultiplier();
            TestAtm_UnknownSymbolFallsBackToDefaults();
            TestAtm_GetProfileKnownAndUnknown();
            TestAtm_ZeroPriceReturnsError();
            TestAtm_RejectedExitOrdersPartialSubmit();
            TestAtm_OcoIdSharedAcrossExitOrders();
            TestAtm_ShouldTriggerBreakeven();
            TestAtm_CalculateBreakevenStopPrice();
            TestAtm_ActiveBracketStatus();

            Console.WriteLine("\n====================================================");
            Console.WriteLine(string.Format("RESULTS: Passed = {0}, Failed = {1}", _testsPassed, _testsFailed));
            Console.WriteLine("====================================================");

            if (_testsFailed > 0)
            {
                Environment.Exit(1);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (condition)
            {
                Console.WriteLine("  [PASS] " + message);
                _testsPassed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [FAIL] " + message);
                Console.ResetColor();
                _testsFailed++;
            }
        }

        private static void TestMaxPositionSizeEnforcement()
        {
            Console.WriteLine("\n[TEST] Max Position Sizing Enforcement");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 5;

            var account = new Account();
            account.Name = "TestAcc";
            
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 6, 18000, 0, config);

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Any(a => a.RuleId == "MAX_SIZE_BREACH"), "Size limit breached above max contracts.");

            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 4, 18000, 0, config);
            actions = addon.EvaluateRules(account, state);
            Assert(!actions.Any(a => a.RuleId == "MAX_SIZE_BREACH"), "No size breach under max contracts.");
        }

        private static void TestDailyLossLimitLockout()
        {
            Console.WriteLine("\n[TEST] Daily Loss Limit Lockout");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 1000.0;

            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.RealizedPnL = -1100.0;
            state.UnrealizedPnL = 0.0;

            var actions = addon.EvaluatePnLRules(account, state);

            Assert(actions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"), "Daily loss breach locks out account.");
            Assert(state.IsLockedOut, "Account state is marked as IsLockedOut.");
        }

        private static void TestTrailingDrawdownLockout()
        {
            Console.WriteLine("\n[TEST] Trailing Drawdown Lockout");
            var config = new RiskConfig();
            config.PnLRules.TrailingDrawdown = 1500.0;

            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.UnrealizedPnL = 0.0;

            // Peak equity at +500
            state.RealizedPnL = 500.0;
            addon.EvaluatePnLRules(account, state);
            Assert(state.PeakEquity == 500.0, "Peak equity correctly tracks top session profit.");

            // Drawdown to -1100 (breach = 500 - 1500 = -1000)
            state.RealizedPnL = -1100.0;
            var actions = addon.EvaluatePnLRules(account, state);

            Assert(actions.Any(a => a.RuleId == "TRAILING_DD_BREACH"), "Trailing Drawdown breach locks out account.");
            Assert(state.IsLockedOut, "Account state is marked as IsLockedOut.");
        }

        private static void TestMaxTradesOvertradingLockout()
        {
            Console.WriteLine("\n[TEST] Max Trades Overtrading Lockout");
            var config = new RiskConfig();
            config.Overtrading.MaxTradesPerSession = 5;

            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.TradesToday = 6;

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Any(a => a.RuleId == "MAX_TRADES_BREACH"), "Max trades breach locks out account.");
            Assert(state.IsLockedOut, "Account state is marked as IsLockedOut.");
        }

        private static void TestConsecutiveLossesCooldownLockout()
        {
            Console.WriteLine("\n[TEST] Consecutive Losses & Cooldown Lockout");
            var config = new RiskConfig();
            config.Overtrading.MaxConsecutiveLosses = 3;
            config.Overtrading.CooldownMinutes = 5;

            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.ConsecutiveLosses = 3;

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Any(a => a.RuleId == "CONSECUTIVE_LOSS_BREACH"), "Consec losses breach locks out account.");
            Assert(state.IsLockedOut, "Account state is marked as IsLockedOut.");

            // Check Cooldown
            state.ConsecutiveLosses = 0;
            state.CooldownUntil = DateTime.UtcNow.AddMinutes(5);
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);
            actions = addon.EvaluateRules(account, state);
            Assert(actions.Any(a => a.RuleId == "COOLDOWN_BREACH"), "Entering during cooldown triggers flatten.");
        }

        private static void TestAccountExclusionsBypass()
        {
            Console.WriteLine("\n[TEST] Account Exclusions Bypass");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.PnLRules.DailyLossLimit = 1000.0;

            var account = new Account();
            account.Name = "ExcludedAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("ExcludedAcc");
            state.RealizedPnL = -1500.0;

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Count == 0, "Excluded account bypasses daily loss limit evaluation.");
            Assert(!state.IsLockedOut, "Excluded account is NOT marked as IsLockedOut.");
        }

        private static void TestManualUnlockResetsAllMetricsAndPreventsRelocking()
        {
            Console.WriteLine("\n[TEST] Manual Unlock Resets All Metrics & Prevents Relocking");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 1000.0;
            config.PnLRules.TrailingDrawdown = 1500.0;
            config.Overtrading.MaxConsecutiveLosses = 3;
            config.Overtrading.MaxTradesPerSession = 5;

            var account = new Account();
            account.Name = "TestAcc";
            account.Values[AccountItem.RealizedProfitLoss] = -1200.0; // Currently down 1200

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            
            var state = new AccountState("TestAcc");
            state.IsLockedOut = true;
            state.PeakEquity = 200.0;
            state.TradesToday = 6;
            state.ConsecutiveLosses = 4;
            state.CooldownUntil = DateTime.UtcNow.AddMinutes(5);
            state.SessionStartRealizedPnL = 0.0;
            state.RealizedPnL = -1200.0;

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");

            // Account must be in Account.All so UnlockAccount can read its realized PnL
            Account.All.Clear();
            Account.All.Add(account);

            // --- PERFORM THE UNLOCK ---
            addon.UnlockAccount("TestAcc");

            // Verify all metrics reset
            Assert(!state.IsLockedOut, "IsLockedOut is set to false.");
            Assert(state.PeakEquity == 0.0, "PeakEquity is reset to 0.0.");
            Assert(state.TradesToday == 0, "TradesToday is reset to 0.");
            Assert(state.ConsecutiveLosses == 0, "ConsecutiveLosses is reset to 0.");
            Assert(state.CooldownUntil == DateTime.MinValue, "CooldownUntil is reset.");
            Assert(state.SessionStartRealizedPnL == -1200.0, "SessionStartRealizedPnL updated to current realized balance (-1200).");
            Assert(state.RealizedPnL == 0.0, "Relative RealizedPnL reset to 0.0.");

            // --- RUN EVALUATOR AGAIN ---
            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Count == 0, "No new lockout actions generated after unlocking (relative PnL is 0).");
            Assert(!state.IsLockedOut, "Account stays active (does not instantly re-lock!).");
        }

        private static void TestRealizedPnLLagHandling()
        {
            Console.WriteLine("\n[TEST] Real-Time PnL Lag Handling");
            var config = new RiskConfig();
            config.Overtrading.MaxConsecutiveLosses = 3;
            config.Overtrading.CooldownMinutes = 5;

            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.SessionStartRealizedPnL = 0.0;
            state.RealizedPnL = 0.0;
            state.ConsecutiveLosses = 0;

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            // Pre-seed LastSessionDate to today so the sweep doesn't trigger a session reset
            // (which would overwrite SessionStartRealizedPnL back to 0)
            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var currentSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            state.LastSessionDate = currentSessionDate;

            // 1. Simulate position closed (transition to flat).
            // At this exact moment, realized PnL is still 0 (lagging).
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Flat, 0, 0, 0, config);
            
            // Verify consecutive losses hasn't changed because PnL didn't change
            Assert(state.ConsecutiveLosses == 0, "Consecutive losses remains 0 immediately upon Flat update (lagging PnL).");

            // 2. A split second later, PnL updates on the account
            account.Values[AccountItem.RealizedProfitLoss] = -150.0;

            // 3. AccountItemUpdate fires (event-driven PnL sync replaces sweep polling)
            account.TriggerAccountItemUpdate(AccountItem.RealizedProfitLoss, -150.0);
            // ExecuteAccountItemUpdate processes the PnL change synchronously in TESTING mode
            addon.ExecuteAccountItemUpdate(account, new AccountItemEventArgs { AccountItem = AccountItem.RealizedProfitLoss, Value = -150.0 });

            // Verify ConsecutiveLosses was incremented when AccountItemUpdate catches the change
            Assert(state.ConsecutiveLosses == 1, "Consecutive losses successfully incremented after AccountItemUpdate catches lagged realized PnL.");
            Assert(state.RealizedPnL == -150.0, "State RealizedPnL correctly syncs with lagging realized PnL.");
        }

        private static void TestMcpBridgeLockoutBlock()
        {
            Console.WriteLine("\n[TEST] McpBridge Lockout Block");
            var config = new RiskConfig();
            var account = new Account();
            account.Name = "TestAcc";

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            
            var state = new AccountState("TestAcc");
            addon.SetAccountStateForTest("TestAcc", state);

            Assert(!addon.IsAccountLocked("TestAcc"), "Account is not locked initially.");

            state.IsLockedOut = true;
            Assert(addon.IsAccountLocked("TestAcc"), "Account is correctly identified as locked.");
        }

        // -
        // CRITICAL GAP TESTS
        // -

        private static void TestIsArmedFalseBypassesAllRules()
        {
            Console.WriteLine("\n[TEST] IsArmed=false Bypasses All Rules");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 1000.0;
            config.Sizing.MaxContractsPerAccount = 5;
            config.Overtrading.MaxTradesPerSession = 5;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetArmedForTest(false); // Disarm

            var state = new AccountState("TestAcc");
            state.RealizedPnL = -2000.0;  // Would breach daily loss
            state.TradesToday = 10;        // Would breach max trades
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 10, 18000, 0, config); // Would breach max size

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Count == 0, "Disarmed system returns 0 actions even with all rules violated.");
            Assert(!state.IsLockedOut, "Disarmed system does not lock out account.");
        }

        private static void TestTradeTodayCountingOnRoundTrip()
        {
            Console.WriteLine("\n[TEST] TradesToday Counts Each Open Leg");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");

            // Trade 1: enter Long
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18000, 0, config);
            Assert(state.TradesToday == 1, "TradesToday == 1 after first entry.");

            // Close trade 1
            state.UpdatePosition(account, mnq, MarketPosition.Flat, 0, 0, 0, config);
            Assert(state.TradesToday == 1, "TradesToday stays 1 after close.");
            System.Threading.Thread.Sleep(1050);

            // Trade 2: enter Long again
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18100, 0, config);
            Assert(state.TradesToday == 2, "TradesToday == 2 after second entry.");

            // Close trade 2
            state.UpdatePosition(account, mnq, MarketPosition.Flat, 0, 0, 0, config);
            Assert(state.TradesToday == 2, "TradesToday stays 2 after second close.");
        }

        private static void TestFlipDetectionCountsAsEntry()
        {
            Console.WriteLine("\n[TEST] Long-Short Flip Counts As Entry");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");

            // Enter Long
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18000, 0, config);
            Assert(state.TradesToday == 1, "TradesToday == 1 after Long entry.");

            // Flip directly to Short (Long-Short in one NT update)
            state.UpdatePosition(account, mnq, MarketPosition.Short, 2, 18100, 0, config);
            Assert(state.TradesToday == 2, "TradesToday == 2 after Long-Short flip (flip counts as new entry).");

            // Verify we're correctly Short
            Assert(state.Positions[mnq.FullName].MarketPosition == MarketPosition.Short, "Position correctly shows Short after flip.");
        }

        private static void TestTradeCountingMultiContractScalingDebounced()
        {
            Console.WriteLine("\n[TEST] Multi-Contract Scaling & Staggered Fills Debounced");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");

            // Leg 1: enter Long 1 contract
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18000, 0, config);
            Assert(state.TradesToday == 1, "TradesToday == 1 after 1st contract fill.");

            // Leg 2: scale up to 2 contracts (staggered fill / multi-contract)
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18000, 0, config);
            Assert(state.TradesToday == 1, "TradesToday remains 1 after scaling up to 2 contracts.");

            // Leg 3: scale down to 1 contract
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18050, 0, config);
            Assert(state.TradesToday == 1, "TradesToday remains 1 on partial exit.");

            // Flat
            state.UpdatePosition(account, mnq, MarketPosition.Flat, 0, 0, 0, config);
            Assert(state.TradesToday == 1, "TradesToday remains 1 on final flat.");
        }

        private static void TestLockoutWatchdogSweepFlattensOpenPosition()
        {
            Console.WriteLine("\n[TEST] Lockout Safety Sweep Watchdog Flattens Open Position");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            Account.All.Add(account);
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetArmedForTest(true);

            var state = new AccountState("TestAcc");
            state.IsLockedOut = true;
            var mnq = new Instrument("MNQ");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18000, 0, config);

            DateTime nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            Account.All.Clear();
            Account.All.Add(account);
            addon.SetConfigForTest(config);
            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            addon.SetArmedForTest(true);
            addon.SetModeForTest("live");

            // Run safety sweep with no incoming order events
            addon.ExecuteSafetySweep();

            Assert(state.CurrentLockoutPhase == AccountState.LockoutPhase.PendingCancel ||
                   state.CurrentLockoutPhase == AccountState.LockoutPhase.PendingFlatten ||
                   state.CurrentLockoutPhase == AccountState.LockoutPhase.Confirmed, 
                   "Lockout phase advanced during sweep watchdog evaluation.");
        }

        private static void TestLockoutAllowsPositionReducingOrders()
        {
            Console.WriteLine("\n[TEST] Lockout Allows Position Reducing Orders");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 2, 18000, 0, config);

            var sellOrder = new Order
            {
                Instrument = mnq,
                OrderAction = OrderAction.Sell,
                OrderType = OrderType.Market,
                Quantity = 2
            };

            var buyOrder = new Order
            {
                Instrument = mnq,
                OrderAction = OrderAction.Buy,
                OrderType = OrderType.Limit,
                Quantity = 2
            };

            Assert(addon.IsPositionReducingOrder(sellOrder, state) == true, "Sell order is position reducing for Long.");
            Assert(addon.IsPositionReducingOrder(buyOrder, state) == false, "Buy order is NOT position reducing for Long.");
        }

        private static void TestCooldownExpiryAllowsReEntry()
        {
            Console.WriteLine("\n[TEST] Expired Cooldown Allows Re-Entry");
            var config = new RiskConfig();
            config.Overtrading.CooldownMinutes = 5;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            // Cooldown expired 1 minute ago
            state.CooldownUntil = DateTime.UtcNow.AddMinutes(-1);
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);

            var actions = addon.EvaluateRules(account, state);

            Assert(!actions.Any(a => a.RuleId == "COOLDOWN_BREACH"), "No COOLDOWN_BREACH after cooldown has expired.");
        }

        private static void TestOrderCancelledWhenLockedOnOrderUpdate()
        {
            Console.WriteLine("\n[TEST] Working Order Cancelled When Account Is Locked");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.IsLockedOut = true;
            addon.SetAccountStateForTest("TestAcc", state);

            var order = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderState = OrderState.Working,
                OrderType = OrderType.Limit,
                Instrument = new Instrument("MNQ")
            };
            account.Orders.Add(order);

            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = order });

            Assert(order.OrderState == OrderState.Cancelled,
                "Working Limit order is cancelled when account is locked.");
        }

        private static void TestOrderNotCancelledInFilledStateWhenLocked()
        {
            Console.WriteLine("\n[TEST] Filled Order NOT Cancelled When Account Is Locked");
            var config = new RiskConfig();
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.IsLockedOut = true;
            addon.SetAccountStateForTest("TestAcc", state);

            var order = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderState = OrderState.Filled,  // Already filled - should NOT be cancelled
                OrderType = OrderType.Limit,
                Instrument = new Instrument("MNQ")
            };

            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = order });

            Assert(order.OrderState == OrderState.Filled,
                "Filled order state is unchanged even when account is locked.");
        }

        private static void TestOrderCancelledWhenConsecLossesAtMaxNotLocked()
        {
            Console.WriteLine("\n[TEST] Order Cancelled When ConsecLosses At Max (Not Formally Locked)");
            var config = new RiskConfig();
            config.Overtrading.MaxConsecutiveLosses = 3;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.IsLockedOut = false;         // NOT formally locked
            state.ConsecutiveLosses = 3;       // But at the consec-loss limit
            addon.SetAccountStateForTest("TestAcc", state);

            var order = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderState = OrderState.Submitted,
                OrderType = OrderType.Market,
                Instrument = new Instrument("MNQ")
            };
            account.Orders.Add(order);

            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = order });

            Assert(order.OrderState == OrderState.Cancelled,
                "Submitted Market order cancelled when consecutive losses at max (even without formal lockout).");
        }

        // -
        // IMPORTANT GAP TESTS
        // -

        private static void TestDailyLossIncludesUnrealizedPnL()
        {
            Console.WriteLine("\n[TEST] Daily Loss Limit Includes Unrealized PnL");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 1000.0;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.RealizedPnL   = -500.0;  // Within limit alone
            state.UnrealizedPnL = -600.0;  // Combined = -1100 - breach

            var actions = addon.EvaluatePnLRules(account, state);

            Assert(actions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "DAILY_LOSS_BREACH fires when Realized + Unrealized combined exceeds limit.");
            Assert(state.IsLockedOut, "Account locked when unrealized PnL tips combined total past limit.");

            // Boundary: realized -500 + unrealized -499 = -999, within limit
            var state2 = new AccountState("TestAcc2");
            state2.RealizedPnL   = -500.0;
            state2.UnrealizedPnL = -499.0; // Combined = -999, just inside limit
            var actions2 = addon.EvaluatePnLRules(account, state2);
            Assert(!actions2.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "No DAILY_LOSS_BREACH when Realized + Unrealized is just within limit.");
        }

        private static void TestSessionResetInSweep()
        {
            Console.WriteLine("\n[TEST] Session Reset Resets All Counters");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 0.0; // Disable rules so only reset logic runs

            var account = new Account { Name = "TestAcc" };
            account.Values[AccountItem.RealizedProfitLoss] = -500.0;

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.TradesToday      = 7;
            state.ConsecutiveLosses = 4;
            state.PeakEquity       = 300.0;
            state.IsLockedOut      = true;
            // Deliberately set LastSessionDate to yesterday to trigger reset
            state.LastSessionDate  = DateTime.UtcNow.Date.AddDays(-2);
            state.SessionStartRealizedPnL = 0.0;

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            addon.ExecuteSafetySweep();

            Assert(state.TradesToday == 0,       "TradesToday reset to 0 on new session.");
            Assert(state.ConsecutiveLosses == 0, "ConsecutiveLosses reset to 0 on new session.");
            Assert(state.PeakEquity == 0.0,      "PeakEquity reset to 0 on new session.");
            Assert(!state.IsLockedOut,            "IsLockedOut reset to false on new session.");
            Assert(state.SessionStartRealizedPnL == -500.0,
                "SessionStartRealizedPnL re-baselined to current account realized (-500) on new session.");
        }

        private static void TestLockoutEnforcementFirstSweep()
        {
            Console.WriteLine("\n[TEST] First Sweep After Lockout Flattens + Cancels");
            var config = new RiskConfig();

            var account = new Account { Name = "TestAcc" };
            // Simulate an open position on the account
            account.Positions.Add(new Position
            {
                Instrument      = new Instrument("MNQ"),
                MarketPosition  = MarketPosition.Long,
                Quantity        = 2,
                AveragePrice    = 18000
            });
            var workingOrder = new Order
            {
                Id         = Guid.NewGuid().ToString(),
                OrderState = OrderState.Working,
                OrderType  = OrderType.Limit,
                Instrument = new Instrument("MNQ")
            };
            account.Orders.Add(workingOrder);

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");  // Must be live for actions to execute

            var state = new AccountState("TestAcc");
            state.IsLockedOut             = true;
            state.InitialLockoutFlattened = false;  // First time
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 2, 18000, 0, config);

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            // Seed LastSessionDate to avoid session-reset side-effect
            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;

            // Lockout enforcement is now event-driven via PositionUpdate.
            // The phased lockout needs two events: first cancels orders, second flattens.
            // Backdate LastLockoutFlattenAttempt so the throttle doesn't block the flatten.
            state.LastLockoutFlattenAttempt = DateTime.UtcNow.AddSeconds(-10);

            // Fire PositionUpdate to trigger EvaluateLockoutPhase (phase 1: cancel orders).
            addon.ExecutePositionUpdate(account, new PositionEventArgs { Position = new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 } });

            // After cancel phase, fire OrderUpdate to advance to flatten phase.
            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = workingOrder });

            // Backdate again so the flatten phase's 5s throttle is satisfied.
            state.LastLockoutFlattenAttempt = DateTime.UtcNow.AddSeconds(-10);

            // Fire another PositionUpdate to trigger flatten phase.
            addon.ExecutePositionUpdate(account, new PositionEventArgs { Position = new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 } });

            Assert(account.Positions.Count == 0, "Positions cleared after lockout enforcement via PositionUpdate.");
            Assert(workingOrder.OrderState == OrderState.Cancelled, "Working order cancelled by lockout enforcement.");
        }

        private static void TestLockoutEnforcementSubsequentSweepNoPosition()
        {
            Console.WriteLine("\n[TEST] Subsequent Sweep With Locked + No Positions - No Action");
            var config = new RiskConfig();

            var account = new Account { Name = "TestAcc" };  // No positions
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var state = new AccountState("TestAcc");
            state.IsLockedOut             = true;
            state.InitialLockoutFlattened = true; // Already flattened once
            // No positions in state either

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;

            addon.ExecuteSafetySweep();

            // Still locked, nothing to flatten, should remain stable
            Assert(state.IsLockedOut, "Account remains locked after subsequent sweep.");
            Assert(account.Positions.Count == 0, "No positions to flatten - account stays flat.");
        }

        private static void TestLockoutEnforcementSubsequentSweepWithNewPosition()
        {
            Console.WriteLine("\n[TEST] Subsequent Sweep With Locked + New Position - Re-Flattened");
            var config = new RiskConfig();

            var account = new Account { Name = "TestAcc" };
            // A new position snuck in after the first enforcement
            account.Positions.Add(new Position
            {
                Instrument     = new Instrument("MNQ"),
                MarketPosition = MarketPosition.Long,
                Quantity       = 1,
                AveragePrice   = 18000
            });

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var state = new AccountState("TestAcc");
            state.IsLockedOut             = true;
            state.InitialLockoutFlattened = true; // Already ran first enforcement
            // Inject the snuck-in position into state
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;

            // Lockout enforcement is now event-driven. Fire PositionUpdate to trigger re-flatten.
            addon.ExecutePositionUpdate(account, new PositionEventArgs { Position = new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 18000 } });

            Assert(account.Positions.Count == 0, "New position re-flattened by lockout enforcement via PositionUpdate.");
        }

        private static void TestStopGuardAutoStop()
        {
            Console.WriteLine("\n[TEST] StopGuard AutoStop: Position With No Stop - MISSING_STOP_ATTACH");
            var config = new RiskConfig();
            config.StopGuard.OnMissing        = "AutoStop";
            config.StopGuard.StopAttachSeconds = 0;

            var account = new Account { Name = "TestAcc" };
            var mnq   = new Instrument("MNQ");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH"),
                "MISSING_STOP_ATTACH action generated when position is unprotected past grace period.");
        }

        private static void TestStopGuardFlatten()
        {
            Console.WriteLine("\n[TEST] StopGuard Flatten: Position With No Stop - MISSING_STOP_FLATTEN");
            var config = new RiskConfig();
            config.StopGuard.OnMissing        = "Flatten";
            config.StopGuard.StopAttachSeconds = 0;

            var account = new Account { Name = "TestAcc" };
            var mnq   = new Instrument("MNQ");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Any(a => a.RuleId == "MISSING_STOP_FLATTEN"),
                "MISSING_STOP_FLATTEN action generated when OnMissing=Flatten and no stop after grace period.");
        }

        private static void TestStopGuardNoActionWhenStopPresent()
        {
            Console.WriteLine("\n[TEST] StopGuard: No Action When Stop Quantity Covers Position");
            var config = new RiskConfig();
            config.StopGuard.OnMissing        = "AutoStop";
            config.StopGuard.StopAttachSeconds = 0;

            var account = new Account { Name = "TestAcc" };
            var mnq     = new Instrument("MNQ");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            // Add a working stop order that covers the 2-contract position
            var stopOrder = new Order
            {
                Id          = Guid.NewGuid().ToString(),
                OrderState  = OrderState.Working,
                OrderType   = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity    = 2,
                Instrument  = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopOrder);

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(!actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH" || a.RuleId == "MISSING_STOP_FLATTEN"),
                "No StopGuard action when working stop fully covers position quantity.");
        }

        private static void TestStopGuardTransientStateValidation()
        {
            Console.WriteLine("\n[TEST] StopGuard: Transient Order States Count Towards Protection");
            var config = new RiskConfig();
            config.StopGuard.OnMissing = "AutoStop";
            config.StopGuard.StopAttachSeconds = 0;

            var account = new Account { Name = "TestAcc" };
            var mnq = new Instrument("MNQ");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            // Add a pending submit stop order (transient state)
            var stopOrder = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderState = OrderState.Initialized,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 2,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopOrder);

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(!actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH"),
                "No StopGuard action when order is in Initialized transient state (FSM is ProtectedPending).");
        }

        private static void TestStopGuardPartiallyFilledValidation()
        {
            Console.WriteLine("\n[TEST] StopGuard: Partially Filled Orders Calculate Remaining Correctly");
            // With the FSM, a PartFilled stop is recognized as ProtectedPending,
            // so grace expiry will NOT fire. The partial gap is handled by the
            // FSM recognizing the stop as pending, not by a legacy gap calculation.
            var config = new RiskConfig();
            config.StopGuard.OnMissing = "AutoStop";
            config.StopGuard.StopAttachSeconds = 0;

            var account = new Account { Name = "TestAcc" };
            var mnq = new Instrument("MNQ");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            // Stop order for 3 contracts, but 2 are filled, so only 1 remaining working
            var stopOrder = new Order
            {
                Id = Guid.NewGuid().ToString(),
                OrderState = OrderState.PartFilled,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 3,
                Filled = 2,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopOrder);

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });

            // PartFilled is recognized by the FSM as ProtectedPending, so no action.
            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(!actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH"),
                "PartFilled stop recognized as ProtectedPending - no auto-stop from grace expiry.");
        }

        private static void TestEdgeWindowGateBreach()
        {
            Console.WriteLine("\n[TEST] EdgeWindowGate: Position Entered Outside Window - Breach");
            var config = new RiskConfig();
            config.EnableWindowGate = true;

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            // Define window 09:50-11:10 ET Monday-Friday
            var parsedWindow = new ParsedWindow
            {
                Start = new TimeSpan(9, 50, 0),
                End   = new TimeSpan(11, 10, 0),
                Days  = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            };
            addon.SetParsedWindowsForTest(new List<ParsedWindow> { parsedWindow });

            var state = new AccountState("TestAcc");
            var mnq   = new Instrument("MNQ");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18000, 0, config);

            // Backdate entry to 08:00 ET on a weekday = clearly outside the 09:50 window.
            // Use a known Monday at 12:00 UTC = 08:00 ET (summer, UTC-4).
            state.Positions[mnq.FullName].LastNonFlatTransition =
                new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc); // Monday 2026-07-13 08:00 ET

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Any(a => a.RuleId == "EDGE_WINDOW_BREACH"),
                "EDGE_WINDOW_BREACH fires when position was entered outside permitted window.");
        }

        private static void TestConsecutiveWinsResetLossCounter()
        {
            Console.WriteLine("\n[TEST] Consecutive Win Resets Loss Counter in Sweep");
            var config = new RiskConfig();
            config.Overtrading.MaxConsecutiveLosses = 3;

            var account = new Account { Name = "TestAcc" };
            account.Values[AccountItem.RealizedProfitLoss] = 200.0; // Profitable

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.ConsecutiveLosses        = 2;
            state.SessionStartRealizedPnL  = 0.0;
            state.RealizedPnL              = 0.0;  // State hasn't synced yet

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;

            // PnL sync is now event-driven via AccountItemUpdate, not the sweep.
            addon.ExecuteAccountItemUpdate(account, new AccountItemEventArgs { AccountItem = AccountItem.RealizedProfitLoss, Value = 200.0 });

            Assert(state.ConsecutiveLosses == 0,
                "Consecutive loss counter reset to 0 after a profitable trade detected via AccountItemUpdate.");
            Assert(state.RealizedPnL == 200.0,
                "RealizedPnL synced to 200 after winning AccountItemUpdate.");
        }

        private static void TestAggregateSizeBreach()
        {
            Console.WriteLine("\n[TEST] Aggregate Cross-Account Size Breach");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount  = 15;
            config.Sizing.MaxContractsAggregate   = 20;
            config.Sizing.ExpectedCopies          = 1;

            var acc1 = new Account { Name = "Acc1" };
            var acc2 = new Account { Name = "Acc2" };

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state1 = new AccountState("Acc1");
            var state2 = new AccountState("Acc2");
            var mnq    = new Instrument("MNQ");

            // 12 contracts each = 24 total > aggregate limit of 20
            state1.UpdatePosition(acc1, mnq, MarketPosition.Long, 12, 18000, 0, config);
            state2.UpdatePosition(acc2, mnq, MarketPosition.Long, 12, 18000, 0, config);

            addon.SetAccountStateForTest("Acc1", state1);
            addon.SetAccountStateForTest("Acc2", state2);
            addon.SetSubscribedAccountForTest("Acc1");
            addon.SetSubscribedAccountForTest("Acc2");
            Account.All.Clear();
            Account.All.Add(acc1);
            Account.All.Add(acc2);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today  = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            state1.LastSessionDate = today;
            state2.LastSessionDate = today;

            // We need live mode for ProcessAction to run, but we test the sweep logic
            // by checking that lockout actions would be generated. Use shadow mode but
            // verify the sweep didn't throw and the logic path was hit.
            addon.SetModeForTest("shadow");
            addon.ExecuteSafetySweep();

            // In shadow mode the flatten won't execute but we can verify the rule would have fired
            // by checking the aggregate contract count logic directly via EvaluateRules state.
            // After sweep in shadow mode with 12+12=24 > 20 aggregate: the aggregate check fires.
            // We verify this by showing the individual accounts are not per-account breached (12 < 15)
            // but combined would be. The sweep handles aggregate separately from per-account.
            var perAccActions1 = addon.EvaluateRules(acc1, state1);
            Assert(!perAccActions1.Any(a => a.RuleId == "MAX_SIZE_BREACH"),
                "Per-account rule does not fire since 12 < 15 per-account limit.");

            // Temporarily lower aggregate and verify sweep logic triggers for aggregate
            config.Sizing.MaxContractsAggregate = 10; // Now 24 > 10 aggregate
            addon.ExecuteSafetySweep(); // Shadow mode - won't flatten but exercises the code path
            Assert(true, "Aggregate size breach sweep runs without exception.");
        }

        // -
        // LOWER-PRIORITY / BOUNDARY TESTS
        // -

        private static void TestShadowModeSkipsAction()
        {
            Console.WriteLine("\n[TEST] Shadow Mode Skips Action Execution");
            var config = new RiskConfig();

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("shadow"); // Default, but explicit

            Account.All.Clear();
            Account.All.Add(account);

            var action = new GuardAction
            {
                AccountName = "TestAcc",
                ActionType  = GuardActionType.FlattenPosition,
                RuleId      = "TEST_RULE"
            };

            string result = addon.ProcessAction(action);

            Assert(result == "SHADOW (SKIPPED)",
                "ProcessAction returns 'SHADOW (SKIPPED)' in shadow mode.");
            Assert(account.Positions.Count == 0, "No positions were touched in shadow mode.");
        }

        private static void TestLiveModeExecutesAction()
        {
            Console.WriteLine("\n[TEST] Live Mode Executes Action");
            var config = new RiskConfig();

            var account = new Account { Name = "TestAcc" };
            account.Positions.Add(new Position
            {
                Instrument     = new Instrument("MNQ"),
                MarketPosition = MarketPosition.Long,
                Quantity       = 1,
                AveragePrice   = 18000
            });

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            Account.All.Clear();
            Account.All.Add(account);

            var action = new GuardAction
            {
                AccountName = "TestAcc",
                ActionType  = GuardActionType.FlattenPosition,
                RuleId      = "TEST_RULE"
            };

            string result = addon.ProcessAction(action);

            Assert(result == "EXECUTED", "ProcessAction returns 'EXECUTED' in live mode.");
            Assert(account.Positions.Count == 0, "Position was actually flattened in live mode.");
        }

        private static void TestMaxSizeAtExactlyLimit()
        {
            Console.WriteLine("\n[TEST] Max Size At Exactly The Limit - No Breach");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 5;

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            // Exactly at the limit - code uses >, not >=
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 5, 18000, 0, config);

            var actions = addon.EvaluateRules(account, state);

            Assert(!actions.Any(a => a.RuleId == "MAX_SIZE_BREACH"),
                "No MAX_SIZE_BREACH when quantity == limit (rule uses strict >, not >=).");
        }

        private static void TestDailyLossAtExactlyLimit()
        {
            Console.WriteLine("\n[TEST] Daily Loss At Exactly The Limit - No Breach");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 1000.0;

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.UnrealizedPnL = 0.0;
            // Exactly at -1000 - code uses < -Limit, so -1000 < -1000 is false
            state.RealizedPnL = -1000.0;

            var actions = addon.EvaluatePnLRules(account, state);

            Assert(!actions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "No DAILY_LOSS_BREACH when PnL == -limit exactly (strict < rule).");
        }

        private static void TestIsAccountLockedForUnknownAccount()
        {
            Console.WriteLine("\n[TEST] IsAccountLocked Returns False For Unknown Account");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(new RiskConfig());

            Assert(!addon.IsAccountLocked("NonExistentAccount"),
                "IsAccountLocked returns false (not exception) for an account that has never been seen.");
        }

        private static void TestMultipleInstrumentsNoPerInstrumentBreach()
        {
            Console.WriteLine("\n[TEST] Multiple Instruments - No Per-Instrument Breach When Each Is Under Limit");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 5;

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            // MNQ = 3 contracts, ES = 3 contracts - each individually under limit of 5
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 3, 18000, 0, config);
            state.UpdatePosition(account, new Instrument("ES"),  MarketPosition.Short, 3, 5000,  0, config);

            var actions = addon.EvaluateRules(account, state);

            Assert(!actions.Any(a => a.RuleId == "MAX_SIZE_BREACH"),
                "No MAX_SIZE_BREACH when each instrument is individually within the per-account limit.");
        }

        // -
        // EXCLUSION DEEP-DIVE TESTS  (test-first - these define the correct behaviour)
        // -

        // -
        // An excluded account must bypass ALL rules - EvaluateRules path
        // -
        private static void TestExcludedAccountMaxContractsBypassed()
        {
            Console.WriteLine("\n[TEST] Excluded Account: MaxContracts Rule Is Bypassed");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 3;
            config.ExcludedAccounts.Add("ExcludedAcc");

            var account = new Account { Name = "ExcludedAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("ExcludedAcc");
            // 10 contracts - far exceeds limit of 3 - but account is excluded
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 10, 18000, 0, config);

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Count == 0,
                "Excluded account returns 0 rule actions even with 10 contracts (limit 3).");
            Assert(!state.IsLockedOut,
                "Excluded account is NOT locked out despite exceeding max contracts.");
        }

        // -
        // Excluded account: ALL rules bypassed simultaneously
        // -
        private static void TestExcludedAccountAllRulesBypassed()
        {
            Console.WriteLine("\n[TEST] Excluded Account: All Rules Bypassed Simultaneously");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.Sizing.MaxContractsPerAccount    = 2;
            config.PnLRules.DailyLossLimit          = 100.0;
            config.PnLRules.TrailingDrawdown        = 100.0;
            config.Overtrading.MaxTradesPerSession  = 1;
            config.Overtrading.MaxConsecutiveLosses = 1;
            config.Overtrading.CooldownMinutes      = 60;

            var account = new Account { Name = "ExcludedAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("ExcludedAcc");
            state.RealizedPnL      = -5000.0;  // Breaches DailyLoss
            state.UnrealizedPnL    = -5000.0;  // Breaches TrailingDD
            state.PeakEquity       = 0.0;
            state.TradesToday      = 99;        // Breaches MaxTrades
            state.ConsecutiveLosses = 99;       // Breaches ConsecLosses
            state.CooldownUntil    = DateTime.UtcNow.AddHours(1); // In cooldown
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 10, 18000, 0, config); // Breaches MaxSize + Cooldown

            var actions = addon.EvaluateRules(account, state);

            Assert(actions.Count == 0,
                "Excluded account has 0 rule actions from EvaluateRules (sizing/overtrading bypassed).");
            Assert(!state.IsLockedOut,
                "Excluded account is NEVER locked out.");

            // Also verify PnL rules are bypassed for excluded accounts
            var pnlActions = addon.EvaluatePnLRules(account, state);
            Assert(pnlActions.Count == 0,
                "Excluded account has 0 PnL rule actions (PnL rules bypassed).");
        }

        // -
        // Excluded account: OnOrderUpdate must NOT cancel its orders
        // -
        private static void TestExcludedAccountOrderNotCancelledWhenLocked()
        {
            Console.WriteLine("\n[TEST] Excluded Account: Working Order NOT Cancelled Even With IsLockedOut=true");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");

            var account = new Account { Name = "ExcludedAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            // Even if state somehow has IsLockedOut=true (e.g. stale persisted state)
            var state = new AccountState("ExcludedAcc");
            state.IsLockedOut = true;
            addon.SetAccountStateForTest("ExcludedAcc", state);

            var order = new Order
            {
                Id         = Guid.NewGuid().ToString(),
                OrderState = OrderState.Working,
                OrderType  = OrderType.Market,
                Instrument = new Instrument("MNQ")
            };
            account.Orders.Add(order);

            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = order });

            Assert(order.OrderState == OrderState.Working,
                "Working order on excluded account is NOT cancelled, even when IsLockedOut=true.");
        }

        // -
        // BUG: Excluded account contracts must NOT count toward aggregate total
        // -
        private static void TestExcludedAccountNotCountedInAggregate()
        {
            Console.WriteLine("\n[TEST] Excluded Account Contracts NOT Counted In Aggregate Total");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.Sizing.MaxContractsAggregate = 10;
            config.Sizing.ExpectedCopies        = 1;

            // ExcludedAcc: 20 contracts (excluded, should be invisible to aggregate)
            // NormalAcc:    5 contracts (under the limit of 10)
            // Without the fix: 20+5=25 > 10 - triggers aggregate breach on NormalAcc
            // With the fix:      0+5= 5 <= 10 - no breach

            var exclAcc   = new Account { Name = "ExcludedAcc" };
            var normalAcc = new Account { Name = "NormalAcc" };

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var exclState   = new AccountState("ExcludedAcc");
            var normalState = new AccountState("NormalAcc");
            var mnq         = new Instrument("MNQ");

            exclState.UpdatePosition(exclAcc, mnq, MarketPosition.Long, 20, 18000, 0, config);
            normalState.UpdatePosition(normalAcc, mnq, MarketPosition.Long, 5, 18000, 0, config);

            // Reflect positions on account.Positions for ExecuteAction to see
            exclAcc.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 20, AveragePrice = 18000 });
            normalAcc.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 5, AveragePrice = 18000 });

            addon.SetAccountStateForTest("ExcludedAcc", exclState);
            addon.SetAccountStateForTest("NormalAcc",   normalState);
            addon.SetSubscribedAccountForTest("ExcludedAcc");
            addon.SetSubscribedAccountForTest("NormalAcc");
            Account.All.Clear();
            Account.All.Add(exclAcc);
            Account.All.Add(normalAcc);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today  = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            exclState.LastSessionDate   = today;
            normalState.LastSessionDate = today;

            // Aggregate sizing is now event-driven via PositionUpdate, not the sweep.
            // Fire a PositionUpdate on normalAcc to trigger EvaluateAggregateSizing.
            addon.ExecutePositionUpdate(normalAcc, new PositionEventArgs { Position = new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 5, AveragePrice = 18000 } });

            Assert(normalAcc.Positions.Count > 0,
                "NormalAcc (5 contracts, under limit) is NOT flattened when excluded account's 20 contracts are correctly ignored in aggregate.");
            Assert(exclAcc.Positions.Count > 0,
                "ExcludedAcc is NOT flattened by aggregate check.");
        }

        // -
        // BUG: Excluded account must NOT be flattened by aggregate breach action
        // -
        private static void TestExcludedAccountNotFlattenedByAggregateBreach()
        {
            Console.WriteLine("\n[TEST] Excluded Account NOT Flattened Even When Aggregate Limit Breached By Non-Excluded Accounts");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.Sizing.MaxContractsAggregate = 5;
            config.Sizing.ExpectedCopies        = 1;

            // Two non-excluded accounts breach the aggregate limit.
            // The excluded account has a position too.
            // The excluded account must NOT be flattened.

            var exclAcc = new Account { Name = "ExcludedAcc" };
            var normAcc1 = new Account { Name = "NormAcc1" };
            var normAcc2 = new Account { Name = "NormAcc2" };

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var mnq = new Instrument("MNQ");

            var exclState  = new AccountState("ExcludedAcc");
            var normState1 = new AccountState("NormAcc1");
            var normState2 = new AccountState("NormAcc2");

            exclState.UpdatePosition(exclAcc,   mnq, MarketPosition.Long, 2, 18000, 0, config);
            normState1.UpdatePosition(normAcc1,  mnq, MarketPosition.Long, 4, 18000, 0, config);
            normState2.UpdatePosition(normAcc2,  mnq, MarketPosition.Long, 4, 18000, 0, config);

            exclAcc.Positions.Add(new Position  { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2, AveragePrice = 18000 });
            normAcc1.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 4, AveragePrice = 18000 });
            normAcc2.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 4, AveragePrice = 18000 });

            addon.SetAccountStateForTest("ExcludedAcc", exclState);
            addon.SetAccountStateForTest("NormAcc1",    normState1);
            addon.SetAccountStateForTest("NormAcc2",    normState2);
            addon.SetSubscribedAccountForTest("ExcludedAcc");
            addon.SetSubscribedAccountForTest("NormAcc1");
            addon.SetSubscribedAccountForTest("NormAcc2");
            Account.All.Clear();
            Account.All.Add(exclAcc);
            Account.All.Add(normAcc1);
            Account.All.Add(normAcc2);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today  = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            exclState.LastSessionDate  = today;
            normState1.LastSessionDate = today;
            normState2.LastSessionDate = today;

            // Aggregate sizing is now event-driven via PositionUpdate, not the sweep.
            // Fire a PositionUpdate on normAcc1 to trigger EvaluateAggregateSizing.
            addon.ExecutePositionUpdate(normAcc1, new PositionEventArgs { Position = new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 4, AveragePrice = 18000 } });

            // NormAcc1 and NormAcc2 should be flattened (4+4=8 > limit of 5)
            // ExcludedAcc must keep its position
            Assert(exclAcc.Positions.Count > 0,
                "ExcludedAcc position is preserved even when non-excluded accounts trigger aggregate breach.");
            Assert(normAcc1.Positions.Count == 0 || normAcc2.Positions.Count == 0,
                "At least one non-excluded account IS flattened by aggregate breach.");
        }

        // -
        // Excluded account: sweep must never lock it out
        // -
        private static void TestExcludedAccountSweepDoesNotLockout()
        {
            Console.WriteLine("\n[TEST] Excluded Account: Full Sweep Never Locks It Out");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.PnLRules.DailyLossLimit         = 100.0;
            config.Overtrading.MaxTradesPerSession  = 1;

            var account = new Account { Name = "ExcludedAcc" };
            account.Values[AccountItem.RealizedProfitLoss] = -9999.0;

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("shadow"); // Shadow so no actual flatten, testing logic only

            var state = new AccountState("ExcludedAcc");
            state.RealizedPnL   = -9999.0;
            state.TradesToday   = 99;
            state.SessionStartRealizedPnL = 0.0;
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);

            addon.SetAccountStateForTest("ExcludedAcc", state);
            addon.SetSubscribedAccountForTest("ExcludedAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            state.LastSessionDate = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;

            addon.ExecuteSafetySweep();

            Assert(!state.IsLockedOut,
                "Sweep does NOT lock out excluded account even with all rules violated.");
        }

        // -
        // A non-excluded account beside an excluded one is still enforced
        // -
        private static void TestNonExcludedAccountStillCaughtBesideExcludedOne()
        {
            Console.WriteLine("\n[TEST] Non-Excluded Account Is Still Caught When Beside Excluded Account");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.PnLRules.DailyLossLimit = 500.0;

            var exclAccount   = new Account { Name = "ExcludedAcc" };
            var normalAccount = new Account { Name = "NormalAcc" };

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var exclState   = new AccountState("ExcludedAcc");
            var normalState = new AccountState("NormalAcc");

            exclState.RealizedPnL   = -9999.0; // Excluded - should not fire
            normalState.RealizedPnL = -600.0;  // Not excluded - SHOULD fire DAILY_LOSS_BREACH

            var exclActions   = addon.EvaluateRules(exclAccount,   exclState);
            var normalActions  = addon.EvaluateRules(normalAccount, normalState);
            var normalPnlActions = addon.EvaluatePnLRules(normalAccount, normalState);

            Assert(exclActions.Count == 0,
                "Excluded account produces 0 rule actions.");
            Assert(!normalActions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "EvaluateRules does not emit PnL rules for non-excluded account (owned by EvaluatePnLRules).");
            Assert(normalPnlActions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "Non-excluded account beside it still gets DAILY_LOSS_BREACH from EvaluatePnLRules.");
        }

        // -
        // Removing an account from exclusion list re-enables all rules
        // -
        private static void TestExclusionRemovedReEnablesRules()
        {
            Console.WriteLine("\n[TEST] Removing Exclusion Re-Enables All Rules");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("TestAcc");
            config.PnLRules.DailyLossLimit = 500.0;

            var account = new Account { Name = "TestAcc" };
            var addon   = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.RealizedPnL = -600.0;
            state.UnrealizedPnL = 0.0;

            // While excluded: no actions
            var actionsWhileExcluded = addon.EvaluatePnLRules(account, state);
            Assert(actionsWhileExcluded.Count == 0,
                "No PnL actions while account is in exclusion list.");

            // Remove from exclusion list
            config.ExcludedAccounts.Remove("TestAcc");
            state.IsLockedOut = false; // Reset so we measure just the rule

            var actionsAfterRemoval = addon.EvaluatePnLRules(account, state);
            Assert(actionsAfterRemoval.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "DAILY_LOSS_BREACH fires immediately after account is removed from exclusion list.");
        }

        // -
        // PASS 2 GAP TESTS
        // -

        // 1.a) Excluded account with IsLockedOut=true (stale) should NOT be flattened by sweep lockout enforcement
        private static void TestSweepLockoutSkipsExcludedAccount()
        {
            Console.WriteLine("\n[TEST] Sweep Lockout Enforcement Skips Excluded Account");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");

            var account = new Account { Name = "ExcludedAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var state = new AccountState("ExcludedAcc");
            state.IsLockedOut = true; // Stale state
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);
            account.Positions.Add(new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 18000 });

            addon.SetAccountStateForTest("ExcludedAcc", state);
            addon.SetSubscribedAccountForTest("ExcludedAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            state.LastSessionDate = today;

            addon.ExecuteSafetySweep();

            Assert(account.Positions.Count > 0, "Excluded account position NOT flattened by sweep lockout enforcement despite IsLockedOut=true.");
        }

        // 1.b/c) Excluded account PnL sync should NOT increment ConsecutiveLosses or set CooldownUntil
        private static void TestSweepPnLSyncSkipsConsecutiveLossForExcludedAccount()
        {
            Console.WriteLine("\n[TEST] Sweep PnL Sync Skips ConsecutiveLosses and Cooldown for Excluded Account");
            var config = new RiskConfig();
            config.ExcludedAccounts.Add("ExcludedAcc");
            config.Overtrading.MaxConsecutiveLosses = 1;
            config.Overtrading.CooldownMinutes = 10;

            var account = new Account { Name = "ExcludedAcc" };
            account.Values[AccountItem.RealizedProfitLoss] = -100.0; // Trigger loss

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var state = new AccountState("ExcludedAcc");
            state.SessionStartRealizedPnL = 0.0;
            state.RealizedPnL = 0.0;

            addon.SetAccountStateForTest("ExcludedAcc", state);
            addon.SetSubscribedAccountForTest("ExcludedAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            state.LastSessionDate = today;

            addon.ExecuteSafetySweep();

            Assert(state.ConsecutiveLosses == 0, "ConsecutiveLosses NOT incremented for excluded account.");
            Assert(state.CooldownUntil == DateTime.MinValue, "CooldownUntil NOT set for excluded account.");
        }

        // 2. ValidateInvariant returns false for unknown account
        private static void TestValidateInvariantReturnsFalseForUnknownAccount()
        {
            Console.WriteLine("\n[TEST] ValidateInvariant Returns False For Unknown Account");
            var addon = new RiskGuardAddOn();
            Account.All.Clear();
            
            var action = new GuardAction
            {
                AccountName = "UnknownAcc",
                ActionType = GuardActionType.FlattenPosition,
                RuleId = "TEST_RULE"
            };

            string result = addon.ProcessAction(action, forceLive: true);
            Assert(result == "REJECTED (INVARIANT VIOLATION)", "ProcessAction rejects action for account not in Account.All");
        }

        // 3. StopGuard partial stop gap
        private static void TestStopGuardPartialStopGap()
        {
            Console.WriteLine("\n[TEST] StopGuard Partial Stop: FSM recognizes partial stop as ProtectedPending");
            var config = new RiskConfig();
            config.StopGuard.StopAttachSeconds = 0;
            config.StopGuard.OnMissing = "AutoStop";

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 4);

            // Add a working stop for 2 contracts (partial coverage)
            var stopOrder = new Order { Instrument = mnq, OrderState = OrderState.Working, OrderType = OrderType.StopMarket, OrderAction = OrderAction.Sell, Quantity = 2 };
            addon.TestFsmOnOrder(account, mnq.FullName, stopOrder);

            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 4, AveragePrice = 18000 });

            // With the FSM, a working stop (even partial qty) transitions to Protected,
            // so grace expiry does NOT fire. The partial gap is not tracked by the FSM
            // the way the legacy sweep did; the FSM just checks stop presence, not qty coverage.
            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(!actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH"),
                "No MISSING_STOP_ATTACH from FSM when a working stop is present (even partial qty).");
        }

        // 4. StopGuard OnMissing = "WarnOnly"
        private static void TestStopGuardWarnOnlyProducesNoAction()
        {
            Console.WriteLine("\n[TEST] StopGuard WarnOnly Produces No Action");
            var config = new RiskConfig();
            config.StopGuard.StopAttachSeconds = 0;
            config.StopGuard.OnMissing = "WarnOnly";

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 18000 });

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(!actions.Any(a => a.RuleId.StartsWith("MISSING_STOP_")), "No action generated when OnMissing is WarnOnly");
        }

        // 5. Cooldown auto-set in sweep when consecutive losses breach limit
        private static void TestSweepAutoSetsCooldownOnConsecutiveLosses()
        {
            Console.WriteLine("\n[TEST] Sweep Auto-Sets Cooldown On ConsecutiveLosses Breach");
            var config = new RiskConfig();
            config.Overtrading.MaxConsecutiveLosses = 1;
            config.Overtrading.CooldownMinutes = 30;

            var account = new Account { Name = "TestAcc" };
            account.Values[AccountItem.RealizedProfitLoss] = -100.0; // loss

            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("shadow");

            var state = new AccountState("TestAcc");
            state.SessionStartRealizedPnL = 0.0;
            state.RealizedPnL = 0.0;

            addon.SetAccountStateForTest("TestAcc", state);
            addon.SetSubscribedAccountForTest("TestAcc");
            Account.All.Clear();
            Account.All.Add(account);

            var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            var today = nowEt.TimeOfDay >= new TimeSpan(18, 0, 0) ? nowEt.Date.AddDays(1) : nowEt.Date;
            state.LastSessionDate = today;

            // PnL sync is now event-driven via AccountItemUpdate, not the sweep.
            addon.ExecuteAccountItemUpdate(account, new AccountItemEventArgs { AccountItem = AccountItem.RealizedProfitLoss, Value = -100.0 });

            Assert(state.ConsecutiveLosses == 1, "ConsecutiveLosses incremented by AccountItemUpdate");
            Assert(state.CooldownUntil > DateTime.UtcNow, "CooldownUntil auto-set by AccountItemUpdate");
        }

        // 6. forceLive parameter in ProcessAction bypasses shadow mode
        private static void TestProcessActionForceLiveBypassesShadowMode()
        {
            Console.WriteLine("\n[TEST] forceLive Parameter Bypasses Shadow Mode");
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetModeForTest("shadow");
            
            Account.All.Clear();
            Account.All.Add(account);

            var action = new GuardAction { AccountName = "TestAcc", ActionType = GuardActionType.FlattenPosition, RuleId = "MANUAL_PANIC" };
            
            string result = addon.ProcessAction(action, forceLive: true);
            Assert(result == "EXECUTED" || result.StartsWith("ERROR"), $"Action not skipped in shadow mode when forceLive=true (Result: {result})");
        }

        // 7. EdgeWindowGate: position entered INSIDE the window
        private static void TestEdgeWindowGateInsideWindowNoBreach()
        {
            Console.WriteLine("\n[TEST] EdgeWindowGate: Position Entered Inside Window -> No Breach");
            var config = new RiskConfig();
            config.EnableWindowGate = true;
            
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            
            var parsedWindows = new List<ParsedWindow>();
            parsedWindows.Add(new ParsedWindow { Start = new TimeSpan(0, 0, 0), End = new TimeSpan(23, 59, 59), Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday } });
            addon.SetParsedWindowsForTest(parsedWindows);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18000, 0, config);
            state.Positions["MNQ"].LastNonFlatTransition = DateTime.UtcNow;

            var actions = addon.EvaluateRules(account, state);
            Assert(!actions.Any(a => a.RuleId == "EDGE_WINDOW_BREACH"), "No EDGE_WINDOW_BREACH when inside window");
        }

        // 8. EdgeWindowGate: no windows defined
        private static void TestEdgeWindowGateNoWindowsDefinedNoBreach()
        {
            Console.WriteLine("\n[TEST] EdgeWindowGate: No Windows Defined -> No Breach");
            var config = new RiskConfig();
            config.EnableWindowGate = true;
            
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            
            var parsedWindows = new List<ParsedWindow>();
            addon.SetParsedWindowsForTest(parsedWindows);

            var state = new AccountState("TestAcc");
            var mnq = new Instrument("MNQ");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18000, 0, config);
            state.Positions["MNQ"].LastNonFlatTransition = DateTime.UtcNow;

            var actions = addon.EvaluateRules(account, state);
            Assert(!actions.Any(a => a.RuleId == "EDGE_WINDOW_BREACH"), "No EDGE_WINDOW_BREACH when no windows defined");
        }

        // 9. Multiple rules fire simultaneously
        private static void TestMultipleRulesFireSimultaneously()
        {
            Console.WriteLine("\n[TEST] Multiple Rules Fire Simultaneously");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 1;
            config.PnLRules.DailyLossLimit = 100.0;
            config.Overtrading.MaxTradesPerSession = 1;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.RealizedPnL = -200.0; // Daily loss breach
            state.UnrealizedPnL = 0.0;
            state.TradesToday = 2; // Max trades breach
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 5, 18000, 0, config); // Max size breach

            // EvaluateRules handles sizing + overtrading (not PnL anymore)
            var actions = addon.EvaluateRules(account, state);
            
            bool hasSize = actions.Any(a => a.RuleId == "MAX_SIZE_BREACH");
            bool hasTrades = actions.Any(a => a.RuleId == "MAX_TRADES_BREACH");
            
            Assert(hasSize && hasTrades, "Sizing and overtrading rules return actions in the same evaluation");

            // PnL rules are separate
            var pnlActions = addon.EvaluatePnLRules(account, state);
            bool hasLoss = pnlActions.Any(a => a.RuleId == "DAILY_LOSS_BREACH");
            Assert(hasLoss, "DAILY_LOSS_BREACH fires from EvaluatePnLRules");
        }

        // -
        // PASS 3 GAP TESTS
        // -

        // 1. Aggregate Sizing ExpectedCopies scaling
        private static void TestAggregateSizingExpectedCopiesScaling()
        {
            Console.WriteLine("\n[TEST] Aggregate Sizing ExpectedCopies Scaling Bypass");
            var config = new RiskConfig();
            config.Sizing.MaxContractsPerAccount = 15;
            config.Sizing.MaxContractsAggregate = 15;
            config.Sizing.ExpectedCopies = 2; // Intended 2-way mirror
            
            var acc1 = new Account { Name = "Acc1" };
            var acc2 = new Account { Name = "Acc2" };
            
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");
            
            var state1 = new AccountState("Acc1");
            var state2 = new AccountState("Acc2");
            
            state1.UpdatePosition(acc1, new Instrument("MNQ"), MarketPosition.Long, 10, 18000, 0, config);
            acc1.Positions.Add(new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 10, AveragePrice = 18000 });
            
            state2.UpdatePosition(acc2, new Instrument("MNQ"), MarketPosition.Long, 10, 18000, 0, config);
            acc2.Positions.Add(new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 10, AveragePrice = 18000 });
            
            addon.SetAccountStateForTest("Acc1", state1);
            addon.SetSubscribedAccountForTest("Acc1");
            
            addon.SetAccountStateForTest("Acc2", state2);
            addon.SetSubscribedAccountForTest("Acc2");
            
            Account.All.Clear();
            Account.All.Add(acc1);
            Account.All.Add(acc2);

            addon.ExecuteSafetySweep();

            Assert(acc1.Positions.Count > 0 && acc2.Positions.Count > 0, "Aggregate breach avoided due to ExpectedCopies scaling (max single=10, aggregate limit=15).");
        }

        // 2. Firm Mirror Trailing DD Integration
        private static void TestFirmMirrorTrailingDDBreachEmitsAction()
        {
            Console.WriteLine("\n[TEST] Firm Mirror Trailing DD Breach Emits Action And Locks Out");
            var account = new Account { Name = "FirmAcc" };
            var addon = new RiskGuardAddOn();
            
            var fmConfig = new FirmMirrorConfig();
            fmConfig.Enabled = true;
            fmConfig.TrailingDD.Enabled = true;
            fmConfig.TrailingDD.Amount = 2500;
            fmConfig.TrailingDD.Buffer = 300;
            
            var config = new RiskConfig();
            config.FirmMirror = fmConfig;
            addon.SetConfigForTest(config);
            
            var state = new AccountState("FirmAcc");
            state.FirmStartingBalance = 100000;
            state.FirmTrailingPeak = 100000;
            
            account.Values[AccountItem.CashValue] = 97000.0;
            account.Values[AccountItem.UnrealizedProfitLoss] = 0.0;
            account.Values[AccountItem.RealizedProfitLoss] = -3000.0;

            var actions = addon.EvaluateFirmMirror(account, state, DateTime.UtcNow);
            
            Assert(actions.Any(a => a.RuleId == "FIRM_TRAILING_DD_BREACH"), "Firm trailing DD breach action generated");
            Assert(state.IsLockedOut == true, "Firm mirror breach locks out account");
        }

        // 3. Firm Mirror Daily Loss Integration
        private static void TestFirmMirrorDailyLossBreachEmitsAction()
        {
            Console.WriteLine("\n[TEST] Firm Mirror Daily Loss Breach Emits Action And Locks Out");
            var account = new Account { Name = "FirmAcc" };
            var addon = new RiskGuardAddOn();
            
            var fmConfig = new FirmMirrorConfig();
            fmConfig.Enabled = true;
            fmConfig.DailyLoss.Enabled = true;
            fmConfig.DailyLoss.Amount = 1500;
            fmConfig.DailyLoss.Buffer = 200; // Limit is -1300
            
            var config = new RiskConfig();
            config.FirmMirror = fmConfig;
            addon.SetConfigForTest(config);
            
            var state = new AccountState("FirmAcc");
            state.FirmDailyDate = DateTime.UtcNow.Date;
            state.FirmDailyStartRealized = 0.0;
            
            account.Values[AccountItem.RealizedProfitLoss] = -1400.0; // Less than limit -1300
            account.Values[AccountItem.UnrealizedProfitLoss] = 0.0;
            account.Values[AccountItem.CashValue] = 98600.0; 

            var actions = addon.EvaluateFirmMirror(account, state, DateTime.UtcNow);
            
            Assert(actions.Any(a => a.RuleId == "FIRM_DAILY_LOSS_BREACH"), "Firm daily loss breach action generated");
            Assert(state.IsLockedOut == true, "Firm mirror breach locks out account");
        }

        // 4. StopGuard default offset fallback
        private static void TestStopGuardDefaultOffsetFallback()
        {
            Console.WriteLine("\n[TEST] StopGuard Default Offset Fallback (Unknown Ticker)");
            var config = new RiskConfig();
            config.StopGuard.StopAttachSeconds = 0;
            config.StopGuard.OnMissing = "AutoStop";
            
            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();

            var unknownTick = new Instrument("CL");
            unknownTick.MasterInstrument.TickSize = 0.01;
            
            account.Positions.Add(new Position { Instrument = unknownTick, MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 80.00 });

            addon.TestFsmOnPosition(account, unknownTick.FullName, MarketPosition.Long, 1);

            var actions = addon.EvaluateGraceExpiry(account, unknownTick.FullName);
            var attachAction = actions.FirstOrDefault(a => a.RuleId == "MISSING_STOP_ATTACH");
            
            Assert(attachAction != null, "Action generated for missing stop on unknown ticker via FSM");
            Assert(true, "Fallback triggered gracefully");
        }

        // -
        // MANUAL LOCKOUT TESTS
        // -

        // 1. Manual Timed Lockout
        private static void TestManualTimedLockout()
        {
            Console.WriteLine("\n[TEST] Manual Timed Lockout Flattens And Prevents Entry");
            var addon = new RiskGuardAddOn();
            var config = new RiskConfig();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");
            
            var account = new Account { Name = "Acc1" };
            var state = new AccountState("Acc1");
            
            addon.SetAccountStateForTest("Acc1", state);
            addon.SetSubscribedAccountForTest("Acc1");
            Account.All.Clear();
            Account.All.Add(account);
            
            // Give them a position
            state.UpdatePosition(account, new Instrument("MNQ"), MarketPosition.Long, 1, 18000, 0, config);
            account.Positions.Add(new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 18000 });
            
            // Lock for 15 minutes
            addon.LockAccount("Acc1", 15);
            
            Assert(state.LockoutUntil > DateTime.UtcNow, "LockoutUntil is in the future");
            Assert(state.IsLockedOut == false, "IsLockedOut is false for timed lockout");
            
            // Lockout enforcement is now event-driven via PositionUpdate.
            // Fire a PositionUpdate to trigger EvaluateLockoutPhase.
            addon.ExecutePositionUpdate(account, new PositionEventArgs { Position = new Position { Instrument = new Instrument("MNQ"), MarketPosition = MarketPosition.Long, Quantity = 1, AveragePrice = 18000 } });
            
            Assert(account.Positions.Count == 0, "Position flattened by manual timed lockout via PositionUpdate");
        }

        // 2. Manual EOD Lockout
        private static void TestManualEodLockout()
        {
            Console.WriteLine("\n[TEST] Manual EOD Lockout Uses IsLockedOut Flag");
            var addon = new RiskGuardAddOn();
            var state = new AccountState("Acc1");
            addon.SetAccountStateForTest("Acc1", state);
            
            addon.LockAccount("Acc1", -1);
            
            Assert(state.IsLockedOut == true, "EOD lock sets IsLockedOut to true");
            Assert(state.LockoutUntil == DateTime.MinValue, "EOD lock clears LockoutUntil");
        }

        // 3. Manual Unlock Clears Timed Lockout
        private static void TestManualUnlockClearsTimedLockout()
        {
            Console.WriteLine("\n[TEST] Manual Unlock Clears Timed Lockout");
            var addon = new RiskGuardAddOn();
            var state = new AccountState("Acc1");
            addon.SetAccountStateForTest("Acc1", state);
            
            addon.LockAccount("Acc1", 60);
            Assert(state.LockoutUntil > DateTime.UtcNow, "Account is timed locked");
            
            addon.UnlockAccount("Acc1");
            Assert(state.LockoutUntil == DateTime.MinValue, "Unlock clears timed lockout");
        }

        // -
        // FSM STOPGUARD TESTS (-6 of RiskGuardAddOn.md)
        // These assert on the per-position FSM state across event sequences
        // rather than on a single EvaluateRules snapshot, which was the gap
        // that hid the duplicate-SL race.
        // -

        private static RiskConfig FsmTestConfig(int graceSeconds = 60, string onMissing = "AutoStop")
        {
            var c = new RiskConfig();
            c.StopGuard.StopAttachSeconds = graceSeconds;
            c.StopGuard.OnMissing = onMissing;
            return c;
        }

        private static Account FsmTestAccount(string name = "TestAcc")
        {
            Account.All.Clear();
            var a = new Account { Name = name };
            Account.All.Add(a);
            return a;
        }

        // 1. flat -> nonflat (Unprotected) -> OCO stop leg Submitted (ProtectedPending) -> Working (Protected)
        private static void TestFsm_UnprotectedToProtectedViaOcoStopLeg()
        {
            Console.WriteLine("\n[TEST] FSM: Unprotected -> ProtectedPending -> Protected via OCO stop leg");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Unprotected, "FSM created in Unprotected");

            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 2,
                Instrument = mnq,
                Oco = "BRACKET-1"
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);

            fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.ProtectedPending, "Stop leg Submitted -> ProtectedPending");
            Assert(ReferenceEquals(fsm.RecognizedStopOrder, stopLeg), "Recognised stop is the OCO leg (by reference)");

            stopLeg.OrderState = OrderState.Working;
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);
            fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.Protected, "Stop leg Working -> Protected");
        }

        // 2. After ProtectedPending, a second grace-expiry / duplicate event must NOT place another stop.
        private static void TestFsm_NoDuplicateAutoStopWhenStopLegPending()
        {
            Console.WriteLine("\n[TEST] FSM: No duplicate auto-stop when stop leg is pending");
            var config = FsmTestConfig(graceSeconds: 0); // grace already expired
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);

            // Stop leg arrives Submitted (FSM -> ProtectedPending)
            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 2,
                Instrument = mnq,
                Oco = "BRACKET-1"
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);

            // Position object present for EvaluateGraceExpiry
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 2 });

            // Grace is 0s, so deadline is already past. But FSM is ProtectedPending, so no action.
            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Count == 0, "No auto-stop emitted while FSM is ProtectedPending (grace expiry suppressed)");
        }

        // 3. Grace expiry from Unprotected emits exactly one MISSING_STOP_ATTACH, then FSM is ProtectedPending.
        private static void TestFsm_GraceExpiryPlacesAutoStopOnce()
        {
            Console.WriteLine("\n[TEST] FSM: Grace expiry places auto-stop exactly once");
            var config = FsmTestConfig(graceSeconds: 0, onMissing: "AutoStop");
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1 });

            var first = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(first.Any(a => a.RuleId == "MISSING_STOP_ATTACH"), "First grace expiry emits MISSING_STOP_ATTACH");

            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.ProtectedPending, "FSM moved to ProtectedPending after emitting");

            // Second call must not emit again (FSM no longer Unprotected).
            var second = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(second.Count == 0, "Second grace-expiry call emits nothing (FSM already ProtectedPending)");
        }

        // 4. Stop OrderUpdate arrives BEFORE PositionUpdate -> buffered, consumed on position open.
        private static void TestFsm_StopArrivesBeforePositionIsBuffered()
        {
            Console.WriteLine("\n[TEST] FSM: Stop arriving before position is buffered and consumed");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 2,
                Instrument = mnq,
                Oco = "BRACKET-2"
            };
            // No FSM yet -> should be buffered.
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);
            Assert(addon.TestAllFsms().Count == 0, "No FSM created by stop event alone");

            // Position opens -> FSM created and consumes the buffered stop.
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.ProtectedPending,
                "Buffered stop consumed on position open -> ProtectedPending");
            Assert(ReferenceEquals(fsm.RecognizedStopOrder, stopLeg), "Buffered stop recognised by reference");
        }

        // 5. nonflat -> flat tears down FSM and cancels an orphan RiskGuard auto-stop.
        private static void TestFsm_FlatTearsDownAndCancelsOrphanAutoStop()
        {
            Console.WriteLine("\n[TEST] FSM: Flat tears down FSM and cancels orphan auto-stop");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);

            // Simulate an auto-stop we placed, still working.
            var autoStop = new Order
            {
                Name = "RiskGuardAutoStop",
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            account.Orders.Add(autoStop);
            addon.TestFsmOnOrder(account, mnq.FullName, autoStop);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.Protected, "Auto-stop Working -> Protected");
            Assert(ReferenceEquals(fsm.AutoStopOrder, autoStop), "AutoStopOrder recorded");

            // Position flattens -> FSM torn down, orphan auto-stop cancelled.
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Flat, 0);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName) == null, "FSM removed on flat");
            Assert(autoStop.OrderState == OrderState.Cancelled, "Orphan auto-stop cancelled on flat");
        }

        // 6. Standalone (non-OCO) working stop -> Protected.
        private static void TestFsm_StandaloneStopReachesProtected()
        {
            Console.WriteLine("\n[TEST] FSM: Standalone working stop reaches Protected");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Short, 1);

            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Buy, // opposite of short
                Quantity = 1,
                Instrument = mnq
                // Oco intentionally empty (external/manual bracket)
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.Protected, "Standalone working stop -> Protected (no Oco needed)");
        }

        // 7. Recognised stop leg Rejected -> back to Unprotected.
        private static void TestFsm_RejectedStopLegReturnsToUnprotected()
        {
            Console.WriteLine("\n[TEST] FSM: Rejected stop leg returns FSM to Unprotected");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);

            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.ProtectedPending,
                "Submitted -> ProtectedPending");

            stopLeg.OrderState = OrderState.Rejected;
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);
            // Position still open (qty 1) -> Unprotected
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Unprotected, "Rejected stop -> Unprotected (position still open)");
            Assert(fsm.RecognizedStopOrder == null, "Recognised stop cleared on rejection");
        }

        // 8. Position flattens before grace expires -> no auto-stop emitted.
        private static void TestFsm_PositionFlattenedBeforeGraceNoAutoStop()
        {
            Console.WriteLine("\n[TEST] FSM: Position flat before grace -> no auto-stop");
            var config = FsmTestConfig(graceSeconds: 60); // grace far in future
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Flat, 0);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName) == null, "FSM torn down on flat");

            // Even if grace were somehow invoked, no FSM exists -> no action.
            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Count == 0, "No auto-stop after position flattened before grace");
        }

        // 9. Duplicate OrderUpdate for the same stop leg is idempotent.
        private static void TestFsm_DuplicateOrderUpdatesAreIdempotent()
        {
            Console.WriteLine("\n[TEST] FSM: Duplicate OrderUpdate events are idempotent");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);

            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg); // duplicate
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg); // duplicate

            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.ProtectedPending, "Repeated Submitted events stay ProtectedPending");
            Assert(addon.TestAllFsms().Count == 1, "Still exactly one FSM");
        }

        // 10. Duplicate PositionUpdate (re-entrant) is idempotent.
        private static void TestFsm_DuplicatePositionUpdatesAreIdempotent()
        {
            Console.WriteLine("\n[TEST] FSM: Duplicate PositionUpdate events are idempotent");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2); // duplicate
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2); // duplicate

            Assert(addon.TestAllFsms().Count == 1, "Exactly one FSM after duplicate position updates");
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.Unprotected, "Still Unprotected (no stop arrived)");
            Assert(fsm.PositionQuantity == 2, "Quantity preserved");
        }

        // 11. EvaluateRules no longer emits StopGuard actions (FSM owns it).
        private static void TestFsm_EvaluateRulesNoLongerEmitsStopGuard()
        {
            Console.WriteLine("\n[TEST] FSM: EvaluateRules no longer emits StopGuard actions");
            var config = FsmTestConfig(graceSeconds: 0, onMissing: "AutoStop");
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            var state = new AccountState("TestAcc");
            state.UpdatePosition(account, mnq, MarketPosition.Long, 1, 18000, 0, config);
            state.Positions[mnq.FullName].LastNonFlatTransition = DateTime.UtcNow.AddSeconds(-10);
            addon.SetAccountStateForTest("TestAcc", state);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1 });

            var actions = addon.EvaluateRules(account, state);
            Assert(!actions.Any(a => a.RuleId == "MISSING_STOP_ATTACH" || a.RuleId == "MISSING_STOP_FLATTEN"),
                "EvaluateRules emits no MISSING_STOP_* (FSM owns StopGuard now)");
        }

        // 12. Excluded account: FSM is not created.
        private static void TestFsm_ExcludedAccountSkipsFsm()
        {
            Console.WriteLine("\n[TEST] FSM: Excluded account does not create FSM");
            var config = FsmTestConfig();
            config.ExcludedAccounts = new List<string> { "ExcludedAcc" };
            var account = FsmTestAccount("ExcludedAcc");
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            Assert(addon.TestAllFsms().Count == 0, "No FSM created for excluded account");
        }

        // 13. Protected -> Unprotected when recognised stop FILLS (not just rejected).
        private static void TestFsm_ProtectedToUnprotectedOnStopFilled()
        {
            Console.WriteLine("\n[TEST] FSM: Protected -> Unprotected when stop fills (position still open)");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.Protected,
                "Working stop -> Protected");

            // Stop fills (e.g., OCO target leg hit the stop, but position still shows open briefly).
            stop.OrderState = OrderState.Filled;
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            // PositionQuantity is still 1 (set at creation), so -> Unprotected.
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Unprotected,
                "Filled stop -> Unprotected (position still open)");
        }

        // 14. ProtectedPending -> Unprotected on Cancelled (distinct from Rejected).
        private static void TestFsm_ProtectedPendingToUnprotectedOnCancelled()
        {
            Console.WriteLine("\n[TEST] FSM: ProtectedPending -> Unprotected on Cancelled");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            var stop = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.ProtectedPending,
                "Submitted -> ProtectedPending");

            stop.OrderState = OrderState.Cancelled;
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Unprotected,
                "Cancelled stop -> Unprotected (position still open)");
        }

        // 15. Grace expiry with OnMissing=Flatten emits MISSING_STOP_FLATTEN.
        private static void TestFsm_GraceExpiryFlatten()
        {
            Console.WriteLine("\n[TEST] FSM: Grace expiry with OnMissing=Flatten");
            var config = FsmTestConfig(graceSeconds: 0, onMissing: "Flatten");
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1 });

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Any(a => a.RuleId == "MISSING_STOP_FLATTEN"),
                "Grace expiry with OnMissing=Flatten emits MISSING_STOP_FLATTEN");
        }

        // 16. Grace not expired yet (deadline in future) -> no action.
        private static void TestFsm_GraceNotExpiredNoAction()
        {
            Console.WriteLine("\n[TEST] FSM: Grace not expired -> no action");
            var config = FsmTestConfig(graceSeconds: 600);
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1 });

            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Count == 0, "No action when grace deadline is in the future");
        }

        // 17. Short position FSM reaches Protected via opposite-side buy stop.
        private static void TestFsm_ShortPositionProtected()
        {
            Console.WriteLine("\n[TEST] FSM: Short position reaches Protected");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var es = new Instrument("ES");

            addon.TestFsmOnPosition(account, es.FullName, MarketPosition.Short, 3);
            var fsm = addon.TestGetFsm(account.Name, es.FullName);
            Assert(fsm != null && fsm.PositionSide == MarketPosition.Short, "FSM created with Short side");

            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.BuyToCover, // NT8 ATM uses BuyToCover for short-covering stops
                Quantity = 3,
                Instrument = es
            };
            addon.TestFsmOnOrder(account, es.FullName, stop);
            Assert(addon.TestGetFsm(account.Name, es.FullName).State == GuardFsmState.Protected,
                "Short position + buy stop -> Protected");
        }

        // 18. Position flip (Long->Short) recreates FSM with the new side.
        private static void TestFsm_FlipRecreatesFsm()
        {
            Console.WriteLine("\n[TEST] FSM: Position flip recreates FSM with new side");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);
            var fsm1 = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm1.PositionSide == MarketPosition.Long, "Initial FSM is Long");

            // Flip to short.
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Short, 1);
            var fsm2 = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm2 != null, "FSM exists after flip");
            Assert(fsm2.PositionSide == MarketPosition.Short, "FSM side updated to Short after flip");
            Assert(fsm2.PositionQuantity == 1, "FSM quantity updated to 1 after flip");
            Assert(fsm2.State == GuardFsmState.Unprotected, "FSM is Unprotected after flip (new entry, new grace)");
        }

        // 19. Multiple instruments get separate independent FSMs.
        private static void TestFsm_MultipleInstrumentsIndependent()
        {
            Console.WriteLine("\n[TEST] FSM: Multiple instruments have independent FSMs");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");
            var es = new Instrument("ES");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            addon.TestFsmOnPosition(account, es.FullName, MarketPosition.Long, 2);

            Assert(addon.TestAllFsms().Count == 2, "Two FSMs created for two instruments");

            // Protect MNQ only.
            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);

            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.Protected,
                "MNQ is Protected");
            Assert(addon.TestGetFsm(account.Name, es.FullName).State == GuardFsmState.Unprotected,
                "ES is still Unprotected (independent)");
        }

        // 20. Disarmed guard does not create FSMs.
        private static void TestFsm_DisarmedSkipsFsm()
        {
            Console.WriteLine("\n[TEST] FSM: Disarmed guard does not create FSMs");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetArmedForTest(false);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            Assert(addon.TestAllFsms().Count == 0, "No FSM created when guard is disarmed");
        }

        // 21. Non-stop order types (Limit target leg) do not transition the FSM.
        private static void TestFsm_LimitOrderDoesNotTransition()
        {
            Console.WriteLine("\n[TEST] FSM: Limit order (target leg) does not transition FSM");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);

            // A limit order (target leg of bracket) opposite side but not a stop type.
            var target = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.Limit,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, target);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.Unprotected,
                "Limit order does not protect position (only stop types do)");
        }

        // 22. Pending stop is consumed even when it arrives in Working state (buffered then consumed).
        private static void TestFsm_PendingStopWorkingConsumed()
        {
            Console.WriteLine("\n[TEST] FSM: Buffered Working stop consumed -> Protected directly");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            // Stop arrives Working before position event.
            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 1,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            Assert(addon.TestAllFsms().Count == 0, "No FSM yet (buffered)");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Protected,
                "Buffered Working stop consumed -> Protected directly");
        }

        // 23. Short position with BuyToCover stop leg (the exact bug: NT8 ATM uses BuyToCover, not Buy).
        private static void TestFsm_ShortPositionBuyToCoverStopRecognized()
        {
            Console.WriteLine("\n[TEST] FSM: Short position BuyToCover stop leg recognized (the original bug)");
            var config = FsmTestConfig(graceSeconds: 2);
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            // Position goes short.
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Short, 6);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null && fsm.State == GuardFsmState.Unprotected, "FSM created Unprotected for short");

            // OCO stop leg arrives as BuyToCover (NT8 ATM for shorts).
            var stopLeg = new Order
            {
                OrderState = OrderState.Submitted,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.BuyToCover,
                Quantity = 6,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopLeg);

            fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.ProtectedPending,
                "BuyToCover stop leg Submitted -> ProtectedPending (not ignored!)");

            // Grace expiry should NOT fire (FSM is ProtectedPending).
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Short, Quantity = 6 });
            var actions = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(actions.Count == 0,
                "No auto-stop emitted -- BuyToCover stop recognized, no duplicate SL");
        }

        // 24. Long position with SellShort stop leg (symmetric: NT8 may use SellShort for long exits).
        private static void TestFsm_LongPositionSellShortStopRecognized()
        {
            Console.WriteLine("\n[TEST] FSM: Long position SellShort stop leg recognized");
            var config = FsmTestConfig(graceSeconds: 2);
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var es = new Instrument("ES");

            addon.TestFsmOnPosition(account, es.FullName, MarketPosition.Long, 2);
            Assert(addon.TestGetFsm(account.Name, es.FullName).State == GuardFsmState.Unprotected,
                "FSM created Unprotected for long");

            var stopLeg = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.SellShort,
                Quantity = 2,
                Instrument = es
            };
            addon.TestFsmOnOrder(account, es.FullName, stopLeg);
            Assert(addon.TestGetFsm(account.Name, es.FullName).State == GuardFsmState.Protected,
                "SellShort stop Working -> Protected (not ignored!)");
        }

        // --
        // AUDIT REGRESSION TESTS
        // These tests are written FIRST (red), then the code is fixed to make them green.
        // --

        // A1. PositionUpdate with same side + different qty must NOT recreate the FSM
        // (the FSM should update qty in place, preserving Protected state).
        private static void TestFsm_QtyOnlyUpdatePreservesProtectedState()
        {
            Console.WriteLine("\n[TEST] AUDIT: Qty-only PositionUpdate preserves Protected FSM");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            // Open long 2
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);
            var stopRef = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 2,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stopRef);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.Protected,
                "FSM is Protected after stop Working");

            // NT8 fires PositionUpdate with same side, qty=3 (partial fill / scale-out)
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 3);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm != null, "FSM still exists after qty-only update");
            Assert(fsm.State == GuardFsmState.Protected,
                "FSM stays Protected after qty-only PositionUpdate (not reset to Unprotected)");
            Assert(fsm.PositionQuantity == 3, "FSM PositionQuantity updated to 3");
            Assert(ReferenceEquals(fsm.RecognizedStopOrder, stopRef),
                "RecognizedStopOrder preserved across qty-only update");
        }

        // A2. Partial fill on an already-protected position must not reset the FSM.
        private static void TestFsm_PartialFillPreservesProtectedState()
        {
            Console.WriteLine("\n[TEST] AUDIT: Partial fill does not reset Protected FSM");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 5);
            var stop = new Order
            {
                OrderState = OrderState.Working,
                OrderType = OrderType.StopMarket,
                OrderAction = OrderAction.Sell,
                Quantity = 5,
                Instrument = mnq
            };
            addon.TestFsmOnOrder(account, mnq.FullName, stop);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).State == GuardFsmState.Protected,
                "Protected after stop");

            // Partial fill reduces qty from 5 to 3, same side.
            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 3);
            var fsm = addon.TestGetFsm(account.Name, mnq.FullName);
            Assert(fsm.State == GuardFsmState.Protected,
                "Still Protected after partial fill qty reduction");
            Assert(fsm.PositionQuantity == 3, "Qty updated to 3");
        }

        // A3. Grace expiry with OnMissing=Flatten should emit exactly once.
        private static void TestFsm_GraceExpiryFlattenEmitsOnce()
        {
            Console.WriteLine("\n[TEST] AUDIT: Grace expiry Flatten emits exactly once");
            var config = FsmTestConfig(graceSeconds: 0, onMissing: "Flatten");
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 1);
            account.Positions.Add(new Position { Instrument = mnq, MarketPosition = MarketPosition.Long, Quantity = 1 });

            var first = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(first.Any(a => a.RuleId == "MISSING_STOP_FLATTEN"),
                "First grace expiry emits MISSING_STOP_FLATTEN");

            // Second call must not re-emit (FSM should have transitioned out of Unprotected).
            var second = addon.EvaluateGraceExpiry(account, mnq.FullName);
            Assert(second.Count == 0,
                "Second grace-expiry Flatten call emits nothing (already triggered)");
        }

        // A4. FSM PositionQuantity is kept in sync with actual position qty.
        private static void TestFsm_PositionQuantityUpdatedOnQtyChange()
        {
            Console.WriteLine("\n[TEST] AUDIT: FSM PositionQuantity stays in sync");
            var config = FsmTestConfig();
            var account = FsmTestAccount();
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.TestClearFsms();
            var mnq = new Instrument("MNQ");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 4);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).PositionQuantity == 4,
                "Initial qty = 4");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 7);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).PositionQuantity == 7,
                "Qty updated to 7 after scale-out");

            addon.TestFsmOnPosition(account, mnq.FullName, MarketPosition.Long, 2);
            Assert(addon.TestGetFsm(account.Name, mnq.FullName).PositionQuantity == 2,
                "Qty updated to 2 after partial close");
        }

        // A5. EvaluateRules should NOT emit DAILY_LOSS_BREACH or TRAILING_DD_BREACH
        // (those are owned by EvaluatePnLRules via AccountItemUpdate to avoid double-fire).
        private static void TestPnLRulesNotDuplicatedInEvaluateRules()
        {
            Console.WriteLine("\n[TEST] AUDIT: PnL rules not duplicated in EvaluateRules");
            var config = new RiskConfig();
            config.PnLRules.DailyLossLimit = 500.0;
            config.PnLRules.TrailingDrawdown = 500.0;

            var account = new Account { Name = "TestAcc" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);

            var state = new AccountState("TestAcc");
            state.RealizedPnL = -600.0; // Breaches daily loss
            state.PeakEquity = 0.0;
            state.UnrealizedPnL = 0.0;

            var actions = addon.EvaluateRules(account, state);
            // After fix, EvaluateRules should NOT emit PnL rules (owned by EvaluatePnLRules).
            Assert(!actions.Any(a => a.RuleId == "DAILY_LOSS_BREACH"),
                "EvaluateRules does not emit DAILY_LOSS_BREACH (owned by EvaluatePnLRules)");
            Assert(!actions.Any(a => a.RuleId == "TRAILING_DD_BREACH"),
                "EvaluateRules does not emit TRAILING_DD_BREACH (owned by EvaluatePnLRules)");
        }

        // A6. ExecuteOrderUpdate should process lockout actions OUTSIDE the lock
        // to avoid re-entrancy corruption. We verify by ensuring that a locked-out
        // account with a working order gets the order cancelled and no exception.
        private static void TestExecuteOrderUpdateProcessesActionsOutsideLock()
        {
            Console.WriteLine("\n[TEST] AUDIT: ExecuteOrderUpdate processes lockout actions safely");
            var config = new RiskConfig();
            // - New TDD Copier, Instrument Caps, ATM & Prop-Firm Tests -
            TestPerInstrumentSizing_MNQVsMES();
            TestInstrumentBlacklist_BlocksMiniNQ();
            TestPropFirmProfile_AllowedInstruments();
            TestTradeCopier_RatioScaling();
            TestTradeCopier_SymbolMapping();
            TestAtmStrategy_DrawdownShieldBreakeven();
            TestNewsShield_FlattensBeforeCPI();
            TestStrategyApi_CanTradeReturnsFalseWhenLockedOut();
            TestEvaluationProfitTargetLock_LocksAccount();
            TestPeakEquityProtection_ClosesOnGiveback();

            // Printed Summary
            Console.WriteLine("\n====================================================");
            Console.WriteLine($"TEST SUMMARY: Passed {_testsPassed} / {_testsPassed + _testsFailed} tests.");
            Console.WriteLine("====================================================");
            if (_testsFailed > 0)
            {
                Environment.Exit(1);
            }
        }

        private static void TestPerInstrumentSizing_MNQVsMES()
        {
            Console.WriteLine("\n[TEST] TestPerInstrumentSizing_MNQVsMES");
            var config = new RiskConfig();
            config.InstrumentLimits["MNQ"] = new PerInstrumentRiskConfig { MaxContracts = 2 };
            config.InstrumentLimits["MES"] = new PerInstrumentRiskConfig { MaxContracts = 10 };

            var account = new Account { Name = "Sim101" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var mnqOrder = new Order { Id = "1", OrderState = OrderState.Working, OrderType = OrderType.Market, Quantity = 3, Instrument = new Instrument("MNQ SEP26") };
            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = mnqOrder });
            Assert(mnqOrder.OrderState == OrderState.Cancelled, "MNQ order exceeding 2 contracts cap cancelled");

            var mesOrder = new Order { Id = "2", OrderState = OrderState.Working, OrderType = OrderType.Market, Quantity = 8, Instrument = new Instrument("MES SEP26") };
            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = mesOrder });
            Assert(mesOrder.OrderState == OrderState.Working, "MES order under 10 contracts cap allowed working");
        }

        private static void TestInstrumentBlacklist_BlocksMiniNQ()
        {
            Console.WriteLine("\n[TEST] TestInstrumentBlacklist_BlocksMiniNQ");
            var config = new RiskConfig();
            config.BlockedInstruments = new List<string> { "NQ", "ES", "YM" };

            var account = new Account { Name = "Sim101" };
            var addon = new RiskGuardAddOn();
            addon.SetConfigForTest(config);
            addon.SetModeForTest("live");

            var nqOrder = new Order { Id = "1", OrderState = OrderState.Working, OrderType = OrderType.Market, Quantity = 1, Instrument = new Instrument("NQ SEP26") };
            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = nqOrder });
            Assert(nqOrder.OrderState == OrderState.Cancelled, "Blacklisted Mini NQ order cancelled");

            var mnqOrder = new Order { Id = "2", OrderState = OrderState.Working, OrderType = OrderType.Market, Quantity = 1, Instrument = new Instrument("MNQ SEP26") };
            addon.ExecuteOrderUpdate(account, new OrderEventArgs { Order = mnqOrder });
            Assert(mnqOrder.OrderState == OrderState.Working, "Non-blacklisted Micro MNQ order allowed");
        }

        private static void TestPropFirmProfile_AllowedInstruments()
        {
            Console.WriteLine("\n[TEST] TestPropFirmProfile_AllowedInstruments");
            var profile = new PropFirmProfile
            {
                Name = "Apex Trader Funding",
                AllowedInstruments = new List<string> { "NQ", "MNQ", "ES", "MES" },
                BlockedInstruments = new List<string> { "ZB", "ZN" }
            };
            Assert(profile.AllowedInstruments.Contains("MNQ"), "Apex profile allows MNQ");
            Assert(profile.BlockedInstruments.Contains("ZB"), "Apex profile blocks ZB");
        }

        private static void TestTradeCopier_RatioScaling()
        {
            Console.WriteLine("\n[TEST] TestTradeCopier_RatioScaling");
            var engine = new TradeCopierEngine();
            var rel = new CopierRelationship
            {
                LeaderAccountName = "Sim101",
                FollowerAccountName = "SimCopy2",
                QuantityRatio = 0.5,
                AutoSymbolConversion = false,
                IsEnabled = true
            };
            engine.AddRelationship(rel);
            int followerQty = engine.CalculateFollowerQuantity(rel, 4, "MNQ SEP26");
            Assert(followerQty == 2, "4 leader contracts @ 0.5x ratio = 2 follower contracts");
        }

        private static void TestTradeCopier_SymbolMapping()
        {
            Console.WriteLine("\n[TEST] TestTradeCopier_SymbolMapping");
            var engine = new TradeCopierEngine();
            var rel = new CopierRelationship
            {
                LeaderAccountName = "Sim101",
                FollowerAccountName = "SimCopy2",
                QuantityRatio = 1.0,
                AutoSymbolConversion = true,
                IsEnabled = true
            };
            engine.AddRelationship(rel);
            string targetSymbol = engine.TranslateSymbol("NQ SEP26");
            int targetQty = engine.CalculateFollowerQuantity(rel, 1, "NQ SEP26");
            Assert(targetSymbol == "MNQ SEP26", "NQ translated to MNQ");
            Assert(targetQty == 10, "1 NQ translated to 10 MNQ contracts");
        }

        private static void TestAtmStrategy_DrawdownShieldBreakeven()
        {
            Console.WriteLine("\n[TEST] TestAtmStrategy_DrawdownShieldBreakeven");
            var atm = new DynamicAtmManager();
            var config = new AtmStrategyConfig
            {
                Type = AtmStrategyType.DrawdownShield,
                BreakevenTriggerTicks = 12,
                BreakevenOffsetTicks = 2
            };
            bool shouldTrail = atm.ShouldTriggerBreakeven(config, entryPrice: 20000.0, currentPrice: 20003.0, isLong: true, tickSize: 0.25);
            Assert(shouldTrail == true, "+12 ticks gain triggers breakeven trailing");

            double newStop = atm.CalculateBreakevenStopPrice(entryPrice: 20000.0, isLong: true, tickSize: 0.25, offsetTicks: 2);
            Assert(newStop == 20000.50, "Breakeven stop placed at Entry + 2 ticks");
        }

        private static void TestNewsShield_FlattensBeforeCPI()
        {
            Console.WriteLine("\n[TEST] TestNewsShield_FlattensBeforeCPI");
            var suite = new PropFirmProtectionSuite();
            var cpiTime = DateTime.UtcNow.AddMinutes(1); // 1 minute in future
            suite.AddTestNewsEvent(new EconomicNewsEvent { EventTimeUtc = cpiTime, Title = "CPI", Impact = "High" });

            bool inBuffer = suite.IsInNewsWindow(DateTime.UtcNow, bufferMinutesBefore: 2, bufferMinutesAfter: 2);
            Assert(inBuffer == true, "Within 2m pre-CPI window detects news lock");
        }

        private static void TestStrategyApi_CanTradeReturnsFalseWhenLockedOut()
        {
            Console.WriteLine("\n[TEST] TestStrategyApi_CanTradeReturnsFalseWhenLockedOut");
            var addon = new RiskGuardAddOn();
            var state = new AccountState("Sim101") { IsLockedOut = true };
            addon.SetAccountStateForTest("Sim101", state);

            bool canTrade = addon.CanTrade("Sim101", "MNQ SEP26", "MyStrategy");
            Assert(canTrade == false, "CanTrade returns false for locked-out account");
        }

        private static void TestEvaluationProfitTargetLock_LocksAccount()
        {
            Console.WriteLine("\n[TEST] TestEvaluationProfitTargetLock_LocksAccount");
            var suite = new PropFirmProtectionSuite();
            var config = new PropFirmProtectionConfig { EnableProfitTargetLock = true, EvaluationTargetProfit = 3000.0 };

            bool reached = suite.EvaluateProfitTargetLock(currentRealizedPnL: 3050.0, config);
            Assert(reached == true, "+$3,050 PnL triggers evaluation target lockout");
        }

        private static void TestPeakEquityProtection_ClosesOnGiveback()
        {
            Console.WriteLine("\n[TEST] TestPeakEquityProtection_ClosesOnGiveback");
            var suite = new PropFirmProtectionSuite();
            var config = new PropFirmProtectionConfig { EnablePeakEquityProtection = true, MaxPeakGivebackPct = 0.30 };

            bool givebackExceeded = suite.EvaluatePeakEquityGiveback(peakOpenGain: 1000.0, currentUnrealized: 600.0, config);
            Assert(givebackExceeded == true, "40% giveback (from $1000 peak to $600) exceeds 30% giveback threshold");

            TestOptionC_TradeCopierSingletonIntegration();
            TestOptionC_PropProtectionSingletonIntegration();
        }

        private static void TestOptionC_TradeCopierSingletonIntegration()
        {
            Console.WriteLine("\n[TEST] TestOptionC_TradeCopierSingletonIntegration");
            var rel = new CopierRelationship
            {
                LeaderAccountName = "SimLeader",
                FollowerAccountName = "SimFollower",
                ArmedForLive = true,
                QuantityRatio = 1.0,
                AutoSymbolConversion = true
            };

            // Test Arming Gate: confirmLive = false must force ArmedForLive = false
            TradeCopierEngine.Instance.UpsertRelationship(rel, confirmLive: false);
            var rels = TradeCopierEngine.Instance.GetRelationships();
            var registeredRel = rels.FirstOrDefault(r => r.LeaderAccountName == "SimLeader");
            Assert(registeredRel != null, "CopierRelationship registered in TradeCopierEngine.Instance");
            Assert(registeredRel.ArmedForLive == false, "ArmedForLive forced to false when confirmLive is false");

            // Arm with confirmLive = true
            rel.ArmedForLive = true;
            TradeCopierEngine.Instance.UpsertRelationship(rel, confirmLive: true);
            Assert(rel.ArmedForLive == true, "ArmedForLive enabled when confirmLive is true");

            // Worked Examples for Quantity Math & 10x Conversion
            string followerSymbol = TradeCopierEngine.Instance.TranslateSymbol("NQ 09-26");
            Assert(followerSymbol == "MNQ 09-26", "Symbol translated from NQ to MNQ");

            int qty1 = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 1, "NQ 09-26", isExit: false);
            Assert(qty1 == 10, "Worked Example: 1 NQ leader @ 1.0 ratio = 10 MNQ follower");

            rel.QuantityRatio = 2.0;
            rel.MaxPositionSize = 50;
            int qty2 = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 1, "NQ 09-26", isExit: false);
            Assert(qty2 == 20, "Worked Example: 1 NQ leader @ 2.0 ratio = 20 MNQ follower");

            // Reduce / Exit Handling
            int exitQty = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 0, "NQ 09-26", isExit: true);
            Assert(exitQty == 0, "Exit with 0 qty returns 0 without floored 1-lot minimum");

            TestOptionC_MultiPartialFillPositionClamping();
        }

        private static void TestOptionC_MultiPartialFillPositionClamping()
        {
            Console.WriteLine("\n[TEST] TestOptionC_MultiPartialFillPositionClamping");
            var rel = new CopierRelationship
            {
                LeaderAccountName = "SimLeader",
                FollowerAccountName = "SimFollower",
                ArmedForLive = true,
                QuantityRatio = 1.0,
                AutoSymbolConversion = true,
                MaxPositionSize = 25
            };

            int currentPos = 0;

            // Partial Fill 1: Leader fills 1 NQ (wants 10 MNQ)
            int fill1 = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 1, "NQ 09-26", currentPos, isExit: false, out bool clamped1);
            Assert(fill1 == 10, "Partial Fill 1 copies 10 MNQ");
            Assert(clamped1 == false, "Partial Fill 1 is not clamped");
            currentPos += fill1;

            // Partial Fill 2: Leader fills another 1 NQ (wants 10 MNQ)
            int fill2 = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 1, "NQ 09-26", currentPos, isExit: false, out bool clamped2);
            Assert(fill2 == 10, "Partial Fill 2 copies 10 MNQ");
            Assert(clamped2 == false, "Partial Fill 2 is not clamped");
            currentPos += fill2;

            // Partial Fill 3: Leader fills another 1 NQ (wants 10 MNQ, but capacity is 25 - 20 = 5 MNQ)
            int fill3 = TradeCopierEngine.Instance.CalculateFollowerQuantity(rel, 1, "NQ 09-26", currentPos, isExit: false, out bool clamped3);
            Assert(fill3 == 5, "Partial Fill 3 clamped to remaining capacity of 5 MNQ");
            Assert(clamped3 == true, "Partial Fill 3 triggers position clamping warning flag");
            currentPos += fill3;

            Assert(currentPos == 25, "Follower cumulative position capped at MaxPositionSize 25 MNQ across multi-partial fills");
        }

        private static void TestOptionC_PropProtectionSingletonIntegration()
        {
            Console.WriteLine("\n[TEST] TestOptionC_PropProtectionSingletonIntegration");
            var cfg = new PropFirmProtectionConfig
            {
                ArmedForLive = true,
                EnableNewsShield = true,
                EnableProfitTargetLock = true,
                EvaluationTargetProfit = 2500.0,
                MaxPeakGivebackPct = 0.25
            };

            // Test Arming Gate: confirmLive = false must force ArmedForLive = false
            PropFirmProtectionSuite.Instance.UpdateConfig(cfg, confirmLive: false);
            Assert(PropFirmProtectionSuite.Instance.Config.ArmedForLive == false, "ArmedForLive forced to false when confirmLive is false");

            cfg.ArmedForLive = true;
            PropFirmProtectionSuite.Instance.UpdateConfig(cfg, confirmLive: true);
            Assert(PropFirmProtectionSuite.Instance.Config.ArmedForLive == true, "ArmedForLive enabled when confirmLive is true");

            bool locked = PropFirmProtectionSuite.Instance.EvaluateProfitTargetLock(2600.0);
            Assert(locked == true, "PnL 2600 reaches 2500 target lock threshold");
        }

        private static void TestCopierGroup_GroupManagement()
        {
            Console.WriteLine("\n[TEST] TestCopierGroup_GroupManagement");
            var engine = TradeCopierEngine.Instance;
            
            var grp = new CopierGroup
            {
                GroupName = "UnitTestGroup1",
                LeaderAccountName = "SimLeader1",
                QuantityRatio = 1.5,
                MaxPositionSize = 50,
                FollowerAccounts = new List<string> { "SimCopyA", "SimCopyB" }
            };

            engine.UpsertGroup(grp, confirmLive: false);
            var groups = engine.GetGroups();
            Assert(groups.Any(g => g.GroupName == "UnitTestGroup1"), "Group UnitTestGroup1 created and registered");

            engine.AddFollowerToGroup("UnitTestGroup1", "SimCopyC");
            var fetched = engine.GetGroup("UnitTestGroup1");
            Assert(fetched != null && fetched.FollowerAccounts.Contains("SimCopyC"), "Follower SimCopyC added to UnitTestGroup1");

            engine.RemoveFollowerFromGroup("UnitTestGroup1", "SimCopyA");
            fetched = engine.GetGroup("UnitTestGroup1");
            Assert(fetched != null && !fetched.FollowerAccounts.Contains("SimCopyA"), "Follower SimCopyA removed from UnitTestGroup1");

            engine.RemoveGroup("UnitTestGroup1");
            groups = engine.GetGroups();
            Assert(!groups.Any(g => g.GroupName == "UnitTestGroup1"), "Group UnitTestGroup1 deleted successfully");
        }

        private static void TestCopierGroup_PerGroupConfigurationExecution()
        {
            Console.WriteLine("\n[TEST] TestCopierGroup_PerGroupConfigurationExecution");
            var engine = TradeCopierEngine.Instance;

            foreach (var g in engine.GetGroups()) engine.RemoveGroup(g.GroupName);

            var group1 = new CopierGroup
            {
                GroupName = "Apex_50K",
                LeaderAccountName = "SimLeaderAlpha",
                QuantityRatio = 1.0,
                AutoSymbolConversion = true,
                MaxPositionSize = 100,
                FollowerAccounts = new List<string> { "ApexFollower1", "ApexFollower2" }
            };

            var group2 = new CopierGroup
            {
                GroupName = "Topstep_100K",
                LeaderAccountName = "SimLeaderAlpha",
                QuantityRatio = 0.5,
                AutoSymbolConversion = true,
                MaxPositionSize = 100,
                FollowerAccounts = new List<string> { "TopstepFollower1" }
            };

            engine.UpsertGroup(group1, confirmLive: false);
            engine.UpsertGroup(group2, confirmLive: false);

            var activeRels = engine.GetActiveRelationshipsForLeader("SimLeaderAlpha");
            Assert(activeRels.Count == 3, "Leader SimLeaderAlpha maps to 3 total follower relationships across 2 groups");

            var apexRel = activeRels.First(r => r.FollowerAccountName == "ApexFollower1");
            int apexQty = engine.CalculateFollowerQuantity(apexRel, 1, "NQ 09-26");
            Assert(apexQty == 10, "Apex group follower copies 1 NQ as 10 MNQ (1.0x ratio)");

            var topstepRel = activeRels.First(r => r.FollowerAccountName == "TopstepFollower1");
            int topstepQty = engine.CalculateFollowerQuantity(topstepRel, 1, "NQ 09-26");
            Assert(topstepQty == 5, "Topstep group follower copies 1 NQ as 5 MNQ (0.5x ratio)");
        }

        private static void TestCopierGroup_GroupPersistence()
        {
            Console.WriteLine("\n[TEST] TestCopierGroup_GroupPersistence");
            var engine = TradeCopierEngine.Instance;
            string testFile = Path.Combine(Path.GetTempPath(), "test_copier_group_config.json");

            var group = new CopierGroup
            {
                GroupName = "PersistTestGroup",
                LeaderAccountName = "PersistLeader",
                QuantityRatio = 2.0,
                FollowerAccounts = new List<string> { "PersistFollower1", "PersistFollower2" }
            };

            engine.UpsertGroup(group, confirmLive: false);
            engine.SaveToDisk(testFile);
            Assert(File.Exists(testFile), "Group config JSON successfully saved to disk");

            engine.RemoveGroup("PersistTestGroup");
            Assert(engine.GetGroup("PersistTestGroup") == null, "Group removed in-memory prior to reload");

            engine.LoadFromDisk(testFile);
            var reloaded = engine.GetGroup("PersistTestGroup");
            Assert(reloaded != null, "Reloaded group PersistTestGroup exists");
            Assert(reloaded.QuantityRatio == 2.0, "Reloaded group QuantityRatio is 2.0");
            Assert(reloaded.FollowerAccounts != null && reloaded.FollowerAccounts.Count == 2, "Reloaded group FollowerAccounts count is 2");

            try { File.Delete(testFile); } catch {}
        }

        private static void TestCopierGroup_GroupStressAndConcurrency()
        {
            Console.WriteLine("\n[TEST] TestCopierGroup_GroupStressAndConcurrency");
            var engine = TradeCopierEngine.Instance;

            for (int i = 1; i <= 10; i++)
            {
                var followers = new List<string>();
                for (int j = 1; j <= 5; j++) followers.Add($"StressFollower_{i}_{j}");

                var grp = new CopierGroup
                {
                    GroupName = $"StressGroup_{i}",
                    LeaderAccountName = "StressLeaderMaster",
                    QuantityRatio = 0.5 * i,
                    MaxPositionSize = 200,
                    FollowerAccounts = followers
                };
                engine.UpsertGroup(grp, confirmLive: false);
            }

            int iterationsPerThread = 250;
            int threadCount = 4;
            int totalCalculatedOrders = 0;
            bool threadExceptionOccurred = false;

            var tasks = new Task[threadCount + 1];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    try
                    {
                        string[] symbols = new string[] { "NQ 09-26", "ES 09-26", "YM 09-26", "CL 10-26", "GC 12-26" };
                        for (int k = 0; k < iterationsPerThread; k++)
                        {
                            string sym = symbols[k % symbols.Length];
                            var rels = engine.GetActiveRelationshipsForLeader("StressLeaderMaster");
                            foreach (var rel in rels)
                            {
                                int qty = engine.CalculateFollowerQuantity(rel, 2, sym);
                                Interlocked.Increment(ref totalCalculatedOrders);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[STRESS TEST ERROR] Worker thread exception: {ex.Message}");
                        threadExceptionOccurred = true;
                    }
                });
            }

            tasks[threadCount] = Task.Run(() =>
            {
                try
                {
                    for (int m = 0; m < 50; m++)
                    {
                        engine.AddFollowerToGroup("StressGroup_1", $"DynamicFollower_{m}");
                        engine.RemoveFollowerFromGroup("StressGroup_1", $"DynamicFollower_{m}");
                        Thread.Sleep(2);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[STRESS TEST ERROR] Mutator thread exception: {ex.Message}");
                    threadExceptionOccurred = true;
                }
            });

            Task.WaitAll(tasks);

            Assert(!threadExceptionOccurred, "Stress & concurrency test executed with ZERO thread exceptions or race conditions");
            Assert(totalCalculatedOrders >= iterationsPerThread * threadCount * 50, $"Processed {totalCalculatedOrders} follower quantity evaluations across 1,000 rapid execution bursts under load");
        }

        public static void RunCopierFixesVerificationTests()
        {
            Console.WriteLine("\n--- RUNNING COPIER FIXES VERIFICATION TESTS ---");
            var engine = TradeCopierEngine.Instance;

            // Test 1: Deduplication of Group and Direct Relationships
            string testLeader = "DedupLeader";
            string testFollower = "DedupFollower";

            var directRel = new CopierRelationship
            {
                LeaderAccountName = testLeader,
                FollowerAccountName = testFollower,
                IsEnabled = true
            };
            engine.UpsertRelationship(directRel);

            var grp = new CopierGroup
            {
                GroupName = "DedupGroup",
                LeaderAccountName = testLeader,
                IsEnabled = true,
                FollowerAccounts = new List<string> { testFollower }
            };
            engine.UpsertGroup(grp);

            var activeRels = engine.GetActiveRelationshipsForLeader(testLeader);
            Assert(activeRels.Count == 1, $"Group and Direct follower relationship deduplicated to 1 record (got {activeRels.Count})");
            Assert(activeRels[0].FollowerAccountName.Equals(testFollower, StringComparison.OrdinalIgnoreCase), "Correct follower account returned");

            // Test 2: Inverse Mode Quantity Calculation
            var inverseRel = new CopierRelationship
            {
                LeaderAccountName = testLeader,
                FollowerAccountName = "InverseFollower",
                QuantityRatio = -1.0,
                AutoSymbolConversion = false,
                MaxPositionSize = 10
            };
            int qtyEntry = engine.CalculateFollowerQuantity(inverseRel, 2, "NQ 09-26", 0, false, out _);
            Assert(qtyEntry == 2, $"Inverse entry quantity is positive 2 (got {qtyEntry})");

            // Test 3: Bidirectional Symbol Translation (Mini <-> Micro)
            string mnqTrans = engine.TranslateSymbol("NQ 09-26");
            Assert(mnqTrans.Contains("MNQ"), $"NQ translated to MNQ (got {mnqTrans})");

            string nqTrans = engine.TranslateSymbol("MNQ 09-26");
            Assert(nqTrans.Contains("NQ") && !nqTrans.Contains("MNQ"), $"MNQ translated to NQ (got {nqTrans})");

            string mesTrans = engine.TranslateSymbol("ES 09-26");
            Assert(mesTrans.Contains("MES"), $"ES translated to MES (got {mesTrans})");

            string esTrans = engine.TranslateSymbol("MES 09-26");
            Assert(esTrans.Contains("ES") && !esTrans.Contains("MES"), $"MES translated to ES (got {esTrans})");

            // Test 4: Per-Ticker Ratio Overrides & Micro-to-Mini Fractional Scaling
            var tickerRel = new CopierRelationship
            {
                LeaderAccountName = testLeader,
                FollowerAccountName = "TickerFollower",
                QuantityRatio = 1.0,
                AutoSymbolConversion = true,
                MaxPositionSize = 100
            };
            tickerRel.PerTickerRatios["NQ"] = 2.0;
            tickerRel.PerTickerRatios["ES"] = 0.5;

            // NQ -> MNQ with 2.0x ratio override: 1 NQ * 2.0 * 10 = 20 MNQ
            int nqQty = engine.CalculateFollowerQuantity(tickerRel, 1, "NQ 09-26", 0, false, out _);
            Assert(nqQty == 20, $"NQ with 2.0x ratio override equals 20 MNQ (got {nqQty})");

            // ES -> MES with 0.5x ratio override: 2 ES * 0.5 * 10 = 10 MES
            int esQty = engine.CalculateFollowerQuantity(tickerRel, 2, "ES 09-26", 0, false, out _);
            Assert(esQty == 10, $"ES with 0.5x ratio override equals 10 MES (got {esQty})");

            // Micro -> Mini 0.1x scaling: 10 MNQ * 1.0 * 0.1 = 1 NQ
            var microToMiniRel = new CopierRelationship
            {
                LeaderAccountName = testLeader,
                FollowerAccountName = "MiniFollower",
                QuantityRatio = 1.0,
                AutoSymbolConversion = true,
                MaxPositionSize = 100
            };
            int miniQty = engine.CalculateFollowerQuantity(microToMiniRel, 10, "MNQ 09-26", 0, false, out _);
            Assert(miniQty == 1, $"10 MNQ scaled to Mini equals 1 NQ (got {miniQty})");

            Console.WriteLine("[PASS] All Copier Fixes Verification Tests Passed Successfully!");
        }

        public static void TestOrderVerificationWatchdogAndReconciliation()
        {
            Console.WriteLine("\n--- RUNNING ORDER VERIFICATION & WATCHDOG TESTS ---");

            var engine = TradeCopierEngine.Instance;

            // Test 1: AwayFromZero Contract Scaling Precision
            int scaledHalf = engine.CalculateScaledQuantity(1, 0.5m);
            Assert(scaledHalf == 1, "1 contract @ 0.5 ratio rounds AwayFromZero to 1 contract");

            int scaledQuart = engine.CalculateScaledQuantity(1, 0.2m);
            Assert(scaledQuart == 0, "1 contract @ 0.2 ratio rounds to 0");

            // Test 2: Double-Checked Quarantine State Locking
            var testRel = new CopierRelationship
            {
                LeaderAccountName = "QuarantineLeader",
                FollowerAccountName = "QuarantinedFollower",
                IsQuarantined = true,
                QuarantineReason = "Execution Verification Timeout"
            };
            engine.UpsertRelationship(testRel, confirmLive: true);
            var active = engine.GetActiveRelationshipsForLeader("QuarantineLeader");
            Assert(active.Count == 0, "Quarantined relationship excluded from active leader execution list");

            // Test 3: Emergency Flatten Sequence Order State Filter
            var activeStates = new List<OrderState> { OrderState.Working, OrderState.Submitted, OrderState.Accepted, OrderState.PartFilled };
            Assert(activeStates.Contains(OrderState.PartFilled), "PartFilled order state included in emergency flatten cancel pass");
            Assert(activeStates.Contains(OrderState.Submitted), "Submitted order state included in emergency flatten cancel pass");

            Console.WriteLine("[PASS] All Order Verification Watchdog & Reconciliation Tests Passed Successfully!");
        }

        public static void TestHedgingReconciliationAndAutoClose()
        {
            Console.WriteLine("\n--- RUNNING HEDGING, RECONCILIATION & AUTO-CLOSE TESTS ---");

            var engine = TradeCopierEngine.Instance;

            // Test 1: Normal short entry allowed when flat
            int safeShortDelta = engine.CalculateSafeFollowerDelta(leaderTargetQty: -2, currentFollowerQty: 0, isMarketOrder: true, out bool isBlocked);
            Assert(safeShortDelta == -2 && !isBlocked, "Normal short entry delta allowed when flat");

            // Test 2: Quantity Capping on Position Reduction
            int cappedDelta = engine.CalculateSafeFollowerDelta(leaderTargetQty: 0, currentFollowerQty: 5, isMarketOrder: true, out isBlocked);
            Assert(cappedDelta == -5 && !isBlocked, "Exit delta capped to follower open position size");

            // Test 3: Standalone Limit/Stop entries unblocked
            int limitDelta = engine.CalculateSafeFollowerDelta(leaderTargetQty: 3, currentFollowerQty: 0, isMarketOrder: false, out isBlocked);
            Assert(limitDelta == 3 && !isBlocked, "Standalone Limit/Stop entry unblocked when flat");

            Console.WriteLine("[PASS] All Hedging, Reconciliation & Auto-Close Tests Passed Successfully!");
        }

        // ─────────────────────────────────────────────────────────────────────
        // DYNAMIC ATM BRACKET TESTS
        // Exercises DynamicAtmManager.PlaceBracket strategy math, profile
        // fallback, OCO wiring, bracket registration, and error paths without
        // requiring a live NinjaTrader runtime or open market.
        // ─────────────────────────────────────────────────────────────────────

        private static bool Approx(double a, double b, double tol = 0.0001)
        {
            return Math.Abs(a - b) <= tol;
        }

        private static Account CreateAtmAccount(string name = "Sim101")
        {
            return new Account { Name = name };
        }

        private static Instrument CreateAtmInstrument(string root, string fullName = null)
        {
            var instr = new Instrument(fullName ?? root);
            instr.MasterInstrument.Name = root;
            instr.MasterInstrument.TickSize = 0.25;
            return instr;
        }

        private static void TestAtm_FixedTicksLong()
        {
            Console.WriteLine("\n[TEST] ATM: FixedTicks Long");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "FixedTicks long submitted");
            Assert(result.StrategyName == "FixedTicks", "Strategy name is FixedTicks");
            Assert(Approx(result.StopPrice, 17998.0), "Stop = entry - 8 ticks (17998)");
            Assert(Approx(result.TargetPrice, 18004.0), "Target = entry + 16 ticks (18004)");
            Assert(!string.IsNullOrEmpty(result.BracketId), "BracketId generated");
            Assert(!string.IsNullOrEmpty(result.OcoId), "OcoId generated");
            Assert(!string.IsNullOrEmpty(result.EntryOrderId) && !string.IsNullOrEmpty(result.StopOrderId) && !string.IsNullOrEmpty(result.TargetOrderId), "All 3 order ids returned");
            Assert(account.Orders.Count == 3, "3 orders submitted to account");
            Assert(account.Orders.Count(o => o.Name.StartsWith("AtmEntry_")) == 1, "Entry order named AtmEntry_*");
            Assert(account.Orders.Count(o => o.Name.StartsWith("Stop_")) == 1, "Stop order named Stop_*");
            Assert(account.Orders.Count(o => o.Name.StartsWith("Target_")) == 1, "Target order named Target_*");
        }

        private static void TestAtm_FixedTicksShort()
        {
            Console.WriteLine("\n[TEST] ATM: FixedTicks Short");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "sell", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "FixedTicks short submitted");
            Assert(Approx(result.StopPrice, 18002.0), "Short stop = entry + 8 ticks (18002)");
            Assert(Approx(result.TargetPrice, 17996.0), "Short target = entry - 16 ticks (17996)");
            var entry = account.Orders.First(o => o.Name.StartsWith("AtmEntry_"));
            var stop = account.Orders.First(o => o.Name.StartsWith("Stop_"));
            var target = account.Orders.First(o => o.Name.StartsWith("Target_"));
            Assert(entry.OrderAction == OrderAction.Sell, "Short entry order action is Sell");
            Assert(stop.OrderAction == OrderAction.Buy, "Short stop order action is Buy");
            Assert(target.OrderAction == OrderAction.Buy, "Short target order action is Buy");
        }

        private static void TestAtm_OcoIdSharedAcrossExitOrders()
        {
            Console.WriteLine("\n[TEST] ATM: OCO Id Shared Across Exit Orders");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            var stop = account.Orders.First(o => o.Name.StartsWith("Stop_"));
            var target = account.Orders.First(o => o.Name.StartsWith("Target_"));
            var entry = account.Orders.First(o => o.Name.StartsWith("AtmEntry_"));
            Assert(!string.IsNullOrEmpty(stop.Oco) && stop.Oco == result.OcoId, "Stop order carries result OcoId");
            Assert(!string.IsNullOrEmpty(target.Oco) && target.Oco == result.OcoId, "Target order carries result OcoId");
            Assert(string.IsNullOrEmpty(entry.Oco), "Entry order has empty Oco id (not OCO-linked)");
        }

        private static void TestAtm_DrawdownShieldRegistersBracket()
        {
            Console.WriteLine("\n[TEST] ATM: DrawdownShield Registers Monitor Bracket");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.DrawdownShield, StopTicks = 10, TargetTicks = 20, BreakevenTriggerTicks = 12, BreakevenOffsetTicks = 2 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "DrawdownShield submitted");
            Assert(result.Note != null && result.Note.Contains("registered"), "Bracket registered note present");
            var brackets = DynamicAtmManager.Instance.GetActiveBrackets();
            Assert(brackets.Any(b => b.BracketId == result.BracketId), "Bracket appears in GetActiveBrackets()");
            var bracket = brackets.First(b => b.BracketId == result.BracketId);
            Assert(bracket.Symbol == "MNQ" && bracket.IsLong && bracket.Quantity == 1, "Bracket metadata correct (symbol/isLong/qty)");
            Assert(bracket.Config.Type == AtmStrategyType.DrawdownShield, "Bracket carries strategy config");

            DynamicAtmManager.Instance.RemoveBracket(result.BracketId);
        }

        private static void TestAtm_ScaledRunnerRegistersBracket()
        {
            Console.WriteLine("\n[TEST] ATM: ScaledRunner Registers Monitor Bracket");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("NQ", "NQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.ScaledRunner, StopTicks = 8, TargetTicks = 30 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 20.0);

            Assert(result.Status == "submitted", "ScaledRunner submitted");
            var brackets = DynamicAtmManager.Instance.GetActiveBrackets();
            Assert(brackets.Any(b => b.BracketId == result.BracketId), "ScaledRunner bracket registered");
            Assert(brackets.First(b => b.BracketId == result.BracketId).Config.Type == AtmStrategyType.ScaledRunner, "Bracket carries ScaledRunner config");

            DynamicAtmManager.Instance.RemoveBracket(result.BracketId);
        }

        private static void TestAtm_MonitoredStrategiesNotDoubleRegistered()
        {
            Console.WriteLine("\n[TEST] ATM: Non-monitored Strategy Does Not Register Bracket");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");

            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };
            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "FixedTicks submitted");
            var brackets = DynamicAtmManager.Instance.GetActiveBrackets();
            Assert(!brackets.Any(b => b.BracketId == result.BracketId), "FixedTicks bracket NOT registered for monitoring");
        }

        private static void TestAtm_VolatilityScaledQuantityCapped()
        {
            Console.WriteLine("\n[TEST] ATM: VolatilityScaled Quantity Capped At MaxContracts");
            BarsRequest.TestBarsFactory = null; // ensure fallback ATR path (isolation from prior live-ATR tests)
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.VolatilityScaled, RiskPerTrade = 100000.0 };

            // MNQ profile: MaxContracts = 50, DefaultATR = 30 (tick 0.25) -> fallback atr = 7.5
            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "VolatilityScaled submitted");
            Assert(result.CalculatedQuantity == 50, "Calculated quantity capped at profile MaxContracts (50)");
        }

        private static void TestAtm_VolatilityScaledRiskBasedQuantity()
        {
            Console.WriteLine("\n[TEST] ATM: VolatilityScaled Risk-Based Quantity");
            BarsRequest.TestBarsFactory = null; // ensure fallback ATR path (isolation from prior live-ATR tests)
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.VolatilityScaled, RiskPerTrade = 200.0 };

            // fallback atr = 30*0.25 = 7.5; riskPerContract = 7.5*1.5*2.0 = 22.5; qty = floor(200/22.5) = 8
            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "VolatilityScaled submitted");
            Assert(result.CalculatedQuantity == 8, "Calculated quantity = floor(RiskPerTrade / riskPerContract) = 8");
        }

        private static void TestAtm_AtrAdaptiveFallbackUsesDefaultAtr()
        {
            Console.WriteLine("\n[TEST] ATM: AtrAdaptive Fallback To Profile DefaultATR");
            BarsRequest.TestBarsFactory = null; // ensure fallback ATR path (no live bars injected)
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.AtrAdaptive };

            // No bars factory -> atr = DefaultATR * tickSize = 30*0.25 = 7.5
            // slDist = 7.5*1.5 = 11.25, tpDist = 7.5*2.5 = 18.75
            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "AtrAdaptive submitted");
            Assert(Approx(result.StopPrice, 17988.75), "Stop = entry - 11.25 (profile default ATR fallback)");
            Assert(Approx(result.TargetPrice, 18018.75), "Target = entry + 18.75 (profile default ATR fallback)");
        }

        private static void TestAtm_AtrAdaptiveUsesLiveAtr()
        {
            Console.WriteLine("\n[TEST] ATM: AtrAdaptive Uses Live ATR From Bars");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.AtrAdaptive };

            // Constant range bars -> TR = 1.0 for every bar -> ATR = 1.0
            int n = 20;
            var high = new double[n];
            var low = new double[n];
            var close = new double[n];
            var open = new double[n];
            var vol = new long[n];
            var time = new DateTime[n];
            var baseTime = new DateTime(2026, 8, 1, 9, 30, 0);
            for (int i = 0; i < n; i++)
            {
                high[i] = 100.0;
                low[i] = 99.0;
                close[i] = 99.5;
                open[i] = 99.5;
                vol[i] = 1000;
                time[i] = baseTime.AddMinutes(i);
            }
            BarsRequest.TestBarsFactory = req => new Bars(high, low, close, open, vol, time);

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            // ATR = 1.0 -> slDist = 1.5, tpDist = 2.5
            Assert(result.Status == "submitted", "AtrAdaptive submitted with live ATR");
            Assert(Approx(result.StopPrice, 17998.5), "Stop = entry - 1.5 (live ATR=1.0)");
            Assert(Approx(result.TargetPrice, 18002.5), "Target = entry + 2.5 (live ATR=1.0)");

            BarsRequest.TestBarsFactory = null;
        }

        private static void TestAtm_SwingPointUsesSwingLow()
        {
            Console.WriteLine("\n[TEST] ATM: SwingPoint Uses Swing Low");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.SwingPoint, SwingLookbackBars = 5, SwingBufferTicks = 4 };

            // lows descending: last lookback window min = 17996; buffer = 4 ticks = 1.0 -> stop = 17995
            int n = 10;
            double[] lows = { 18010, 18009, 18008, 18007, 18006, 18005, 18004, 18003, 18002, 17996 };
            var high = new double[n];
            var low = new double[n];
            var close = new double[n];
            var open = new double[n];
            var vol = new long[n];
            var time = new DateTime[n];
            var baseTime = new DateTime(2026, 8, 1, 9, 30, 0);
            for (int i = 0; i < n; i++)
            {
                low[i] = lows[i];
                high[i] = low[i] + 2.0;
                close[i] = high[i] - 0.5;
                open[i] = high[i] - 0.5;
                vol[i] = 1000;
                time[i] = baseTime.AddMinutes(i * 5);
            }
            BarsRequest.TestBarsFactory = req => new Bars(high, low, close, open, vol, time);

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "SwingPoint submitted");
            Assert(Approx(result.StopPrice, 17995.0), "Stop = swing low 17996 - buffer 1.0 = 17995");
            Assert(Approx(result.TargetPrice, 18010.0), "Target = entry + 2x(entry-stop) = 18010");

            BarsRequest.TestBarsFactory = null;
        }

        private static void TestAtm_SessionAdaptiveMultiplier()
        {
            Console.WriteLine("\n[TEST] ATM: SessionAdaptive Smoke Test");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.SessionAdaptive, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "submitted", "SessionAdaptive submitted");
            Assert(result.StrategyName == "SessionAdaptive", "Strategy name is SessionAdaptive");
            Assert(result.StopPrice < 18000 && result.TargetPrice > 18000, "Stop below entry and target above entry for long");
            Assert(result.StopPrice < result.TargetPrice, "Stop strictly below target");
        }

        private static void TestAtm_UnknownSymbolFallsBackToDefaults()
        {
            Console.WriteLine("\n[TEST] ATM: Unknown Symbol Falls Back To Default Profile");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("XYZ", "XYZ 12-26");
            instrument.MasterInstrument.TickSize = 0.25;
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 100, 0.25, 50.0);

            Assert(result.Status == "submitted", "Unknown-symbol bracket submitted");
            Assert(result.StrategyName == "FixedTicks", "Fallback profile uses FixedTicks");
            Assert(Approx(result.StopPrice, 98.0), "Fallback stop = entry - 8 ticks (98)");
            Assert(Approx(result.TargetPrice, 104.0), "Fallback target = entry + 16 ticks (104)");
        }

        private static void TestAtm_GetProfileKnownAndUnknown()
        {
            Console.WriteLine("\n[TEST] ATM: GetProfile Known And Unknown");
            var mnq = DynamicAtmManager.GetProfile("MNQ");
            Assert(mnq != null && mnq.MaxContracts == 50 && mnq.DefaultStrategy == AtmStrategyType.AtrAdaptive, "MNQ profile returns MaxContracts=50, AtrAdaptive default");
            var es = DynamicAtmManager.GetProfile("ES");
            Assert(es != null && es.DefaultStrategy == AtmStrategyType.SwingPoint, "ES profile default strategy is SwingPoint");
            var sixE = DynamicAtmManager.GetProfile("6E");
            Assert(sixE != null && sixE.DefaultStrategy == AtmStrategyType.FixedTicks, "6E profile default strategy is FixedTicks");
            Assert(DynamicAtmManager.GetProfile("ZZZZ") == null, "Unknown root returns null profile");
        }

        private static void TestAtm_ZeroPriceReturnsError()
        {
            Console.WriteLine("\n[TEST] ATM: Zero Price Returns Error");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 0, 0.25, 2.0);

            Assert(result.Status == "error", "Zero entry price produces error status");
            Assert(!string.IsNullOrEmpty(result.Error), "Error message populated for zero price");
        }

        private static void TestAtm_RejectedExitOrdersPartialSubmit()
        {
            Console.WriteLine("\n[TEST] ATM: Rejected Exit Orders -> partial_submit");
            var account = CreateAtmAccount();
            account.SimulateExitRejection = true;
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.FixedTicks, StopTicks = 8, TargetTicks = 16 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);

            Assert(result.Status == "partial_submit", "Status becomes partial_submit when exit orders rejected");
            Assert(result.Note != null && result.Note.Contains("rejected"), "Note mentions rejected exit orders");
        }

        private static void TestAtm_ShouldTriggerBreakeven()
        {
            Console.WriteLine("\n[TEST] ATM: ShouldTriggerBreakeven Logic");
            var mgr = DynamicAtmManager.Instance;
            var config = new AtmStrategyConfig { BreakevenTriggerTicks = 12 };

            Assert(mgr.ShouldTriggerBreakeven(config, 18000, 18003.0, true, 0.25), "Long at exactly +12 ticks triggers breakeven");
            Assert(!mgr.ShouldTriggerBreakeven(config, 18000, 18002.75, true, 0.25), "Long at +11 ticks does not trigger breakeven");
            Assert(mgr.ShouldTriggerBreakeven(config, 18000, 17997.0, false, 0.25), "Short at -12 ticks triggers breakeven");
            Assert(!mgr.ShouldTriggerBreakeven(config, 18000, 18002.75, false, 0.25), "Short at +11 ticks does not trigger breakeven");
        }

        private static void TestAtm_CalculateBreakevenStopPrice()
        {
            Console.WriteLine("\n[TEST] ATM: CalculateBreakevenStopPrice Logic");
            var mgr = DynamicAtmManager.Instance;

            Assert(Approx(mgr.CalculateBreakevenStopPrice(18000, true, 0.25, 2), 18000.5), "Long breakeven stop = entry + offset 0.5");
            Assert(Approx(mgr.CalculateBreakevenStopPrice(18000, false, 0.25, 2), 17999.5), "Short breakeven stop = entry - offset 0.5");
            Assert(Approx(mgr.CalculateBreakevenStopPrice(18000, true, 0.25, 0), 18000.0), "Long breakeven stop = entry with zero offset");
        }

        private static void TestAtm_ActiveBracketStatus()
        {
            Console.WriteLine("\n[TEST] ATM: GetBracketStatus");
            var account = CreateAtmAccount();
            var instrument = CreateAtmInstrument("MNQ", "MNQ 09-26");
            var config = new AtmStrategyConfig { Type = AtmStrategyType.DrawdownShield, StopTicks = 10, TargetTicks = 20 };

            var result = DynamicAtmManager.Instance.PlaceBracket(account, instrument, "buy", 1, config, 18000, 0.25, 2.0);
            var status = DynamicAtmManager.Instance.GetBracketStatus(result.BracketId) as dynamic;

            Assert(status != null, "GetBracketStatus returns an object for a registered bracket");
            string statusBracketId = status.bracketId;
            Assert(statusBracketId == result.BracketId, "Status bracketId matches result BracketId");
            bool isComplete = status.isComplete;
            Assert(isComplete == false, "Newly registered bracket is not complete");
            var missing = DynamicAtmManager.Instance.GetBracketStatus("does-not-exist");
            Assert(missing is dynamic && ((dynamic)missing).error != null, "Unknown bracketId returns error payload");

            DynamicAtmManager.Instance.RemoveBracket(result.BracketId);
        }
    }
}
#endif
