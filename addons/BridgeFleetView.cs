// P2-127, section 4 of docs/UI_REDESIGN_DESIGN.md (nt8-riskguard): the FLEET pane.
//
// §4 is "Layout -- one window, two panes, zero nav tabs", and it was RE-CONFIRMED by the
// operator on 2026-08-16 against a live challenge. The left column lists ENTITIES -- groups
// with their followers nested, then unlinked accounts -- and selecting one drives the
// inspector. It is not a list of sections, and it has no navigation tabs; §4.2 kills those
// explicitly, "recorded so nobody re-adds them".
//
// WHY THIS IS A COMPILED CLASS AND NOT JAVASCRIPT. `ui/index.html` is in no test build and
// no mutation battery, exactly like `TradeCopierWindow.cs` before `P1-121`. P2-127's own
// plan entry says so: "Move the decisions -- which badge, which severity, what the tree
// contains -- into a class the harness compiles ... Otherwise this grows a third untested
// surface, and it will be the one the operator actually uses." This file names no
// NinjaTrader type, so `tests/BridgeTests.csproj` compiles and EXECUTES it, the same trade
// as BridgeAccountResolver.cs and CopierEnforcementView.cs.
//
// ⚠️ THE ORDERING IS THE WHOLE POINT, AND TWO INCOMING SCALES DISAGREE ABOUT WHICH END IS
// BAD. Measured on the live box, one payload, both at once:
//
//     "rows": [ { "verdict": "Idle", "severity": 5 } ]      <- 0 is WORST (CopierSnapshotJson)
//     "system": { "severity": "warn" }                      <- Ok=0 .. Critical=3, 3 is WORST
//
// A tree that sorted both by "the severity number" would put a healthy Idle row (5) last --
// correct by accident -- and a `critical` system cell (3) in the middle, below `Shadow` (3)
// which is not a fault at all. The core already carries this warning for its own half:
// CopierSnapshotJson.SeverityRank exists because casting `CopierConformance` would sort an
// ORPHAN -- a follower holding a live position nobody manages -- into the middle. So this
// file converts BOTH scales into ONE rank, once, and everything downstream sorts by that.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// One relationship as `/api/copier/snapshot` delivers it, reduced to what the tree needs.
    /// Primitives only -- see the file header for why this class may not name an NT8 type.
    /// </summary>
    public sealed class FleetCopierRow
    {
        public string LeaderAccountName;
        public string FollowerAccountName;
        public string GroupName;

        /// <summary>
        /// Null on both live rows today, and that is exactly why it is here: a leader and a
        /// follower may hold more than one relationship, one per instrument, and a tree built
        /// per ROW would then list that account twice. Carried so the case is representable in
        /// a test rather than only in production.
        /// </summary>
        public string InstrumentFullName;

        /// <summary>`CopierSnapshotJson.SeverityRank`, in which <b>0 is WORST</b>.</summary>
        public int Severity;

        public bool Enforcing;
        public string NotEnforcingLabel;
    }

    /// <summary>
    /// A node in the fleet tree. A group carries its followers as <see cref="Children"/>;
    /// the single "Unlinked accounts" node carries every account in no relationship.
    /// </summary>
    public sealed class FleetNode
    {
        /// <summary>"group", "account" or "unlinked".</summary>
        public string Kind;
        public string Name;

        /// <summary>"leader", "follower" or null.</summary>
        public string Role;

        /// <summary>The unified rank. <b>0 is WORST</b>, and nothing downstream re-derives it.</summary>
        public int Rank;

        public string Badge;
        public List<FleetNode> Children = new List<FleetNode>();

        /// <summary>
        /// P2-127 follow-up (2026-08-18). True for an account the broker did not deliver on
        /// this login (`Connection == null`) and that has no open position, working order or
        /// live guard finding. The page hides these by default behind a stated count and a
        /// toggle; the flag is DATA, classified at the call site, so this class still names
        /// no NinjaTrader type. Only UNLINKED children carry it -- an account in a copy
        /// relationship is configured to copy, which is the thing the tree exists to show.
        /// </summary>
        public bool Dormant;
    }

    public static class BridgeFleetView
    {
        /// <summary>0 is WORST, everywhere in this file and in everything it produces.</summary>
        public const int WorstRank = 0;

        /// <summary>
        /// The rank an unknown or absent state gets. It is the WORST, deliberately: an
        /// unreadable state must not sort below a healthy one and read as fine. Same
        /// fail-closed direction as `CopierEnforcementView.SeverityName`, whose unknown rank
        /// answers "critical" rather than "ok".
        /// </summary>
        public const int UnknownRank = WorstRank;

        /// <summary>The name of the node holding every account in no relationship.</summary>
        public const string UnlinkedName = "Unlinked accounts";

        /// <summary>
        /// The rank of an account the COPIER scale does not describe at all -- an account in no
        /// relationship. Above every real rank, so it sorts as the least severe thing on the page
        /// and never crowds the top.
        ///
        /// ⚠️ IT IS DELIBERATELY NOT <see cref="UnknownRank"/>, and the distinction is the whole
        /// point: fail-closed is for a state we cannot READ, not for one that does not APPLY.
        /// Measured on the live box, 95 of 97 accounts are in no copier relationship, so ranking
        /// them "worst" paints 95 permanent red rows -- an alarm that is always on, which this
        /// system has now produced seven times by other routes. Ranking them "ok" is the opposite
        /// lie. So the renderer gets a value it can show as neutral and colour as neither.
        ///
        /// ⚠️ TEMPORARY, AND THE NEXT SLICE OF P2-127 REPLACES IT. An unlinked account still has a
        /// GUARD state, which is the thing the operator wants for it; this rank is what stands in
        /// until that is folded in. A test pins it so that change has to be deliberate.
        /// </summary>
        public const int NotApplicableRank = 6;

        /// <summary>
        /// P2-138. The badge section 4's rows show: `follower_1 1.0x ✔MATCH`.
        ///
        /// A refusing row's badge is the label it ALREADY CARRIES. CopierEnforcementView folds
        /// that label out of the same relationships this tree is built from and ranks the reasons
        /// by what BINDS, so reusing it keeps one vocabulary; inventing a second wording here
        /// would drift from the sentence the row's own tooltip shows.
        ///
        /// Only the enforcing case needs a word of its own, because an enforcing row has no
        /// refusal to name. It is a NAME and not a number: the page colours off it, and a numeric
        /// severity crossing the wire is what this file's header warns about at length.
        /// </summary>
        public const string EnforcingBadge = "MATCH";

        /// <summary>The badge for one relationship row. See <see cref="EnforcingBadge"/>.</summary>
        public static string BadgeForRow(FleetCopierRow row)
        {
            if (row == null) return null;
            return row.Enforcing ? EnforcingBadge : row.NotEnforcingLabel;
        }

        /// <summary>
        /// A copier row's severity, converted to the unified rank. `CopierSnapshotJson`
        /// already uses 0-is-worst, so this is near-identity -- it exists so the conversion
        /// is NAMED and so an out-of-range value is clamped rather than sorted.
        /// </summary>
        public static int RankOfCopierRow(int copierSeverity)
        {
            // CopierSnapshotJson.SeverityRank is already 0-is-worst, so valid values keep their meaning.
            if (copierSeverity >= 0 && copierSeverity <= 5)
                return copierSeverity;

            // An unreadable rank must sort below every real state so it cannot look healthy.
            return UnknownRank;
        }

        /// <summary>
        /// The system cell's severity NAME, converted to the unified rank. This scale runs
        /// the OTHER WAY (`CopierStatusSeverity`: Ok=0, Info=1, Warn=2, Critical=3), so this
        /// method inverts. An unrecognised name is <see cref="UnknownRank"/>.
        /// </summary>
        public static int RankOfSystemSeverity(string severityName)
        {
            if (string.IsNullOrWhiteSpace(severityName))
                return UnknownRank;

            // The system scale runs best-to-worst 0..3; invert it so the page still sorts 0-is-worst.
            switch (severityName.Trim().ToLowerInvariant())
            {
                case "ok":       return 3;          // best system state -> lowest page priority
                case "info":     return 2;
                case "warn":     return 1;
                case "critical": return WorstRank;  // worst system state -> top of page
                default:         return UnknownRank;
            }
        }

        /// <summary>
        /// The worst of a set of child ranks -- i.e. the SMALLEST, because 0 is worst.
        ///
        /// ⚠️ An EMPTY set is <see cref="UnknownRank"/>, not the best rank. A group with a
        /// leader and no followers copies nothing, and §4.2's reason for killing tabs is that
        /// this page's value is that a bad state is visible without being looked for. An
        /// empty group that sorted to the bottom as healthy is that hazard exactly.
        /// </summary>
        public static int WorstOf(IEnumerable<int> ranks)
        {
            // On this page 0 is worst, so the smallest value is the worst.
            if (ranks == null)
                return UnknownRank;

            int worst = int.MaxValue;
            bool any = false;
            foreach (int rank in ranks)
            {
                if (!any || rank < worst)
                    worst = rank;
                any = true;
            }

            // No ranks means no evaluable state; fail closed rather than returning a clean 0.
            return any ? worst : UnknownRank;
        }

        /// <summary>
        /// The fleet tree: group nodes worst-first, each with its followers nested worst-first,
        /// then the single "Unlinked accounts" node last, its children worst-first.
        ///
        /// Grouping follows §4 decision 2 -- groups are the only grouping, and a 1:1 pair is a
        /// group of one -- so a row with no `GroupName` is keyed by its LEADER. `P1-76` made a
        /// follower belong to a direct relationship OR a group and never both.
        ///
        /// Every account in <paramref name="allAccounts"/> that appears in no row goes under
        /// "Unlinked accounts", and NO account appears in two places: a leader is its group's
        /// leader and is not also unlinked.
        /// </summary>
        /// <summary>
        /// Worst rank first, then by name. The name half is not cosmetic: `List&lt;T&gt;.Sort` is
        /// documented as UNSTABLE, and equal ranks are the normal case here, not the exception.
        /// </summary>
        private static int CompareNodes(FleetNode a, FleetNode b)
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0) return byRank;
            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
        }

        public static List<FleetNode> Build(IList<FleetCopierRow> rows, IList<string> allAccounts)
        {
            return Build(rows, allAccounts, null);
        }

        /// <summary>
        /// P2-127 follow-up (2026-08-18). The dormant-account filter.
        ///
        /// <paramref name="dormantAccounts"/> is the set of account names the CALLER has
        /// classified as dormant -- measured as `Connection == null` with no open position,
        /// no working order and no live guard finding. The classification stays at the call
        /// site because it reads `NinjaTrader.Cbi.Account.Connection`, and this class is
        /// testable precisely because it names no NinjaTrader type. Passing the RESULT in as
        /// data keeps that property.
        ///
        /// ⚠️ THE INVARIABLE: an account with an open position, a working order or a live
        /// guard finding is NEVER dormant, whatever the caller's filter setting. The caller
        /// is trusted to classify; this method only applies the flag to UNLINKED children,
        /// so an account in a copy relationship is never hidden by this filter either.
        /// </summary>
        public static List<FleetNode> Build(IList<FleetCopierRow> rows, IList<string> allAccounts,
                                           ISet<string> dormantAccounts)
        {
            // Treat null inputs as empty so the tree is always well-formed.
            IList<FleetCopierRow> safeRows = rows ?? new List<FleetCopierRow>();
            IList<string> safeAccounts = allAccounts ?? new List<string>();
            ISet<string> dormant = dormantAccounts ?? new HashSet<string>();

            Dictionary<string, List<FleetCopierRow>> groups = new Dictionary<string, List<FleetCopierRow>>();
            HashSet<string> linked = new HashSet<string>();

            foreach (FleetCopierRow row in safeRows)
            {
                // Group by name when one exists; otherwise the leader account is the group of one.
                string key = string.IsNullOrWhiteSpace(row.GroupName)
                    ? row.LeaderAccountName
                    : row.GroupName;

                if (!string.IsNullOrEmpty(row.LeaderAccountName))
                    linked.Add(row.LeaderAccountName);
                if (!string.IsNullOrEmpty(row.FollowerAccountName))
                    linked.Add(row.FollowerAccountName);

                // A row with no group and no leader cannot form a group, but its follower is still linked.
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                linked.Add(key);

                if (!groups.TryGetValue(key, out List<FleetCopierRow> bucket))
                {
                    bucket = new List<FleetCopierRow>();
                    groups[key] = bucket;
                }
                bucket.Add(row);
            }

            List<FleetNode> groupNodes = new List<FleetNode>();
            foreach (KeyValuePair<string, List<FleetCopierRow>> kvp in groups)
            {
                // ONE node per follower, not one per ROW. A leader and a follower can hold more
                // than one relationship -- FleetCopierRow carries InstrumentFullName precisely so
                // a per-instrument relationship can exist -- and rendering the same account twice
                // in one group breaks "no account appears twice" without any test noticing,
                // because both live rows on the box today are instrument-less.
                // The account keeps its WORST rank across those rows, for the same reason a group
                // keeps its worst child: the reassuring one must not be the one displayed.
                List<FleetNode> children = new List<FleetNode>();
                Dictionary<string, FleetNode> byFollower = new Dictionary<string, FleetNode>();
                // P2-138. Whether the badge currently displayed came from a REFUSING row, tracked
                // as the fact rather than re-read from the badge string. The first draft asked
                // `existing.Badge != "MATCH"`, which decides "is this a refusal?" by comparing a
                // DISPLAY string to a literal -- so the day an enforcing row gains a second badge
                // wording, or a refusal label happens to read "MATCH", the merge silently inverts.
                // The row already carries the answer. [[a-second-reader-of-the-same-state]].
                Dictionary<string, bool> followerIsRefusing = new Dictionary<string, bool>();
                List<int> childRanks = new List<int>();
                foreach (FleetCopierRow row in kvp.Value)
                {
                    int rank = RankOfCopierRow(row.Severity);
                    childRanks.Add(rank);

                    string key = row.FollowerAccountName ?? "";
                    string newBadge = BadgeForRow(row);
                    bool newIsRefusal = !row.Enforcing;

                    FleetNode existing;
                    if (byFollower.TryGetValue(key, out existing))
                    {
                        bool existingIsRefusal;
                        followerIsRefusing.TryGetValue(key, out existingIsRefusal);

                        // The worse rank wins the badge, EXCEPT that a refusal already seen is
                        // never displaced by an enforcing row: "this follower is refusing on one
                        // of its instruments" is the fact the operator needs, and a reassuring
                        // badge beside a worse rank is the reassuring-one-displayed defect the
                        // rank merge two lines up exists to prevent.
                        bool take = rank < existing.Rank
                            ? (newIsRefusal || !existingIsRefusal)
                            // At an equal rank the order rows arrive in must not decide what is
                            // shown, or the same data renders differently on two polls.
                            : rank == existing.Rank
                                && ((newIsRefusal && !existingIsRefusal)
                                    || string.IsNullOrEmpty(existing.Badge));

                        if (rank < existing.Rank) existing.Rank = rank;
                        if (take)
                        {
                            existing.Badge = newBadge;
                            followerIsRefusing[key] = newIsRefusal;
                        }
                        continue;
                    }

                    FleetNode child = new FleetNode
                    {
                        Kind = "account",
                        Name = row.FollowerAccountName,
                        Role = "follower",
                        Rank = rank,
                        Badge = newBadge,
                        Children = new List<FleetNode>()
                    };
                    byFollower[key] = child;
                    followerIsRefusing[key] = newIsRefusal;
                    children.Add(child);
                }

                // Worst-first within the group, then by NAME. The name is not decoration:
                // List<T>.Sort is documented UNSTABLE, so without a total order two equally
                // ranked rows swap places between refreshes of a page that polls.
                children.Sort(CompareNodes);

                groupNodes.Add(new FleetNode
                {
                    Kind = "group",
                    Name = kvp.Key,
                    Children = children,
                    Rank = WorstOf(childRanks)
                });
            }

            // Worst-first among groups; unlinked node is appended afterwards so it is always last.
            // The name tie-break matters more here than anywhere: `groups` is a Dictionary, whose
            // enumeration order is explicitly unspecified, so an unstable sort over equal ranks
            // would order the fleet differently on runs that saw identical data.
            groupNodes.Sort(CompareNodes);

            List<FleetNode> unlinkedChildren = new List<FleetNode>();
            List<int> unlinkedRanks = new List<int>();
            foreach (string account in safeAccounts)
            {
                if (!linked.Contains(account))
                {
                    // No copier row, so the copier scale does not APPLY -- which is not the same
                    // as unreadable. See NotApplicableRank for why fail-closed is the wrong
                    // instinct here and what replaces this in the next slice.
                    unlinkedChildren.Add(new FleetNode
                    {
                        Kind = "account",
                        Name = account,
                        Rank = NotApplicableRank,
                        Children = new List<FleetNode>(),
                        // P2-127 follow-up. The caller classified this account as dormant
                        // (Connection == null, no position, no order, no guard finding). The
                        // page hides dormant accounts by default behind a stated count.
                        Dormant = dormant.Contains(account)
                    });
                    unlinkedRanks.Add(NotApplicableRank);
                }
            }

            // Every unlinked child currently ties, which is exactly when an unstable sort shows:
            // 95 accounts on this box, ordered arbitrarily and re-ordered on refresh. The name is
            // what makes the list the same list twice.
            unlinkedChildren.Sort(CompareNodes);

            FleetNode unlinkedNode = new FleetNode
            {
                Kind = "unlinked",
                Name = UnlinkedName,
                Children = unlinkedChildren,
                Rank = WorstOf(unlinkedRanks)
            };

            groupNodes.Add(unlinkedNode);
            return groupNodes;
        }

        /// <summary>
        /// P2-138. The rows of `/api/copier/snapshot` as JSON, reduced to <see cref="FleetCopierRow"/>.
        ///
        /// ⚠️ THIS EXISTS SO THE FIELD NAMES ARE EXECUTED RATHER THAN TYPED TWICE. `Build` above is
        /// fully tested and had NO CALLER outside its own test file for a day -- the tree was
        /// correct and nothing served it. The obvious repair is to map the JSON inline in
        /// `McpBridgeAddOn.GetCopierSnapshot`, which is the one bridge source `BridgeTests.csproj`
        /// cannot compile, so a mistyped field name would deserialise to null/0 and the tree would
        /// render a healthy-looking lie with every test still green.
        ///
        /// The test pins this against `tests/fixtures/copier_snapshot_live_20260817.json`, captured
        /// from the deployed box rather than written from memory, so a field RENAME in core breaks
        /// a test instead of blanking a column.
        ///
        /// Takes JToken, not a typed DTO, because the route has already parsed and ENRICHED this
        /// array -- `enforcing` and `notEnforcingLabel` are added by the bridge after core
        /// serialises it, and re-reading the engine to get them back would be the second
        /// derivation this whole file exists to avoid.
        /// </summary>
        public static List<FleetCopierRow> RowsFromSnapshot(JToken rows)
        {
            List<FleetCopierRow> result = new List<FleetCopierRow>();
            if (rows == null)
                return result;

            foreach (JToken element in rows)
            {
                if (element == null || element.Type == JTokenType.Null)
                    continue;

                JObject row = element as JObject;
                if (row == null)
                    continue;

                string leader = row["leaderAccountName"]?.ToString();
                string follower = row["followerAccountName"]?.ToString();
                if (string.IsNullOrWhiteSpace(leader) || string.IsNullOrWhiteSpace(follower))
                    continue;

                JToken severityToken = row["severity"];
                if (severityToken == null || severityToken.Type == JTokenType.Null)
                    continue;

                JToken enforcingToken = row["enforcing"];
                if (enforcingToken == null || enforcingToken.Type == JTokenType.Null)
                    continue;

                result.Add(new FleetCopierRow
                {
                    LeaderAccountName = leader,
                    FollowerAccountName = follower,
                    GroupName = row["groupName"]?.ToString(),
                    InstrumentFullName = row["instrumentFullName"]?.ToString(),
                    Severity = severityToken.Value<int>(),
                    Enforcing = enforcingToken.Value<bool>(),
                    NotEnforcingLabel = row["notEnforcingLabel"]?.ToString()
                });
            }

            return result;
        }
    }
}
