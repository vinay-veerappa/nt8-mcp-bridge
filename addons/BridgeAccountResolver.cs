// P1-90. Every order path in McpBridgeAddOn.cs resolved an account like this:
//
//     the named account
//       ?? the account called "Sim101"
//       ?? ANY account not called "Backtest"
//       ?? ANY account at all
//
// So `nt_place_order` with a name that did not resolve -- a typo, wrong case, a
// disconnected account -- was not refused. The order was placed somewhere else. The live
// box reports 96 accounts. P1-85 had already removed exactly this guess from the copier
// engine; the bridge had kept its own copy at six sites.
//
// The fix is refusal: a request that cannot say which account it means has no safe
// interpretation. For a write that means acting on the wrong account; for a read it means
// answering confidently about someone else's.
//
// WHY THIS IS ITS OWN FILE, and not a private method in McpBridgeAddOn.cs:
// that file is 6013 lines and is in no test build (`P2-27`), so anything inside it can be
// pinned only by source-text regex, which is not evidence. This file names no NinjaTrader
// type -- it takes the account names as strings -- so `tests/BridgeTests.csproj` compiles
// and EXECUTES it. That is the difference between asserting the guess is absent from the
// source and asserting the replacement is correct.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>The outcome of resolving a requested account name. Either a canonical
    /// name, or a refusal carrying the reason -- never a substituted account.</summary>
    public sealed class BridgeAccountResolution
    {
        /// <summary>The account's canonical name as the platform spells it, or null when refused.</summary>
        public string Name { get; private set; }

        /// <summary>Operator-facing reason, or null when resolved.</summary>
        public string Error { get; private set; }

        public bool Refused { get { return Error != null; } }

        private BridgeAccountResolution(string name, string error)
        {
            Name = name;
            Error = error;
        }

        internal static BridgeAccountResolution Resolved(string name)
        {
            return new BridgeAccountResolution(name, null);
        }

        internal static BridgeAccountResolution Refuse(string error)
        {
            return new BridgeAccountResolution(null, error);
        }
    }

    public static class BridgeAccountResolver
    {
        /// <summary>
        /// Resolve a requested account name against the names actually available, refusing
        /// rather than substituting. <paramref name="purpose"/> is a verb phrase naming what
        /// was being attempted ("place an order"), used only in the refusal text.
        /// </summary>
        public static BridgeAccountResolution ResolveOrRefuse(
            string requested, IEnumerable<string> availableNames, string purpose)
        {
            // A null list and an empty list are the same answer here, and it is not the
            // caller's mistake -- do not blame the request for the platform having no
            // accounts.
            var available = (availableNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            if (string.IsNullOrEmpty(purpose)) purpose = "act on an account";

            // Trimmed before the emptiness test AND before matching: a trailing space in a
            // JSON field has exactly one possible intent, and treating " " as a named
            // account would resolve to nothing and read as "not found" rather than
            // "you did not say".
            var name = requested == null ? null : requested.Trim();

            if (string.IsNullOrEmpty(name))
            {
                return BridgeAccountResolution.Refuse(string.Format(
                    "This request must name an account: no `account` field was supplied. " +
                    "Refusing to {0} on a guessed account -- {1} account(s) are available, " +
                    "and picking one of them is not a decision this bridge gets to make.",
                    purpose, available.Count));
            }

            if (available.Count == 0)
            {
                return BridgeAccountResolution.Refuse(string.Format(
                    "No accounts are available at all, so '{0}' cannot be resolved. " +
                    "Refusing to {1}. This is a platform/connection state, not a bad request.",
                    name, purpose));
            }

            // An exact match wins outright. This matters before the case-insensitive pass:
            // if two accounts somehow differ only in case, the one the caller spelled
            // exactly is unambiguously the one they meant.
            var exact = available.FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal));
            if (exact != null) return BridgeAccountResolution.Resolved(exact);

            var matches = available
                .Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (matches.Count == 1) return BridgeAccountResolution.Resolved(matches[0]);

            if (matches.Count > 1)
            {
                // Not observed on this box, and cheap enough to be worth refusing anyway:
                // it is the one remaining way this function could pick one account out of
                // several. Returning FirstOrDefault here would be P1-90 again, smaller.
                return BridgeAccountResolution.Refuse(string.Format(
                    "'{0}' matches {1} accounts that differ only in case ({2}). Refusing to " +
                    "{3} until the request says which one exactly.",
                    name, matches.Count, string.Join(", ", matches), purpose));
            }

            return BridgeAccountResolution.Refuse(string.Format(
                "No account named '{0}' (matched case-insensitively) among the {1} available. " +
                "Refusing to {2} rather than choosing a different account. Check the spelling, " +
                "and that the account is connected.",
                name, available.Count, purpose));
        }
    }
}
