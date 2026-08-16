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
        /// A copier row's severity, converted to the unified rank. `CopierSnapshotJson`
        /// already uses 0-is-worst, so this is near-identity -- it exists so the conversion
        /// is NAMED and so an out-of-range value is clamped rather than sorted.
        /// </summary>
        public static int RankOfCopierRow(int copierSeverity)
        {
            return -1;   // NOT IMPLEMENTED (P2-127)
        }

        /// <summary>
        /// The system cell's severity NAME, converted to the unified rank. This scale runs
        /// the OTHER WAY (`CopierStatusSeverity`: Ok=0, Info=1, Warn=2, Critical=3), so this
        /// method inverts. An unrecognised name is <see cref="UnknownRank"/>.
        /// </summary>
        public static int RankOfSystemSeverity(string severityName)
        {
            return -1;   // NOT IMPLEMENTED (P2-127)
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
            return -1;   // NOT IMPLEMENTED (P2-127)
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
        public static List<FleetNode> Build(IList<FleetCopierRow> rows, IList<string> allAccounts)
        {
            return new List<FleetNode>();   // NOT IMPLEMENTED (P2-127)
        }
    }
}
