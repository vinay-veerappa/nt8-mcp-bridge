// P1-131. "If I sever this connection now, is there anything at the broker I will no longer be
// able to manage?"
//
// ⚠️ READ THIS BEFORE REPLACING IT WITH THE CORE'S PREDICATE. That was the first instinct and it
// is wrong. `RiskGuardAddOn.OccupiesSlot` answers a DIFFERENT question -- its own doc comment says
// so: "Is there already an order here, so I must not create a second one?" It excludes `Departing`
// deliberately, because once a cancel is in flight you should be free to place the replacement
// leg. Borrowing it here would discard exactly the orders that matter most: three orders measured
// on this box had been CancelPending for about FIVE HOURS, which is the strongest possible case of
// something at the broker that this process can no longer do anything about.
//
// The core carries THREE predicates over one enum -- OccupiesSlot, ProvidesCoverage,
// AcceptsModification -- because there are three questions, and the third's comment opens "The
// third question, added 2026-08-10 after a live trade". This is the FOURTH.
//
// WHAT WAS THERE BEFORE, and why it was wrong. `McpBridgeAddOn.OccupiesSlotForBridge` was a
// hand-written list of seven states. It omitted six non-terminal ones -- Initialized,
// AcceptedByRisk, ChangeSubmitted, CancelSubmitted, Suspended, Unknown -- and EVERY omission is in
// the direction that PERMITS a disconnect, because `BridgeConnectionPlan.WouldStrand` refuses one
// only when the count is above zero. Two twin-state asymmetries are the tell that it was written
// by remembering states rather than deriving them: `ChangePending` was in the list and
// `ChangeSubmitted` was not; `CancelPending` was in and `CancelSubmitted` was not. Each handshake
// has two halves and one half of each was recalled.
//
// ⚠️ AND THE NAME IS WHAT HID IT. `OccupiesSlotForBridge` reads as "the core's predicate, over
// here", so nobody asked which question it answered. NAME A PREDICATE AFTER ITS QUESTION.
//
// It takes the state NAME rather than `OrderState`, because naming an NT8 type would put this file
// outside `tests/BridgeTests.csproj` and back into the untestable half of the addon (P2-27). The
// call sites pass `order.OrderState.ToString()`.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeOrderLiveness
    {
        /// <summary>
        /// The three states in which the broker is done with an order and nothing more can happen
        /// to it. Everything else -- including states this build has never heard of -- is
        /// something the broker may still hold.
        ///
        /// ⚠️ `Rejected` belongs here and was the one `GetOrders` forgot, which is why a rejected
        /// order was served by an endpoint advertising "active/working orders".
        /// </summary>
        public static bool IsTerminal(string orderStateName)
        {
            if (string.IsNullOrWhiteSpace(orderStateName)) return false;
            switch (orderStateName.Trim())
            {
                case "Filled":
                case "Cancelled":
                case "Rejected":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// "Would severing the connection strand this order?" True for anything not terminal.
        ///
        /// ⚠️ THE DEFAULT IS THE SAFETY PROPERTY, not a convenience. An unrecognised state name --
        /// a state a future NT8 adds, or a null -- answers TRUE, so the disconnect is assessed as
        /// if something is out there. The two directions do not cost the same: a false YES costs a
        /// refused disconnect the operator can override with `confirmDisruptive`, and a false NO
        /// costs a protective stop left at a broker this process can no longer reach.
        /// </summary>
        public static bool WouldBeStrandedByDisconnect(string orderStateName)
        {
            return !IsTerminal(orderStateName);
        }
    }
}
