// F-17. The two decisions behind connection visibility and control, kept where a test can
// execute them.
//
// WHY THIS EXISTS AT ALL. `P2-115` gave `/api/health` a `feedConnected` that can finally be
// false -- but a caller reading `false` had no way to ask WHY, and no way to act on it. Worse,
// the NEGATIVE half of `P2-115` could not be validated: nothing on this box could disconnect a
// connection, so `feedConnected: false` was a state the code could produce and nothing had ever
// observed. A tool that can disconnect is what turns that into a measurement.
//
// ⚠️ DISCONNECTING IS DESTRUCTIVE ON A TRADING PLATFORM, and it is `P1-106`'s family exactly:
// a control that severs the path by which you would fix what it just broke. A position held on a
// disconnected connection cannot be closed, and a resting stop cannot be cancelled or relied on.
// So the default is REFUSAL when anything is live, with the reason naming what.
//
// ⚠️ AND IT NAMES NO NinjaTrader TYPE. `McpBridgeAddOn.cs` is in no test build (`P2-27`), so
// anything that lives there is verifiable only by source gate and `nt_compile`. Every class in
// this repo that a test actually EXECUTES -- BridgeAccountResolver, BridgeFlattenPlan,
// BridgeClosePlan, BridgeAccountScope, BridgeOrderQuery, BridgeLockoutGate, BridgeFeedStatus --
// is built this way for that reason, and this is the seventh.
using System;
using System.Collections.Generic;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeConnectionPlan
    {
        /// <summary>
        /// `P1-90`'s rule at a new surface: resolve the caller's name against what actually
        /// exists, or REFUSE naming the real ones. Never guess, and never fall through to "all".
        ///
        /// ⚠️ A BLANK NAME IS NOT A WILDCARD. On a read that would merely be sloppy; on a path
        /// that disconnects it means *sever every connection on the box*. `P1-105` shipped the
        /// same shape on a close path, where `symbol: "M"` matched MNQ, MES, MCL and MGC, and a
        /// blank symbol very nearly meant liquidate the account. The failure directions are not
        /// symmetric, so the wildcard is simply not offered.
        /// </summary>
        public static bool TryResolve(string requested, string[] available,
                                      out string resolved, out string refusal)
        {
            resolved = null;
            refusal = null;

            if (string.IsNullOrWhiteSpace(requested))
            {
                refusal = "no connection was named. This is deliberately NOT a wildcard: on a "
                        + "disconnect that would sever every connection on the platform. "
                        + Available(available);
                return false;
            }

            if (available != null)
            {
                for (int i = 0; i < available.Length; i++)
                {
                    if (available[i] == null) continue;
                    if (string.Equals(available[i], requested.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        // The CANONICAL spelling, not the caller's. This string is handed to the
                        // platform and printed in the audit line, and `BridgeAccountResolver`
                        // established the same rule for account names.
                        resolved = available[i];
                        return true;
                    }
                }
            }

            refusal = "no connection named '" + requested + "' exists on this platform. "
                    + Available(available);
            return false;
        }

        /// <summary>
        /// Would disconnecting strand something the operator can no longer manage?
        ///
        /// Both halves matter and they are reported separately. A POSITION is the obvious one. A
        /// WORKING ORDER is the one that gets forgotten: a resting stop is protection that still
        /// exists at the broker after the connection drops, and the operator can neither move it
        /// nor cancel it. `P3-110` and `P0-9` are both about protective orders nobody was
        /// watching; this refuses to create that state on purpose.
        /// </summary>
        public static bool WouldStrand(int openPositions, int workingOrders, out string reason)
        {
            reason = null;
            var parts = new List<string>();

            if (openPositions > 0)
                parts.Add(openPositions + " open position(s), which cannot be closed while the "
                        + "connection is down");
            if (workingOrders > 0)
                parts.Add(workingOrders + " working order(s), which stay live at the broker and "
                        + "can be neither moved nor cancelled from here");

            if (parts.Count == 0)
                return false;

            reason = "disconnecting would strand " + string.Join(" and ", parts.ToArray())
                   + ". Pass confirmDisruptive: true if that is genuinely what you want.";
            return true;
        }

        /// <summary>
        /// ⚠️ DEDUPLICATES, and that is not cosmetic. Found by driving the live tool rather than
        /// by any test: the refusal read `Available: Playback, TPT, TPT.` because the route feeds
        /// this list from a PROVIDER-grained array -- one entry per provider on a connection,
        /// which is correct for the feed predicate and wrong for a list of connections. A caller
        /// reading a name twice reasonably concludes there are two connections sharing it, which
        /// is precisely the confusion F-17 exists to remove.
        ///
        /// Fixed at the call site too. It is deduplicated HERE as well because a refusal is read
        /// by a human, and this class cannot know what grain its caller built.
        /// </summary>
        private static string Available(string[] available)
        {
            if (available == null || available.Length == 0)
                return "No connections are configured.";

            var seen = new List<string>();
            for (int i = 0; i < available.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(available[i])) continue;
                bool dup = false;
                for (int k = 0; k < seen.Count; k++)
                    if (string.Equals(seen[k], available[i], StringComparison.OrdinalIgnoreCase))
                    { dup = true; break; }
                if (!dup) seen.Add(available[i]);
            }
            if (seen.Count == 0) return "No connections are configured.";

            var sb = new StringBuilder("Available: ");
            for (int i = 0; i < seen.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(seen[i]);
            }
            sb.Append('.');
            return sb.ToString();
        }
    }
}
