// P0-104. Which orders the emergency flatten's SECOND cancel pass may touch.
//
// ⚠️ THE PANIC KILL-SWITCH WAS CANCELLING ITS OWN FLATTEN ORDER.
//
// `EmergencyFlatten` runs five steps per account: terminate strategies, cancel every working
// order, `acc.Flatten(...)`, a second cancel pass "for residual bracket/OCO orders", then engage
// a lockout. `acc.Flatten` is ASYNCHRONOUS -- it SUBMITS a `Close` market order and returns. The
// second pass then enumerated `acc.Orders` for anything active and cancelled all of it, which
// includes the `Close` order step 3 had submitted a moment earlier. It could not tell its own
// flatten from a residual bracket, because it never tried.
//
// Measured on the live box 2026-08-14, Sim101 long 11 MNQ with one resting limit:
//
//     35541  Limit Buy 1    McpBridge  -> Cancelled     <- step 2, correct
//     35542  Market Sell 11 Close      -> Submitted -> CancelPending -> Working -> CANCELLED
//
//     response: {"success": true, "cancelledOrders": 2, "firstPassCancelled": 1,
//                "residualCancelled": 1, "flattenedAccounts": 1, "errors": []}
//     position: STILL LONG 11
//
// `residualCancelled: 1` is the flatten. The counts are the proof -- there were exactly two
// orders on that account and the second one was ours.
//
// Ordered the way an operator meets it in a crisis: their stops are cancelled (step 2, correctly,
// before flattening), the flatten is cancelled (step 4), the account is locked (step 5) so
// `nt_place_order` REFUSES the exit they would place by hand -- and the tool returns
// `success: true`. Naked position, no protection, no way out through the tool, and nothing says
// so.
//
// THE RULE: an order that was not on the account before this call began is an order this call
// created, and a cleanup pass may not cancel what its own caller just submitted.
//
// Two deliberate choices in the shape below:
//
//   * the "before" set is EVERY order on the account, not just the active ones. A bracket leg
//     sitting in a non-active state before the flatten and reaching `Working` after it is a
//     genuine residual and must still be cancelled; filtering "before" by state would classify
//     it as new and let it survive, which is this defect in the opposite direction.
//
//   * identity is REFERENCE identity. NT8's `OrderId` is not stable (it is why the core keys its
//     copy-progress map with `OrderReferenceComparer`), and both snapshots are taken inside one
//     synchronous dispatcher invoke, so the same order is the same object.
//
// WHY ITS OWN FILE: `McpBridgeAddOn.cs` is in no test build (`P2-27`), so anything inside it is
// pinnable only by source-text regex. This names no NinjaTrader type -- it is set arithmetic over
// `T : class` -- so `tests/BridgeTests.csproj` compiles and EXECUTES it, the same trade as
// `BridgeAccountResolver`, `CopierEnforcementView` and `BridgeOrderAction`.
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeFlattenPlan
    {
        /// <summary>
        /// The orders a post-flatten cleanup pass is allowed to cancel: those still active AND
        /// already present before the flatten was requested. Anything else appeared during this
        /// call and belongs to it.
        /// </summary>
        /// <param name="knownBeforeFlatten">every order on the account before `Flatten` was called
        /// -- not filtered by state, see the header</param>
        /// <param name="activeAfterFlatten">the orders currently in a cancellable state</param>
        public static List<T> ResidualCancelSet<T>(
            IEnumerable<T> knownBeforeFlatten,
            IEnumerable<T> activeAfterFlatten) where T : class
        {
            var known = new HashSet<T>(ReferenceComparer<T>.Instance);
            if (knownBeforeFlatten != null)
            {
                foreach (var order in knownBeforeFlatten)
                    if (order != null) known.Add(order);
            }

            var residual = new List<T>();
            if (activeAfterFlatten == null) return residual;

            foreach (var order in activeAfterFlatten)
            {
                if (order == null) continue;
                if (known.Contains(order)) residual.Add(order);
            }
            return residual;
        }

        /// <summary>
        /// The orders this call created: still active and NOT present beforehand. Nothing needs it
        /// to decide the cancel set -- it exists so the caller can REPORT what it submitted, which
        /// is the half of `P0-104` that made the defect invisible. `flattened++` counted the CALL.
        /// </summary>
        public static List<T> SubmittedByThisCall<T>(
            IEnumerable<T> knownBeforeFlatten,
            IEnumerable<T> activeAfterFlatten) where T : class
        {
            var known = new HashSet<T>(ReferenceComparer<T>.Instance);
            if (knownBeforeFlatten != null)
            {
                foreach (var order in knownBeforeFlatten)
                    if (order != null) known.Add(order);
            }

            var mine = new List<T>();
            if (activeAfterFlatten == null) return mine;

            foreach (var order in activeAfterFlatten)
            {
                if (order == null) continue;
                if (!known.Contains(order)) mine.Add(order);
            }
            return mine;
        }

        // Reference identity, spelled out rather than taken from ReferenceEqualityComparer:
        // the deployed assembly targets net48, where that type does not exist.
        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
            public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
