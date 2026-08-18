// P2-127, slice 4: the SYSTEM ROW -- `docs/UI_REDESIGN_DESIGN.md` §4 decision 4, *"Selecting nothing
// shows the system row (feed / guard / copier) -- which is where `P3-34`'s two-or-three-indicator
// problem lives."*
//
// ⚠️ THREE CELLS, AND THE REASON IS THE WHOLE TICKET. §2.1's table records it in one line:
// *"`P3-34` (open) | the copier is ENFORCING **regardless of guard mode** | a single 'armed'
// indicator would be a lie."* MEASURED ON THE DEPLOYED BOX, 2026-08-17, and it is not hypothetical:
//
//     GET /api/riskguard/inventory?view=summary   mode: "shadow"   isArmed: true
//     GET /api/copier/snapshot   system.mode: "live"   system.isActing: TRUE
//
// The guard evaluates and enforces nothing, while the copier is live and acting, in the same process,
// at the same instant. Any surface that folds those into one badge is wrong whichever way it folds.
//
// ⚠️ NOTHING HERE RE-DERIVES A VERDICT SOMEBODY ELSE OWNS. The copier cell reads the `system` object
// the copier's own producer emits, and its rank comes from `BridgeFleetView.RankOfSystemSeverity`;
// `CopierEnforcementView` owns "is this relationship enforcing and why not", with a precedence that
// `P3-122` had to correct once already. `F-9` is the standing lesson: a REPORTED state drifted from
// the ENFORCED state in both directions, and the remedy was to derive the display FROM the enforcer
// rather than beside it. A second reader of one question is how these drift.
// [[a-second-reader-of-the-same-state]].
//
// ⚠️ AND THE FEED CELL IS WHERE FAIL-CLOSED WOULD LIE. Measured on the same box:
//
//     97 accounts   91 with connection: null and connectionStatus "Disconnected"
//                    6 on connection "TPT", all "Connected"
//
// Those 91 are expired prop accounts attached to no connection. "Any disconnected account means the
// feed is down" paints this box permanently red, which is exactly the defect that painted 95 of 97
// accounts as the worst thing on the fleet tree: fail-closed is for what you cannot READ, and an
// account on no connection is a question that does not apply. So the cell folds DISTINCT NAMED
// CONNECTIONS, and an account with no connection is not evidence about the feed either way.
// [[an-inapplicable-state-is-not-unreadable]].
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    public sealed class SystemCell
    {
        public string Id;
        public string Label;

        /// <summary>The shared scale. LOWER IS WORSE, as everywhere else on this page.</summary>
        public int Rank;

        /// <summary>Short, and never blank -- a blank cell over a bad state is the hiding hazard.</summary>
        public string Badge;

        public string Detail;
    }

    public static class BridgeSystemRow
    {
        public const string FeedCell = "feed";
        public const string GuardCell = "guard";
        public const string CopierCell = "copier";

        /// <summary>
        /// §2.1's most dangerous state, and the design states the rule without qualification:
        /// **"CONFIGURED and not EVALUATED renders red, everywhere, always."** It is the worst state
        /// this system can be in because the config file reads as protection that does not exist.
        /// Four shipped defects were this state. [[configured-evaluated-enforcing]].
        /// </summary>
        public const int ConfiguredNotEvaluatedRank = BridgeFleetView.WorstRank;

        /// <summary>
        /// The three cells, always all three and always in §4's order.
        ///
        /// ⚠️ ALWAYS THREE. An absent cell and an empty one read identically to whatever renders
        /// them -- that was the sixteenth mutant of slice 1's battery, and it survived a green suite.
        /// </summary>
        public static List<SystemCell> Build(JToken connections, JToken guardSummary, JToken copierSystem)
        {
            return new List<SystemCell>
            {
                BuildFeedCell(connections),
                BuildGuardCell(guardSummary),
                BuildCopierCell(copierSystem)
            };
        }

        /// <summary>
        /// The feed, folded over DISTINCT NAMED CONNECTIONS -- see the header. An account whose
        /// `connection` is null is attached to nothing and is not evidence about the feed.
        ///
        /// ⚠️ NO NAMED CONNECTION AT ALL IS THE WORST RANK, and that is not the same call as the 91
        /// above. Zero readable connections means this cell cannot answer its own question, and
        /// "is data arriving" answered by silence must not read as yes -- §3 says liveness is not
        /// optional precisely because a stalled feed and an idle one look identical.
        /// </summary>
        private static SystemCell BuildFeedCell(JToken connections)
        {
            JArray accounts = null;
            if (connections is JArray asArray) accounts = asArray;
            else if (connections is JObject asObject) accounts = asObject["accounts"] as JArray;

            var byConnection = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int attached = 0;

            if (accounts != null)
            {
                foreach (JToken account in accounts)
                {
                    JObject obj = account as JObject;
                    if (obj == null) continue;

                    string name = Str(obj["connection"]);
                    if (string.IsNullOrWhiteSpace(name)) continue;   // attached to nothing

                    attached++;
                    bool connected = string.Equals(Str(obj["connectionStatus"]), "Connected",
                        StringComparison.OrdinalIgnoreCase);

                    bool existing;
                    if (byConnection.TryGetValue(name, out existing))
                        byConnection[name] = existing && connected;   // one bad account taints its connection
                    else
                        byConnection[name] = connected;
                }
            }

            if (byConnection.Count == 0)
            {
                return new SystemCell
                {
                    Id = FeedCell,
                    Label = "Feed",
                    Rank = BridgeFleetView.UnknownRank,
                    Badge = "No connection",
                    Detail = "No account is attached to a named connection, so whether market data is "
                        + "arriving cannot be determined from here. A stalled feed and an idle one look "
                        + "identical, so this is not reported as healthy."
                };
            }

            var down = byConnection.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var up = byConnection.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

            if (down.Count > 0)
            {
                return new SystemCell
                {
                    Id = FeedCell,
                    Label = "Feed",
                    Rank = BridgeFleetView.WorstRank,
                    Badge = "Down (" + down.Count + ")",
                    Detail = "Not connected: " + string.Join(", ", down)
                        + (up.Count > 0 ? ". Connected: " + string.Join(", ", up) : "")
                        + ". Accounts attached to no connection are not counted either way."
                };
            }

            return new SystemCell
            {
                Id = FeedCell,
                Label = "Feed",
                Rank = BridgeInspectorTabs.CleanRank,
                Badge = "Connected (" + up.Count + ")",
                Detail = "Connected: " + string.Join(", ", up) + ", carrying " + attached
                    + " account" + (attached == 1 ? "" : "s") + ". Accounts attached to no connection "
                    + "are not counted either way -- on this box that is the large majority, and "
                    + "counting them would paint the feed permanently red."
            };
        }

        /// <summary>
        /// The guard, in §2.1's vocabulary rather than as a mode string.
        ///
        /// ⚠️ THE ORDER OF THESE BRANCHES IS THE ANSWER. With more than one condition true, the cell
        /// must name the one that BINDS -- and `unevaluatedRules` binds hardest, because a rule
        /// nothing reads cannot be rescued by arming or by any mode.
        /// [[rank-refusal-reasons-by-what-binds]].
        /// </summary>
        private static SystemCell BuildGuardCell(JToken guardSummary)
        {
            JObject summary = guardSummary as JObject;
            if (summary == null)
            {
                return new SystemCell
                {
                    Id = GuardCell,
                    Label = "Guard",
                    Rank = BridgeFleetView.UnknownRank,
                    Badge = "Unreadable",
                    Detail = "The guard did not return a state summary, so whether it is enforcing "
                        + "anything is unknown. Not reported as healthy."
                };
            }

            string mode = Str(summary["mode"]);
            bool isArmed = summary["isArmed"] != null && summary["isArmed"].Type == JTokenType.Boolean
                && (bool)summary["isArmed"];

            JArray unevaluated = summary["unevaluatedRules"] as JArray;
            int unevaluatedCount = unevaluated == null ? 0 : unevaluated.Count;

            if (unevaluatedCount > 0)
            {
                return new SystemCell
                {
                    Id = GuardCell,
                    Label = "Guard",
                    Rank = ConfiguredNotEvaluatedRank,
                    Badge = "Not evaluated (" + unevaluatedCount + ")",
                    Detail = unevaluatedCount + " configured rule"
                        + (unevaluatedCount == 1 ? " is" : "s are") + " read by no code, so "
                        + (unevaluatedCount == 1 ? "it describes" : "they describe")
                        + " protection that does not exist however this guard is armed. This is the "
                        + "worst state the system can be in, because the config file reads as safe."
                };
            }

            if (!isArmed)
            {
                return new SystemCell
                {
                    Id = GuardCell,
                    Label = "Guard",
                    Rank = 1,
                    Badge = "Disarmed",
                    Detail = "The guard is NOT armed, so no rule can act whatever mode '"
                        + (string.IsNullOrWhiteSpace(mode) ? "(unset)" : mode) + "' says."
                };
            }

            if (string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase))
            {
                return new SystemCell
                {
                    Id = GuardCell,
                    Label = "Guard",
                    Rank = BridgeInspectorTabs.CleanRank,
                    Badge = "Enforcing",
                    Detail = "Armed and in 'live', so rules can act."
                };
            }

            if (string.Equals(mode, "shadow", StringComparison.OrdinalIgnoreCase))
            {
                // ⚠️ NOT AN ERROR AND NOT CLEAN. §2.1: shadow is "EVALUATED, not ENFORCING -- correct
                // and deliberate, but must be unmistakable". Ranking it clean is how an operator comes
                // to believe a limit is protecting them; ranking it worst is how the page cries wolf
                // through the entire shadow-validation period this project is deliberately in.
                return new SystemCell
                {
                    Id = GuardCell,
                    Label = "Guard",
                    Rank = 3,
                    Badge = "Shadow",
                    Detail = "Armed and EVALUATING every rule, but in 'shadow' it ENFORCES NOTHING -- "
                        + "each rule logs the action it would have taken and takes none. Deliberate, "
                        + "and it means no configured limit will stop anything."
                };
            }

            return new SystemCell
            {
                Id = GuardCell,
                Label = "Guard",
                Rank = BridgeFleetView.UnknownRank,
                Badge = "Mode '" + (string.IsNullOrWhiteSpace(mode) ? "(unset)" : mode) + "'",
                Detail = "The guard mode is not one of live/shadow, so what it does cannot be "
                    + "stated from here. Not reported as healthy."
            };
        }

        /// <summary>
        /// The copier, read from the `system` object its OWN producer emits -- headline, detail and
        /// severity. Nothing here re-decides whether the copier is acting; see the header.
        ///
        /// ⚠️ A CONFLICT SIMPLY IS THE WORST RANK, and the first version folded it with `WorstOf`
        /// against the severity to guarantee it "could not make a bad severity look better". The
        /// battery proved that guarantee vacuous: `WorstRank` is 0 and `WorstOf` returns the MINIMUM,
        /// so the fold and a plain assignment are the same function for every input. The mutant
        /// replacing one with the other survived because there is nothing to observe. Written the
        /// direct way, with the reason recorded, rather than left as machinery that reads like a
        /// safeguard and is not. [[a-green-that-can-never-be-red]].
        /// </summary>
        private static SystemCell BuildCopierCell(JToken copierSystem)
        {
            JObject system = copierSystem as JObject;
            if (system == null)
            {
                return new SystemCell
                {
                    Id = CopierCell,
                    Label = "Copier",
                    Rank = BridgeFleetView.UnknownRank,
                    Badge = "Unreadable",
                    Detail = "The copier did not return a system state, so whether a leader fill would "
                        + "be mirrored is unknown. Not reported as healthy."
                };
            }

            bool loaded = system["loaded"] != null && system["loaded"].Type == JTokenType.Boolean
                && (bool)system["loaded"];
            if (!loaded)
            {
                return new SystemCell
                {
                    Id = CopierCell,
                    Label = "Copier",
                    Rank = BridgeFleetView.WorstRank,
                    Badge = "Not loaded",
                    Detail = "The copier config did not load, so no relationship exists and nothing "
                        + "would be mirrored."
                };
            }

            int severityRank = BridgeFleetView.RankOfSystemSeverity(Str(system["severity"]));
            int conflicts = system["configConflicts"] != null
                && system["configConflicts"].Type == JTokenType.Integer
                    ? (int)system["configConflicts"] : 0;

            // A conflict is the worst rank there is; see the doc comment for why this is not a fold.
            int rank = conflicts > 0 ? BridgeFleetView.WorstRank : severityRank;

            string headline = Str(system["headline"]);
            string detail = Str(system["detail"]);
            string mode = Str(system["mode"]);
            bool isActing = system["isActing"] != null && system["isActing"].Type == JTokenType.Boolean
                && (bool)system["isActing"];

            // ⚠️ THE BADGE NAMES THE MODE AND WHETHER IT IS ACTING, INDEPENDENTLY OF THE GUARD'S.
            // This is P3-34 rendered: on the measured box the guard reads 'Shadow' and this reads
            // 'live, acting' in the same row, and an operator who has only ever seen one "armed"
            // light would have read the whole system as inert.
            string badge = (string.IsNullOrWhiteSpace(mode) ? "(unset)" : mode)
                + (isActing ? ", acting" : ", not acting")
                + (conflicts > 0 ? ", " + conflicts + " conflict" + (conflicts == 1 ? "" : "s") : "");

            return new SystemCell
            {
                Id = CopierCell,
                Label = "Copier",
                Rank = rank,
                Badge = badge,
                Detail = string.IsNullOrWhiteSpace(headline)
                    ? (string.IsNullOrWhiteSpace(detail) ? "No copier headline reported." : detail)
                    : headline + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)
            };
        }

        /// <summary>
        /// The worst of the three, folded from the cells themselves rather than counted separately
        /// (`F-9`; `P2-103` recounts from the detail rows for the same reason).
        /// </summary>
        public static int WorstRankOf(IEnumerable<SystemCell> cells)
        {
            if (cells == null) return BridgeFleetView.UnknownRank;
            return BridgeFleetView.WorstOf(cells.Where(c => c != null).Select(c => c.Rank));
        }

        private static string Str(JToken token)
        {
            if (token == null) return "";
            if (token.Type == JTokenType.Null) return "";
            if (token.Type == JTokenType.String) return (string)token;
            return token.ToString();
        }
    }
}
