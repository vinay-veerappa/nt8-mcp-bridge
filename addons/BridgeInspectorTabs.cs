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
        /// state is the hiding hazard section 4.2 killed top-level tabs for.</summary>
        public string Badge;

        public string Reason;
    }

    public static class BridgeInspectorTabs
    {
        public const string CopierTab = "copier";
        public const string RiskTab = "risk";
        public const string RareTab = "rare";

        /// <summary>
        /// The rank for a state that is KNOWN and CLEAN, above every copier severity (0-5) and every
        /// rule state, and below <see cref="BridgeFleetView.NotApplicableRank"/> (6), which means
        /// "this question does not apply here".
        ///
        /// ⚠️ IT EXISTS BECAUSE `WorstOf(new int[0])` IS THE WRONG ANSWER FOR A KNOWN ZERO, and the
        /// first implementation used it: a rare tab with no config conflicts folded an empty set,
        /// got <see cref="BridgeFleetView.UnknownRank"/> — which IS `WorstRank` — and rendered as the
        /// worst thing on the strip while its own badge read "No conflicts". What a surface REPORTS
        /// disagreeing with what it RANKS is the defect, in the direction that trains an operator to
        /// discount the strip.
        ///
        /// `WorstOf`'s empty answer is deliberately pessimistic and that is right for a set it could
        /// not read — a group with no children is not healthy. Zero conflicts is not an unread set;
        /// it is a set that was read and was empty. `NotApplicableRank`'s own comment in
        /// `BridgeFleetView` makes exactly this distinction, which is why it is not `UnknownRank`.
        /// [[an-inapplicable-state-is-not-unreadable]].
        /// </summary>
        public const int CleanRank = 5;

        private const string NoDataLabel = "No data";
        private const string UnrecognisedLabel = "Unrecognised";

        /// <summary>Ranks a guard rule's state. Lower is worse.</summary>
        public static int RankOfRuleState(string state)
        {
            if (string.IsNullOrEmpty(state))
                return BridgeFleetView.UnknownRank;

            switch (state)
            {
                case "ConfiguredNotEvaluated": return 0;
                case "Inert": return 1;
                case "Disabled": return 2;
                case "EvaluatedNotEnforcing": return 3;
                default: return BridgeFleetView.RankOfSystemSeverity(state);
            }
        }

        /// <summary>
        /// The three tabs, always all three and always in section 4's order, with each one's worst state
        /// folded from the rows it would show.
        /// </summary>
        public static List<InspectorTab> Build(
            IList<FleetCopierRow> copierRows,
            IList<InspectorRuleRow> ruleRows,
            string selectedAccount,
            int configConflicts)
        {
            if (copierRows == null)
                copierRows = new List<FleetCopierRow>();
            if (ruleRows == null)
                ruleRows = new List<InspectorRuleRow>();

            return new List<InspectorTab>
            {
                BuildCopierTab(copierRows),
                BuildRiskTab(ruleRows, selectedAccount),
                BuildRareTab(configConflicts)
            };
        }

        /// <summary>Maps the live `/api/riskguard/inventory` payload onto Build's input.</summary>
        public static List<InspectorRuleRow> RuleRowsFromInventory(JToken accounts)
        {
            List<InspectorRuleRow> rows = new List<InspectorRuleRow>();
            if (accounts == null)
                return rows;

            JArray array = accounts as JArray;
            if (array == null && accounts is JObject root)
                array = root["accounts"] as JArray;

            if (array == null)
                return rows;

            foreach (JToken account in array)
            {
                JObject accountObj = account as JObject;
                string accountName = accountObj != null
                    ? SafeString(accountObj["accountName"])
                    : SafeString(account);

                JArray rules = accountObj?["rules"] as JArray;
                if (rules == null)
                    continue;

                foreach (JToken rule in rules)
                {
                    JObject ruleObj = rule as JObject;
                    string ruleName = ruleObj != null
                        ? SafeString(ruleObj["name"])
                        : string.Empty;
                    string state = ruleObj != null
                        ? SafeString(ruleObj["state"])
                        : SafeString(rule);

                    rows.Add(new InspectorRuleRow
                    {
                        AccountName = accountName,
                        RuleName = ruleName,
                        State = state
                    });
                }
            }

            return rows;
        }

        private static InspectorTab BuildCopierTab(IList<FleetCopierRow> rows)
        {
            List<int> ranks = new List<int>(rows.Count);
            foreach (FleetCopierRow row in rows)
                ranks.Add(BridgeFleetView.RankOfCopierRow(row.Severity));

            int worstRank = BridgeFleetView.WorstOf(ranks);
            int count = 0;
            foreach (FleetCopierRow row in rows)
            {
                if (BridgeFleetView.RankOfCopierRow(row.Severity) == worstRank)
                    count++;
            }

            string name;
            if (rows.Count == 0)
                name = NoDataLabel;
            else if (worstRank == BridgeFleetView.UnknownRank)
                name = UnrecognisedLabel;
            else
                name = "Severity " + worstRank.ToString();

            return new InspectorTab
            {
                Id = CopierTab,
                Label = "Copier",
                Rank = worstRank,
                Badge = FormatBadge(name, count),
                Reason = rows.Count == 0
                    ? "No copier rows available."
                    : "Worst copier severity is " + name + " across " + count + " row" + (count == 1 ? "" : "s") + "."
            };
        }

        private static InspectorTab BuildRiskTab(IList<InspectorRuleRow> rows, string selectedAccount)
        {
            List<InspectorRuleRow> filtered = new List<InspectorRuleRow>();
            foreach (InspectorRuleRow row in rows)
            {
                // OrdinalIgnoreCase, matching how the core compares account names everywhere
                // (`a.Name.Equals(bracket.AccountName, StringComparison.OrdinalIgnoreCase)`). A
                // case-sensitive compare here would return a well-formed "No data" tab for an
                // account that is present — a quiet wrong answer rather than a visible one.
                if (selectedAccount == null
                    || string.Equals(row.AccountName, selectedAccount, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(row);
            }

            List<int> ranks = new List<int>(filtered.Count);
            foreach (InspectorRuleRow row in filtered)
                ranks.Add(RankOfRuleState(row.State));

            int worstRank = BridgeFleetView.WorstOf(ranks);
            int count = 0;
            string worstStateName = null;
            foreach (InspectorRuleRow row in filtered)
            {
                if (RankOfRuleState(row.State) == worstRank)
                {
                    count++;
                    if (worstStateName == null)
                        worstStateName = row.State;
                }
            }

            string name;
            if (filtered.Count == 0)
                name = NoDataLabel;
            else if (worstRank == BridgeFleetView.UnknownRank)
                name = UnrecognisedLabel;
            else
                name = worstStateName ?? UnrecognisedLabel;

            return new InspectorTab
            {
                Id = RiskTab,
                Label = "Risk",
                Rank = worstRank,
                Badge = FormatBadge(name, count),
                Reason = filtered.Count == 0
                    ? "No guard rule rows available."
                    : "Worst guard rule state is " + name + " across " + count + " row" + (count == 1 ? "" : "s") + "."
            };
        }

        private static InspectorTab BuildRareTab(int configConflicts)
        {
            // A conflict is the worst; NO conflict is CLEAN, not unknown. See CleanRank: folding an
            // empty set here returned UnknownRank -- which is WorstRank -- and painted a tab whose
            // own badge said "No conflicts" as the worst thing on the strip.
            int worstRank = configConflicts > 0 ? BridgeFleetView.WorstRank : CleanRank;

            string name = configConflicts > 0 ? "Conflict" : "No conflicts";

            // ⚠️ THE LABEL IS "Settings", NOT "Rare". §4 decision 3 puts set-rarely config in the
            // inspector, and this tab IS that: it holds the guard config editor. Commit a983455 moved
            // `<div id="config">` inside `#tabpanes`, so the editor sat behind a tab labelled "Rare"
            // whose badge read "No conflicts (0)" -- and the operator concluded the settings had
            // been lost, because nothing on the tab said settings. No data was lost; the label was.
            // The conflicts badge is a SEPARATE signal (the tab's worst state, per §4.2's rule that
            // a bad state stays visible without being looked for) and it keeps its own wording.
            return new InspectorTab
            {
                Id = RareTab,
                Label = "Settings",
                Rank = worstRank,
                Badge = FormatBadge(name, configConflicts),
                Reason = configConflicts > 0
                    ? configConflicts + " configuration conflict" + (configConflicts == 1 ? "" : "s") + " present."
                    : "No configuration conflicts present."
            };
        }

        private static string FormatBadge(string name, int count)
        {
            return name + " (" + count.ToString() + ")";
        }

        private static string SafeString(JToken token)
        {
            if (token == null)
                return string.Empty;
            if (token.Type == JTokenType.String)
                return (string)token;
            if (token.Type == JTokenType.Null)
                return string.Empty;
            return token.ToString();
        }
    }
}
