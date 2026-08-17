using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// P2-127, slice 3: the INSPECTOR's three tabs, and the only tabs in the application.
    /// `docs/UI_REDESIGN_DESIGN.md` §4 is titled "one window, two panes, zero nav tabs" and §4.2
    /// lists top-level navigation tabs among the things "killed by the operator's constraints,
    /// recorded so nobody re-adds them". These are not those: they live INSIDE the inspector, on the
    /// selected entity, which is what §4 specifies -- `[copier] [risk] [rare]`.
    ///
    /// ⚠️ THE WHOLE REASON THIS IS A CLASS AND NOT JAVASCRIPT. `ui/index.html` is in no test build
    /// and no mutation battery, exactly like `TradeCopierWindow.cs`. P2-127's own entry says it:
    /// "move the decisions -- which badge, which severity, what the tree contains -- into a class the
    /// harness compiles, the way CopierStatusView and CopierSymbolMatrixView do for the WPF window.
    /// Otherwise this grows a third untested surface, and it will be the one the operator actually
    /// uses." The page renders what this returns and decides nothing.
    ///
    /// ⚠️ AND THE HAZARD THAT MAKES TABS DANGEROUS AT ALL, which is why §4.2 killed the top-level
    /// ones. This page's value is that `Inert`, `ConfiguredNotEvaluated` and a non-acting copier are
    /// visible WITHOUT BEING LOOKED FOR. Anything that puts a section behind a click must carry that
    /// section's worst state into the always-visible strip -- folded out of the same payload the
    /// section renders, and never from its own counters (`F-9`; `P2-103` recounts from the detail
    /// rows for this reason).
    ///
    /// ⚠️ MEASURED ON THE DEPLOYED BOX, 2026-08-17, and it decides what the risk tab folds:
    /// `GET /api/riskguard/inventory` returns 97 accounts x 23 rules = 2231 rule rows, with
    /// `unevaluatedRules` EMPTY and the state histogram `EvaluatedNotEnforcing` 1129 / `Inert` 559 /
    /// `Disabled` 543. So on a box with accounts loaded, **`Inert` is the worst state that exists**,
    /// and a strip watching only `unevaluatedRules` would render three clean tabs over exactly the
    /// condition this page exists to make unmissable.
    /// </summary>
    public class InspectorRuleRow
    {
        public string AccountName;
        public string RuleName;
        public string State;
    }

    public class InspectorTab
    {
        public string Id;
        public string Label;

        /// <summary>
        /// The fleet's rank scale, deliberately: `BridgeFleetView.WorstOf` and the tree's ordering
        /// already speak it, and two severity scales in one page is two things to keep in step.
        /// Lower is worse.
        /// </summary>
        public int Rank;

        /// <summary>Short, always set, and NEVER blank for a bad state -- a blank badge over a bad
        /// state is the hiding hazard §4.2 killed top-level tabs for.</summary>
        public string Badge;

        public string Reason;
    }

    public static class BridgeInspectorTabs
    {
        public const string CopierTab = "copier";
        public const string RiskTab = "risk";
        public const string RareTab = "rare";

        /// <summary>Ranks a guard rule's state. Lower is worse.</summary>
        public static int RankOfRuleState(string state)
        {
            // P2-127 slice 3: not implemented. Returns a benign constant rather than throwing, so
            // the acceptance tests fail on their own ASSERTIONS and the suite still prints a RESULTS
            // line. A stub that throws takes the whole runner down, and "no result line" is not a
            // red test -- it is an unusable baseline.
            return 0;
        }

        /// <summary>
        /// The three tabs, always all three and always in §4's order, with each one's worst state
        /// folded from the rows it would show.
        /// </summary>
        public static List<InspectorTab> Build(
            IList<FleetCopierRow> copierRows,
            IList<InspectorRuleRow> ruleRows,
            string selectedAccount,
            int configConflicts)
        {
            return new List<InspectorTab>();      // P2-127 slice 3: not implemented
        }

        /// <summary>Maps the live `/api/riskguard/inventory` payload onto Build's input.</summary>
        public static List<InspectorRuleRow> RuleRowsFromInventory(JToken accounts)
        {
            return new List<InspectorRuleRow>();  // P2-127 slice 3: not implemented
        }
    }
}
