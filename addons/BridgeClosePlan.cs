// P1-105. WHICH positions a close request is about -- asked once, by both halves of the call.
//
// ⚠️ `nt_close_position` REPORTED `positionClosed: true` HAVING SUBMITTED NOTHING.
//
// `ClosePosition` assigned `positionClosed = true` on the line after `account.Flatten(...)`.
// `Flatten` is asynchronous: it returns having *submitted* a close order, and the assignment
// therefore recorded that the method reached that line. Nothing in the handler had ever looked
// at an order or at a position afterwards, so the field could not distinguish "flattened" from
// "called Flatten and nothing happened".
//
// Measured on Sim101 2026-08-14 13:46:33Z, account long 11 MNQ, no lockout, guard in shadow:
//
//     request   {"account": "Sim101", "symbol": "MNQ 09-26"}
//     response  {"status": "flattened", "positionClosed": true, "cancelledOrdersCount": 0}
//
// `interventions.jsonl` records the HTTP call and then NO `ORDER_UPDATE` for Sim101 at all --
// the account logs every order transition and the audit window covers both sides -- so nothing
// reached the book. The position was still long 11. A plain `nt_place_order` Sell 11 closed it
// at once and the copier mirrored the exit, so the account and the path were both working.
//
// THE SECOND-READER SHAPE, AGAIN. `EmergencyFlatten` learned all of this as `P0-104` and got
// `BridgeFlattenPlan` plus a settle poll. `ClosePosition` -- the other of the two `.Flatten(`
// call sites in this file -- was never told, exactly as `IsAccountLocked` was never told what
// `CanTrade` had learned (`P1-100`). Two paths that close a position must answer "did it close?"
// the same way, so this handler now reuses `BridgeFlattenPlan.SubmittedByThisCall` and the same
// bounded re-read rather than growing a second dialect.
//
// WHAT THIS FILE ADDS that the panic path does not need: a SCOPE. `EmergencyFlatten` takes an
// account and closes everything on it. `ClosePosition` takes an account *and a symbol*, so the
// acting pass and the observing pass each have to decide which positions the request was about
// -- and if they decide it differently the report is true about a set the caller never named,
// which is `F-9` restated (derive what you display from what actually happened, over the same
// subject). One predicate, both passes, and a source gate that says so.
//
// TWO DELIBERATE CHOICES:
//
//   * ROOT EQUALITY, NOT `StartsWith`. The old filter was
//     `o.Instrument.FullName.StartsWith(rootSymbol)`, so `symbol: "M"` was a request to flatten
//     MNQ, MES, MCL and MGC together. I could not name a colliding pair of real roots on this
//     box and am not claiming one; the point is that a prefix test on a path that CLOSES
//     POSITIONS is unbounded by construction, and comparing roots costs nothing. Both sides go
//     through `RootOf`, so "MNQ 09-26" and "MNQ" are the same request.
//
//   * THE EXPIRY IS STILL NOT PART OF THE MATCH, on purpose. `symbol: "MNQ 09-26"` closes an
//     `MNQ 12-26` position too, as it always has. Tightening that needs `Instrument.FullName`'s
//     exact spelling on this platform, which I have not measured, and a wrong guess would make
//     a working close silently match nothing -- this defect again, in a new place. Recorded as
//     a known limit rather than fixed blind.
//
// WHY ITS OWN FILE: `McpBridgeAddOn.cs` is in no test build (`P2-27`), so anything inside it is
// pinnable only by source-text regex. This names no NinjaTrader type -- it is string comparison
// -- so `tests/BridgeTests.csproj` compiles and EXECUTES it, the same trade as
// `BridgeFlattenPlan`, `BridgeAccountResolver`, `BridgeLockoutGate`, `CopierEnforcementView`
// and `BridgeOrderAction`. `tools/deploy.py` globs `addons/*.cs`, so it needs no registration.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeClosePlan
    {
        /// <summary>The symbol token meaning "every instrument on the account".</summary>
        public const string EverySymbol = "ALL";

        /// <summary>
        /// The root of an instrument name or a request: the text before the first space, so
        /// "MNQ 09-26" and "MNQ" both give "MNQ". Never null -- an absent symbol has an empty
        /// root, which matches nothing, rather than throwing on a path that closes positions.
        /// </summary>
        public static string RootOf(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
            var trimmed = symbol.Trim();
            int space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed.Substring(0, space);
        }

        /// <summary>
        /// Whether the request asked for every instrument. ONLY the literal "ALL" does.
        /// </summary>
        /// <remarks>
        /// ⚠️ This first read `IsNullOrWhiteSpace(requestedSymbol) -> true`, on the reasoning that
        /// the handler defaults an absent symbol to "ALL" anyway so accepting both could not
        /// drift. A test disagreed with the class in the same commit, and the test was right:
        /// the handler defaults on `IsNullOrEmpty`, so `{"symbol": "   "}` -- a template that
        /// interpolated an empty variable -- reached here as three spaces and would have been
        /// read as a request to CLOSE EVERY POSITION on the account.
        ///
        /// So the wildcard is one exact token and nothing else. A blank, whitespace or null
        /// symbol matches no instrument, the call reports `nothing_to_close`, and the caller
        /// sees their bug. Turning absence into "ALL" happens in exactly one greppable place --
        /// the handler -- rather than in two functions that each think the other is careful.
        /// The failure directions are not symmetric here: matching nothing is a wasted call,
        /// matching everything is an unrequested liquidation.
        /// </remarks>
        public static bool WantsEverySymbol(string requestedSymbol)
        {
            if (requestedSymbol == null) return false;
            return string.Equals(requestedSymbol.Trim(), EverySymbol, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether an instrument is one this request named. Root equality, case-insensitive --
        /// see the header for why this is not a prefix test and why the expiry is not compared.
        /// </summary>
        public static bool MatchesSymbol(string instrumentFullName, string requestedSymbol)
        {
            if (WantsEverySymbol(requestedSymbol)) return true;

            var want = RootOf(requestedSymbol);
            var have = RootOf(instrumentFullName);

            // An instrument with no name cannot be shown to be in scope, and "in doubt" on a
            // closing path means OUT of scope: excluding it leaves a position open and visible
            // in the report, where including it closes something nobody asked about.
            if (want.Length == 0 || have.Length == 0) return false;

            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether an account is one this request named. An empty request means every account
        /// -- the handler's long-standing contract, kept deliberately. ⚠️ The CALLER is expected
        /// to have already refused an account name that resolves to nothing
        /// (`BridgeAccountResolver`, `P1-90`): a typo reaching here matches no account, and
        /// "matched nothing" is a far worse answer than "there is no account called that".
        /// </summary>
        public static bool MatchesAccount(string accountName, string requestedAccount)
        {
            if (string.IsNullOrWhiteSpace(requestedAccount)) return true;
            if (string.IsNullOrWhiteSpace(accountName)) return false;
            return string.Equals(accountName.Trim(), requestedAccount.Trim(),
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The whole scope test: is this (account, instrument) pair one the request was about?
        /// The acting pass and the observing pass both call THIS, not the two halves, so there
        /// is one place for them to agree.
        /// </summary>
        public static bool InScope(string accountName, string instrumentFullName,
                                   string requestedAccount, string requestedSymbol)
        {
            return MatchesAccount(accountName, requestedAccount)
                && MatchesSymbol(instrumentFullName, requestedSymbol);
        }

        /// <summary>
        /// What the handler may claim, given what it observed. Deliberately NOT a rephrasing of
        /// "did we call Flatten": `positionsMatched == 0` is not a close, and a matched position
        /// that is still open after the settle poll is not a close either, however many orders
        /// went out. The old handler returned the constant string "flattened" and a
        /// `positionClosed` that meant "control reached line 2832".
        /// </summary>
        public static string StatusFor(int positionsMatched, int positionsStillOpen, int ordersSubmitted)
        {
            if (positionsMatched == 0) return "nothing_to_close";
            if (positionsStillOpen > 0)
                return ordersSubmitted > 0 ? "close_submitted_not_confirmed" : "close_not_submitted";
            return "flattened";
        }

        /// <summary>
        /// True only when every position this request matched is observed flat. `positionsMatched
        /// == 0` is NOT closed: it is the answer a typo'd symbol produces, and reporting it as a
        /// successful close is how the original defect would come back wearing new fields.
        /// </summary>
        public static bool PositionClosed(int positionsMatched, int positionsStillOpen)
        {
            return positionsMatched > 0 && positionsStillOpen == 0;
        }
    }
}
