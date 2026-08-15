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

            // ⚠️ AMBIGUITY IS A REFUSAL, NOT A PICK. Measured on this box: `TPT` is the name of
            // TWO DISTINCT connections -- one `Simulator` with 5 accounts, one `Provider31` with
            // 1. An earlier version of this method returned the first match, which on a path that
            // connects and disconnects brokers means acting on an arbitrary one of them. That is
            // `P1-90` verbatim: guessing a target instead of refusing.
            //
            // ⚠️ And the first "fix" made it WORSE. The live refusal read
            // `Available: Playback, TPT, TPT.`, I read the repetition as a display artefact of the
            // provider-grained array the route builds for the feed predicate, and deduplicated it
            // -- which left the display tidy and the ambiguity invisible. **A duplicate you cannot
            // explain is evidence, not noise.**
            int matches = 0;
            string first = null;
            if (available != null)
            {
                for (int i = 0; i < available.Length; i++)
                {
                    if (available[i] == null) continue;
                    if (string.Equals(available[i], requested.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        matches++;
                        // The CANONICAL spelling, not the caller's. This string is handed to the
                        // platform and printed in the audit line, and `BridgeAccountResolver`
                        // established the same rule for account names.
                        if (first == null) first = available[i];
                    }
                }
            }

            if (matches > 1)
            {
                refusal = "the name '" + requested + "' is AMBIGUOUS: " + matches + " connections "
                        + "carry it, and they are different objects with different accounts and "
                        + "possibly different statuses. Refusing rather than picking one -- on a "
                        + "connect/disconnect path that would act on an arbitrary broker. Read "
                        + "action 'status' and disambiguate by provider. " + Available(available);
                return false;
            }

            if (matches == 1)
            {
                resolved = first;
                return true;
            }

            refusal = "no connection named '" + requested + "' exists on this platform. "
                    + Available(available);
            return false;
        }

        /// <summary>
        /// Resolves to ONE connection by name plus an optional provider, and returns its INDEX so
        /// the caller can act on the right object rather than on a string.
        ///
        /// ⚠️ THE PROVIDER IS THE DISAMBIGUATOR, and it exists because refusing ambiguity is only
        /// half an answer. Measured on this box, `TPT` is two connections -- a `Simulator` one
        /// with 5 accounts and a `Provider31` one with 1 -- so `TryResolve` correctly refuses the
        /// bare name, and that leaves the operator unable to connect the very broker they meant.
        /// A refusal that cannot be satisfied is a wall, not a gate: it has to say what would work
        /// AND that thing has to exist.
        ///
        /// Provider is optional so the unambiguous case stays a one-word call.
        /// </summary>
        public static bool TryResolveOne(string requestedName, string requestedProvider,
                                         string[] names, string[] providers,
                                         out int index, out string refusal)
        {
            index = -1;
            refusal = null;

            if (string.IsNullOrWhiteSpace(requestedName))
            {
                refusal = "no connection was named. This is deliberately NOT a wildcard: on a "
                        + "disconnect that would sever every connection on the platform. "
                        + Available(names);
                return false;
            }
            if (names == null || names.Length == 0)
            {
                refusal = "No connections are configured.";
                return false;
            }

            var hits = new List<int>();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == null) continue;
                if (!string.Equals(names[i], requestedName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(requestedProvider))
                {
                    string p = providers != null && i < providers.Length ? providers[i] : null;
                    if (p == null || !string.Equals(p, requestedProvider.Trim(),
                                                    StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                hits.Add(i);
            }

            if (hits.Count == 1) { index = hits[0]; return true; }

            if (hits.Count == 0)
            {
                refusal = string.IsNullOrWhiteSpace(requestedProvider)
                    ? "no connection named '" + requestedName + "' exists on this platform. " + Available(names)
                    : "no connection named '" + requestedName + "' with provider '" + requestedProvider
                      + "' exists. " + Available(names);
                return false;
            }

            // Still ambiguous. Name the providers that would separate them -- a refusal has to
            // hand back the thing that satisfies it.
            var sb = new StringBuilder("the name '").Append(requestedName).Append("' is AMBIGUOUS: ")
                .Append(hits.Count).Append(" connections carry it");
            if (!string.IsNullOrWhiteSpace(requestedProvider))
                sb.Append(" even with provider '").Append(requestedProvider).Append('\'');
            sb.Append(". Refusing rather than picking one -- on a connect/disconnect path that "
                    + "would act on an arbitrary broker. Disambiguate with provider: ");
            for (int k = 0; k < hits.Count; k++)
            {
                if (k > 0) sb.Append(", ");
                string p = providers != null && hits[k] < providers.Length ? providers[hits[k]] : null;
                sb.Append(string.IsNullOrEmpty(p) ? "(unknown)" : p);
            }
            sb.Append('.');
            refusal = sb.ToString();
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
        /// Collapses repeats but COUNTS them: `TPT (x2)` rather than either `TPT, TPT` or a bare
        /// `TPT`.
        ///
        /// ⚠️ THIS WENT THROUGH BOTH WRONG ANSWERS FIRST, in one session. The live refusal read
        /// `Available: Playback, TPT, TPT.`; I judged the repeat a display artefact of the
        /// provider-grained array the route builds for the feed predicate, and deduplicated it. A
        /// later live read showed `TPT` is genuinely TWO connections -- one Simulator with 5
        /// accounts, one Provider31 with 1 -- so the repeat was real and deduplicating had hidden
        /// it. Printing it raw was confusing; hiding it was dangerous.
        ///
        /// **A duplicate you cannot explain is evidence, not noise.** Say how many.
        /// </summary>
        private static string Available(string[] available)
        {
            if (available == null || available.Length == 0)
                return "No connections are configured.";

            var seen = new List<string>();
            var times = new List<int>();
            for (int i = 0; i < available.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(available[i])) continue;
                int at = -1;
                for (int k = 0; k < seen.Count; k++)
                    if (string.Equals(seen[k], available[i], StringComparison.OrdinalIgnoreCase))
                    { at = k; break; }
                if (at < 0) { seen.Add(available[i]); times.Add(1); }
                else times[at]++;
            }
            if (seen.Count == 0) return "No connections are configured.";

            var sb = new StringBuilder("Available: ");
            for (int i = 0; i < seen.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(seen[i]);
                if (times[i] > 1) sb.Append(" (x").Append(times[i]).Append(')');
            }
            sb.Append('.');
            return sb.ToString();
        }
    }
}
