// P1-106. A lockout must stop you OPENING risk. It must never stop you CLOSING it.
//
// All three bridge order paths were:
//
//     if (IsAccountLocked(account.Name))
//         return new { error = "Order blocked: Account " + name + " is locked out." };
//
// which does not care what the order DOES. Measured during P0-104's reproduction: Sim101 long 11,
// locked by the panic switch, and a Sell was refused -- the lockout trapped the operator in the
// exact risk it exists to limit. That is the half of P0-104 its fix deliberately left, and it is
// what turned "the flatten failed" into "and you cannot fix it by hand".
//
// The guard has had this notion since P1-44: its entry-cancel block is gated by
// `IsPositionReducingOrder`, precisely so a rate limit can never cancel a protective order and
// leave a position naked. The same reasoning one level up.
//
// ⚠️ READ THE POSITION, NEVER THE ORDER'S LABEL. `OrderAction` is chosen by whoever submits the
// order, so `Sell` does not mean "exit" -- that is P1-97 verbatim, where the bridge's own
// `nt_place_order` emitted `Sell` for a short ENTRY and `Buy` for a cover, and the copier read
// both backwards. This file takes the request's direction (buy/sell, a fact about which way the
// order trades) and the account's real position, and asks whether the two oppose.
//
// ⚠️ THE QUANTITY CLAMP IS LOAD-BEARING, and it is the whole reason this is not a one-line
// predicate. A `Sell 20` against a long 11 is an exit AND a new short 9. NT8 nets it into one
// order, so admitting it under a lockout opens 9 contracts of fresh risk on an account that is
// locked out. Strictly reducing means quantity <= |position|.
//
// ⚠️ BRACKETED ORDERS STAY REFUSED, and this is a deliberate asymmetry rather than an omission.
// `PlaceOcoOrder` and `PlaceAtmOrder` submit an entry plus stop and target legs, and those legs
// take the OPPOSITE side -- so an OCO whose entry happens to flatten a long leaves a resting stop
// and target that OPEN a short when either triggers. The bracket cannot be admitted on the
// strength of its entry alone. `nt_close_position` is not lockout-gated at all, so a market exit
// is always available; this file is what makes a LIMIT exit possible too.
//
// ⚠️ NT8's `Position.Quantity` is ABSOLUTE -- the side is `MarketPosition`, and reading the SIGN
// of the quantity is P0-96, where the copier answered a leader's short-cover with a Sell and
// DOUBLED the follower's short behind 1311 green tests. This takes the side as a string and the
// quantity as a magnitude, so there is no sign here to misread.
//
// WHY ITS OWN FILE: `McpBridgeAddOn.cs` is in no test build (P2-27), so anything inside it is
// pinnable only by source-text regex. This names no NinjaTrader type, so
// `tests/BridgeTests.csproj` compiles and EXECUTES it. Same trade as `BridgeAccountResolver.cs`,
// `BridgeOrderAction.cs` and `BridgeFlattenPlan.cs`.
//
// ⚠️ And extraction moves the untested boundary, it does not remove it (P0-104's surviving
// mutant): the CALLER still has to pass the right position, and no test in here can see that it
// does. `check_lockout_gate_wired.py` pins the three call sites by source text.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>What a lockout should do with one order request.</summary>
    public sealed class LockoutDecision
    {
        /// <summary>True when the order may be submitted.</summary>
        public bool Allowed;

        /// <summary>True when it is allowed ONLY because it strictly reduces an open position.</summary>
        public bool AllowedAsReducing;

        /// <summary>Operator-facing text. Empty when the account is not locked at all.</summary>
        public string Reason = string.Empty;
    }

    public static class BridgeLockoutGate
    {
        public const string Long = "Long";
        public const string Short = "Short";

        /// <summary>
        /// Decide whether a locked-out account may submit this order.
        ///
        /// <paramref name="accountLocked"/> is the enforcer's answer, unmodified -- this never
        /// re-derives a lockout, because a second reader of the same state that computes its own
        /// answer is P1-100.
        ///
        /// <paramref name="isBuy"/> is the direction the order TRADES, taken from the request,
        /// not an `OrderAction` label. <paramref name="positionSide"/> is "Long", "Short", or
        /// anything else (including null) meaning flat, and <paramref name="positionQuantity"/>
        /// is its magnitude -- NT8's `Position.Quantity` is already absolute.
        ///
        /// <paramref name="carriesBracket"/> marks an OCO or ATM request, whose stop and target
        /// legs take the opposite side and would OPEN a position after the entry closed one.
        /// </summary>
        public static LockoutDecision Evaluate(
            bool accountLocked,
            bool isBuy,
            string positionSide,
            int positionQuantity,
            int orderQuantity,
            bool carriesBracket,
            string accountName)
        {
            if (!accountLocked)
                return new LockoutDecision { Allowed = true };

            string who = "Account " + (accountName ?? "(unnamed)") + " is locked out";

            if (carriesBracket)
                return new LockoutDecision
                {
                    Allowed = false,
                    Reason = "Order blocked: " + who + ". A bracketed order is refused even when its "
                           + "entry would reduce the position, because its stop and target legs take "
                           + "the opposite side and would OPEN a position once the entry closed one. "
                           + "Use a plain order to exit, or nt_close_position to flatten."
                };

            bool isLong = string.Equals(positionSide, Long, StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(positionSide, Short, StringComparison.OrdinalIgnoreCase);

            if (!isLong && !isShort)
                return new LockoutDecision
                {
                    Allowed = false,
                    Reason = "Order blocked: " + who + " and the account is FLAT in this instrument, "
                           + "so this order can only open risk."
                };

            bool opposes = (isLong && !isBuy) || (isShort && isBuy);
            if (!opposes)
                return new LockoutDecision
                {
                    Allowed = false,
                    Reason = "Order blocked: " + who + ". This order ADDS to an existing "
                           + positionSide.ToLowerInvariant() + " position of "
                           + Math.Abs(positionQuantity) + "."
                };

            int held = Math.Abs(positionQuantity);
            if (orderQuantity > held)
                return new LockoutDecision
                {
                    Allowed = false,
                    Reason = "Order blocked: " + who + ". " + orderQuantity + " against a "
                           + positionSide.ToLowerInvariant() + " " + held + " is an exit AND a new "
                           + (orderQuantity - held) + " the other way, which opens risk on a locked "
                           + "account. Resubmit for " + held + " or fewer."
                };

            return new LockoutDecision
            {
                Allowed = true,
                AllowedAsReducing = true,
                Reason = "Admitted under lockout: reduces " + positionSide.ToLowerInvariant() + " "
                       + held + " by " + orderQuantity + ". A lockout stops you opening risk, "
                       + "never closing it."
            };
        }
    }
}
