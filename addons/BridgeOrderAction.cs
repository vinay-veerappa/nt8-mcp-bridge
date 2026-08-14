// P1-97. Which of NinjaTrader's four OrderActions a bridge `buy`/`sell` really is.
//
// `nt_place_order` mapped `buy` -> Buy and `sell` -> Sell UNCONDITIONALLY, so it could never
// emit `SellShort` or `BuyToCover`. NT8 accepts that and nets the position correctly -- the
// order works. What breaks is everything downstream that reads the LABEL, and the copier does:
//
//     bool leaderIsExiting = leadAction == OrderAction.Sell || leadAction == OrderAction.BuyToCover;
//
// Measured on the live box 2026-08-13, both halves:
//
//   sell 1 from FLAT   -> real position Short 1  -> copier read isExit=TRUE   (a short ENTRY,
//                                                   classified as an exit, so never copied)
//   buy 1 from SHORT   -> real position flat     -> copier read isExit=FALSE  (a COVER,
//                                                   classified as an entry, so copied as a
//                                                   NEW POSITION IN THE OPPOSITE DIRECTION)
//
// The second produced no wrong position in that run only because AutoSymbolConversion rounded
// 1 MNQ below one NQ contract and it died on COPY_SKIPPED_SUB_MINIMUM. Nothing in the
// correctness path stopped it.
//
// ⚠️ The right fix is HERE and not in the copier. Widening `leaderIsExiting` to accept more
// labels treats the symptom: a label is chosen by whoever submits the order, so it is the wrong
// source of truth for "is this an exit?" -- the durable test is the position DELTA, which is a
// larger change (see the plan's P1-97 entry). What this file does is make the label TRUE, which
// is cheap, local, and is already how `McpBridgeAddOn`'s own close path works
// (`pos.MarketPosition == Long ? Sell : BuyToCover`).
//
// WHY ITS OWN FILE: `McpBridgeAddOn.cs` is in no test build (`P2-27`), so anything inside it is
// pinnable only by source-text regex. This names no NinjaTrader type -- it takes and returns
// strings -- so `tests/BridgeTests.csproj` compiles and EXECUTES it. Same trade as
// `BridgeAccountResolver.cs` and `CopierEnforcementView.cs`.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeOrderAction
    {
        public const string Buy = "Buy";
        public const string Sell = "Sell";
        public const string BuyToCover = "BuyToCover";
        public const string SellShort = "SellShort";

        /// <summary>
        /// The NT8 convention: Buy and SellShort OPEN, Sell and BuyToCover CLOSE.
        ///
        /// <paramref name="currentSide"/> is the account's position in this instrument right now
        /// -- "Long", "Short", or anything else (including null) meaning flat. Taken as a string
        /// so this file names no NT8 type; the caller passes
        /// <c>position?.MarketPosition.ToString()</c>.
        ///
        /// A quantity larger than the position is a REVERSAL, and it still takes the closing
        /// label: NT8 nets it, and the leading part of the order genuinely is a close. Splitting
        /// it into two orders would change the fill semantics of a request the caller made as
        /// one, which is not this fix's job.
        /// </summary>
        public static string Resolve(string action, string currentSide)
        {
            bool isBuy = string.Equals(action, "buy", StringComparison.OrdinalIgnoreCase);

            bool isLong = string.Equals(currentSide, "Long", StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(currentSide, "Short", StringComparison.OrdinalIgnoreCase);

            if (isBuy)
                return isShort ? BuyToCover : Buy;

            return isLong ? Sell : SellShort;
        }
    }
}
