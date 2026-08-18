// P2-127, slice 4: the EVENTS pane -- the bottom region of `docs/UI_REDESIGN_DESIGN.md` §4,
// "EVENTS -- filtered to selection", and the last unbuilt region of that layout.
//
// ⚠️ THE WHOLE REASON THIS IS A CLASS AND NOT JAVASCRIPT. `ui/index.html` is in no test build and no
// mutation battery, exactly like `TradeCopierWindow.cs`, and `P2-138` is what happens when view logic
// is written where nothing executes it. Every decision below -- what is noise, what collapses, what a
// selection includes, how bad a line is -- lives here. The page renders what this returns.
//
// ⚠️ AND THE DECISIONS WERE FORCED BY A MEASUREMENT, NOT BY THE SHAPE OF THE CODE. `interventions.jsonl`
// on the deployed box, 2026-08-17:
//
//     file size                                43 766 928 bytes  (~44 MB, one day)
//     last 3 MB                                     10 877 lines
//       SUBSCRIBE                                     8 148      75%
//       ORDER_UPDATE                                  1 199      11%
//       SESSION_RESET / CONNECTION_CHANGE / FSM_*        ~470
//       INTERVENTION                                      97   <- what an operator wants
//       ATM_STOP_ORDER_NOT_FOUND                          63
//       ARMED_ON_START / INITIALIZE / UI_INJECT       84 each
//       NAKED_POSITION                                    20
//       LOCKOUT_* (six types)                             33
//
// So **86% of the log is per-tick telemetry**, and a pane that renders the tail verbatim shows an
// operator nothing but `SUBSCRIBE`. That is not a cosmetic problem: it is the same hazard §4.2 killed
// top-level tabs over -- this page exists so a bad state is seen WITHOUT BEING LOOKED FOR, and 8 148
// subscribe lines hide 20 naked positions just as effectively as a nav tab would.
//
// ⚠️ THE FILTER IS A DENYLIST, AND THAT DIRECTION IS DELIBERATE. An allowlist means a newly-added
// event type is INVISIBLE until somebody remembers to add it -- and the events most worth showing are
// the ones added by the most recent fix, which is precisely when nobody remembers. An unknown event
// type therefore RENDERS. The cost is that a future high-volume telemetry type floods the pane until
// it is named here; the cost the other way is an alarm wired to a surface that will not show it.
// [[an-alarm-wired-to-a-dead-output]], [[dead-safety-machinery-gate]].
//
// ⚠️ AND REPEATS COLLAPSE, because a denylist alone is not enough. `ARMED_ON_START` appears 84 times
// in that tail, `CONNECTION_CHANGE` 171 -- none of it telemetry, all of it the same sentence. Eighty-
// four identical rows is an unusable pane by a different route, so consecutive identical
// (eventType, account) rows become ONE row carrying a count. The count is kept rather than dropped:
// "the connection cycled 84 times" is a materially different fact from "the connection cycled".
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    public sealed class EventRow
    {
        public string Utc;
        public string Account;
        public string EventType;
        public string Message;

        /// <summary>
        /// The fleet's rank scale, shared with `BridgeFleetView` and the inspector's tabs, because
        /// two severity scales on one page is two things to keep in step. LOWER IS WORSE.
        /// </summary>
        public int Rank;

        /// <summary>
        /// How many consecutive identical events this row stands for. 1 for a single event; never 0,
        /// because a row that stands for nothing should not exist.
        /// </summary>
        public int Count;

        /// <summary>
        /// True when this event belongs to the whole box rather than to an account -- and those are
        /// shown WHATEVER is selected. See <see cref="Filter"/>.
        /// </summary>
        public bool IsSystemScoped;
    }

    public static class BridgeEventsView
    {
        /// <summary>
        /// The measured 86%. Per-tick telemetry that is written for the AUDIT record and is not news:
        /// every one of these is a fact the fleet tree and the inspector already show as STATE, which
        /// is the right place for it -- a position's size belongs on the position, not in a feed.
        ///
        /// ⚠️ `FSM_TRANSITION` is in here and `FSM_UNDERCOVERED` is NOT, deliberately. A transition is
        /// the machine working; undercovered is the machine reporting a position with less protection
        /// than it should have. They differ by one word and by everything else.
        /// </summary>
        private static readonly HashSet<string> Telemetry = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SUBSCRIBE",
            "ORDER_UPDATE",
            "POSITION_UPDATE",
            "EXECUTION_UPDATE",
            "FSM_TRANSITION",
            "FSM_UPDATE",
            "FSM_SEED",
            "COPIER_EXEC_SEEN",
            "UI_INJECT",
            "SESSION_RESET",
            "HEARTBEAT"
        };

        /// <summary>
        /// Event types that are WORST -- the guard acted, or a position is unprotected. Ranked
        /// explicitly rather than inferred from a name, because inferring severity from a substring is
        /// how `"1"` came to be matched by `OCO-P140`. [[a-substring-assertion-catches-the-identifier]].
        /// </summary>
        private static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INTERVENTION",
            "NAKED_POSITION",
            "FSM_UNDERCOVERED",
            "LOCKOUT_CONFIRMED",
            "LOCKOUT_STUCK",
            "LOCKOUT_FLATTEN_RETRY",
            "ATM_STOP_MOVE_ABANDONED",
            "ATM_STOP_ORDER_NOT_FOUND",
            "ATM_BRACKET_UNPROTECTED",
            "ATM_BRACKET_MISMATCHED",
            "ATM_BRACKET_RESTORE_ABANDONED",
            "ATM_BRACKET_RESTORE_FAILED",
            "ATM_MONITOR_NO_DISPATCHER",
            "ERROR"
        };

        /// <summary>Something was refused, suppressed or ignored -- not an intervention, not routine.</summary>
        private static readonly HashSet<string> Warning = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ATM_STOP_CHANGE_IGNORED",
            "ATM_STOP_MOVE_IN_FLIGHT",
            "ACTION_SUPPRESSED",
            "AUDIT_FINDING_SUPPRESSED",
            "COPIER_MODE_CHANGE_REFUSED",
            "COPIER_NO_ACTIVE_RELATIONSHIPS",
            "LOCKOUT_LAPSED",
            "LOCKOUT_PHASE",
            "LOCKOUT_SWEEP_SHADOW",
            "SHADOW_LOCKOUT",
            "SHADOW_PENDING_CANCEL",
            "ENTRY_CANCEL",
            "ATM_PARTIAL_PROFIT_UNAVAILABLE",
            "ATM_STOP_MOVE_WRONG_WAY",
            "ATM_BRACKET_RESTORE_DEFERRED",
            "WARNING"
        };

        /// <summary>
        /// The rank an event type carries. LOWER IS WORSE, matching the tree and the tabs.
        ///
        /// ⚠️ AN UNRECOGNISED TYPE IS `Warning`, NOT CLEAN. It is the fail-LOUD direction and it is
        /// the one this class has to get right: the events most worth surfacing are the ones a recent
        /// fix added, and ranking those as routine would put the newest alarm at the bottom of the
        /// pane. Compare `BridgeInspectorTabs.RankOfRuleState`, where an unreadable state ranks worst
        /// for the same reason. [[detector-needs-a-negative-test]].
        /// </summary>
        public static int RankOfEventType(string eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType))
                return BridgeFleetView.UnknownRank;

            string key = eventType.Trim();
            if (Critical.Contains(key)) return BridgeFleetView.WorstRank;   // 0
            if (Warning.Contains(key)) return 1;

            // Named, recognised, routine: ARMED_ON_START, INITIALIZE, CONNECTION_CHANGE,
            // SHADOW_ACTION, ATM_BRACKET_RELEASED, COPIER_MODE_CHANGED, and anything new.
            if (Routine.Contains(key)) return 3;

            return 1;
        }

        /// <summary>
        /// Recognised and NOT a problem. Listed so that the `default` above can be the loud answer:
        /// with no such list, either everything unknown reads as routine (quiet, wrong) or every
        /// ordinary startup line reads as a warning (loud, useless). Naming the routine ones is what
        /// lets the unknown case be loud without the pane crying wolf on every boot.
        /// </summary>
        private static readonly HashSet<string> Routine = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INITIALIZE",
            "ARMED_ON_START",
            "CONNECTION_CHANGE",
            "SHADOW_ACTION",
            "SHADOW_SESSION",
            "ATM_BRACKET_RELEASED",
            "ATM_BRACKET_RESTORED",
            "ATM_STOP_MOVE_REQUESTED",
            "ATM_STOP_MOVE_CONFIRMED",
            "COPIER_MODE_CHANGED",
            "FIRM_STATE_UPDATE",
            "FSM_WATCHDOG",
            "CONFIG_SAVED",
            "LOCKOUT_CLEARED"
        };

        /// <summary>Whether an event type is the per-tick telemetry the pane drops.</summary>
        public static bool IsTelemetry(string eventType)
        {
            return !string.IsNullOrWhiteSpace(eventType) && Telemetry.Contains(eventType.Trim());
        }

        /// <summary>
        /// Whether an event belongs to the whole box rather than one account.
        ///
        /// ⚠️ `"SYSTEM"` IS AN ACCOUNT NAME IN THIS LOG, not a real account: `RiskGuardAddOn.LogEvent`
        /// is called with the literal `"SYSTEM"` for `INITIALIZE`, `ARMED_ON_START` and `ERROR`, and
        /// with `""` from `LogFromComponent` when a component has no account. Both mean the same thing
        /// and both must be treated as box-wide, or half the box-wide events vanish behind a selection.
        /// </summary>
        public static bool IsSystemScope(string account)
        {
            return string.IsNullOrWhiteSpace(account)
                || string.Equals(account.Trim(), "SYSTEM", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One `interventions.jsonl` line to a row, or null if it is telemetry or unparseable.
        ///
        /// Unparseable returns null rather than a row saying so: a truncated final line is the NORMAL
        /// state of a file being appended to while it is read, and one such row per poll would be a
        /// permanent fixture in the pane.
        /// </summary>
        public static EventRow ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            JObject obj;
            try
            {
                // ⚠️ `DateParseHandling.None`, AND IT IS NOT A DETAIL. `JObject.Parse` defaults to
                // converting anything that looks like an ISO-8601 string into a `Date` TOKEN, so
                // `timestamp_utc` stopped being the string the log wrote: reading it back produced
                // `8/17/2026 10:30:00 PM` -- the machine's LOCALE, in LOCAL TIME, from a field whose
                // name says UTC. Found by an acceptance test asserting the collapsed row's timestamp,
                // which is not what that test was written to check.
                //
                // The pane must show the instant the audit record recorded. A silent timezone shift on
                // an event log is the kind of wrongness an operator cannot see and would correlate
                // against a chart with.
                using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(line)))
                {
                    reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;
                    obj = JObject.Load(reader);
                }
            }
            catch { return null; }

            string eventType = Str(obj["eventType"]);
            if (string.IsNullOrWhiteSpace(eventType)) return null;
            if (IsTelemetry(eventType)) return null;

            string account = Str(obj["account"]);
            string message = null;
            JToken data = obj["data"];
            if (data != null)
            {
                JToken m = data["message"];
                message = m != null ? Str(m) : data.ToString(Newtonsoft.Json.Formatting.None);
            }

            return new EventRow
            {
                Utc = Str(obj["timestamp_utc"]),
                Account = account,
                EventType = eventType,
                Message = message ?? "",
                Rank = RankOfEventType(eventType),
                Count = 1,
                IsSystemScoped = IsSystemScope(account)
            };
        }

        /// <summary>
        /// Consecutive identical (eventType, account) rows become ONE row carrying a count.
        ///
        /// ⚠️ CONSECUTIVE, not global. Collapsing globally would merge a `NAKED_POSITION` from this
        /// morning with one from a minute ago and report a single event with count 2 at the wrong time
        /// -- which is worse than either, because the timestamp is the actionable half. Two runs of
        /// the same event separated by anything else stay two rows.
        ///
        /// ⚠️ THE MESSAGE IS NOT PART OF THE KEY, and that is measured rather than assumed: the 84
        /// `ARMED_ON_START` lines and the 171 `CONNECTION_CHANGE` lines carry the same sentence, while
        /// `ORDER_UPDATE` -- the type whose message varies per tick -- is telemetry and never reaches
        /// here. Keying on the message would leave 63 `ATM_STOP_ORDER_NOT_FOUND` rows uncollapsed,
        /// since each names its own order.
        /// </summary>
        public static List<EventRow> Collapse(IEnumerable<EventRow> rows)
        {
            List<EventRow> result = new List<EventRow>();
            if (rows == null) return result;

            foreach (EventRow row in rows)
            {
                if (row == null) continue;

                EventRow last = result.Count > 0 ? result[result.Count - 1] : null;
                bool same = last != null
                    && string.Equals(last.EventType, row.EventType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.Account ?? "", row.Account ?? "", StringComparison.OrdinalIgnoreCase);

                if (same)
                {
                    last.Count = last.Count + 1;
                    // The row keeps the LATEST timestamp of the run, because "when did this last
                    // happen" is the question an operator asks of a repeating event.
                    last.Utc = row.Utc;
                    continue;
                }

                result.Add(row);
            }

            return result;
        }

        /// <summary>
        /// §4's "filtered to selection".
        ///
        /// ⚠️ SYSTEM-SCOPED EVENTS ARE ALWAYS SHOWN, and this is the load-bearing decision in the
        /// method. `ATM_MONITOR_NO_DISPATCHER`, `ATM_BRACKET_RESTORE_FAILED` and every `ERROR` are
        /// logged with no account, so a literal reading of "filtered to selection" would HIDE the
        /// worst events on the box the moment an operator clicked an account -- and clicking an
        /// account is the normal way to use this page. §4.2 killed top-level tabs over exactly that:
        /// nothing may put a bad state behind an interaction.
        ///
        /// Case-insensitively, as the core compares account names everywhere.
        /// </summary>
        public static List<EventRow> Filter(IEnumerable<EventRow> rows, string selectedAccount)
        {
            List<EventRow> result = new List<EventRow>();
            if (rows == null) return result;

            foreach (EventRow row in rows)
            {
                if (row == null) continue;
                if (selectedAccount == null
                    || row.IsSystemScoped
                    || string.Equals(row.Account, selectedAccount, StringComparison.OrdinalIgnoreCase))
                    result.Add(row);
            }

            return result;
        }

        /// <summary>
        /// The pane: parse, collapse, filter to the selection, newest first, capped.
        ///
        /// ⚠️ `lines` MUST BE IN FILE ORDER (oldest first), because `Collapse` reads runs and the
        /// reversal happens after it. Handing this a reversed list would collapse correctly and stamp
        /// each run with its OLDEST timestamp.
        ///
        /// ⚠️ THE CAP IS APPLIED LAST. Capping before collapsing would spend the whole budget on one
        /// run of 84 identical lines -- which is the measured case, not a hypothetical.
        /// </summary>
        public static List<EventRow> Build(IEnumerable<string> lines, string selectedAccount, int max)
        {
            List<EventRow> parsed = new List<EventRow>();
            if (lines != null)
            {
                foreach (string line in lines)
                {
                    EventRow row = ParseLine(line);
                    if (row != null) parsed.Add(row);
                }
            }

            List<EventRow> collapsed = Collapse(parsed);
            List<EventRow> filtered = Filter(collapsed, selectedAccount);

            filtered.Reverse();   // newest first

            if (max > 0 && filtered.Count > max)
                filtered = filtered.Take(max).ToList();

            return filtered;
        }

        /// <summary>
        /// The worst rank in the pane, for the always-visible summary beside it.
        ///
        /// Folded from the ROWS the pane renders, never counted separately -- `F-9`, and `P2-103`
        /// recounts from the detail rows for the same reason. An empty pane is
        /// <see cref="BridgeInspectorTabs.CleanRank"/>: a log that was read and held nothing worth
        /// showing is not an unread log. [[an-inapplicable-state-is-not-unreadable]].
        /// </summary>
        public static int WorstRankOf(IEnumerable<EventRow> rows)
        {
            if (rows == null) return BridgeInspectorTabs.CleanRank;

            List<int> ranks = rows.Where(r => r != null).Select(r => r.Rank).ToList();
            if (ranks.Count == 0) return BridgeInspectorTabs.CleanRank;

            return BridgeFleetView.WorstOf(ranks);
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
