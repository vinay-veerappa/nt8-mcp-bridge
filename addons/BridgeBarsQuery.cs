// P3-111. The `/api/bars` contract: what a caller may ask for, and what this process will build.
//
// FILED AS: "`/api/bars` does `int.Parse(query["count"] ?? "100")` -- absent is handled,
// unparseable throws." That was one line of four defects. Probing the live box before writing any
// code (`measure-the-deployed-system`) found the endpoint broken at BOTH ends of every parameter:
//
//   THREE CRASHES -- every caller-supplied value except `symbol` 500s on a typo:
//     count=abc | periodValue=xyz | period=Banana   -> HTTP 500 + a .NET stack trace
//     (control: no `count` -> 200, 100 bars; count=5 -> 5 bars)
//
//   AN UNBOUNDED RESPONSE -- and it is worse than the crashes because it is SILENT:
//     count=5000     ->    531,658 bytes
//     count=200000   -> 21,285,727 bytes     <- twenty-one megabytes, served happily
//     count=1000000  -> 1,000,000 bars
//     count=5000000  -> 0 bars, silently, having presumably failed inside
//     count=0 / -5   -> 0 bars, indistinguishable from "this instrument has no data"
//   while the MCP tool schema advertises **"max 5,000 rows"** in two places and the addon
//   enforced nothing. `P1-72`'s shape -- advertised and not implemented -- on a size promise.
//
//   AN IGNORED `offset` -- advertised, sent by the wrapper, dropped by the route: `offset=0` and
//   `offset=500` returned BYTE-IDENTICAL payloads. That is `P2-109` verbatim at a second endpoint,
//   found by running the same test against it, hours after closing it on `/api/orders`.
//
// ⚠️ THE CAP IS 5,000 BECAUSE THE SCHEMA ALREADY SAID SO. Two numbers disagreed -- an advertised
// 5,000 and an enforced infinity -- and the choice of which to keep is not a taste. Lowering the
// promise to meet the code would break callers who believed it; raising the code to meet the
// promise makes an existing written contract TRUE. And the cap is only tolerable because `offset`
// now works: a bound on one RESPONSE is a bound on memory, but a bound on what is KNOWABLE would
// just push callers back to `/api/bars/export`. Paging is what makes the cap honest.
//
// ⚠️ AND `count` IS CLAMPED TO 1, NEVER 0, for the reason `BridgeOrderQuery` clamps its limit: an
// empty result on a read path reads as "this instrument has no data", which is the more expensive
// lie. `count=0` returning one bar is visibly a clamp; `count=0` returning none is a fact about the
// market that isn't true.
//
// WHY THE PERIOD NAMES ARE PASSED IN AS STRINGS: `BarsPeriodType` is an NT8 enum, and this file
// deliberately names no NT8 type so `tests/BridgeTests.csproj` can EXECUTE it (`P2-27`). The addon
// hands over `Enum.GetNames(typeof(BarsPeriodType))` at the call site. That keeps the validation
// testable AND keeps the valid set derived from the platform rather than from a list here that
// would drift the moment NT8 adds a bar type -- the `P1-72` failure mode, where a hand-typed enum
// in the wrapper disagreed with the addon's real whitelist.
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeBarsQuery
    {
        /// <summary>Bars returned when the caller does not ask for a count. The historical default,
        /// preserved: it is what every existing caller of this endpoint already gets.</summary>
        public const int DefaultCount = 100;

        /// <summary>Upper bound on ONE response. This is the number the MCP tool schema has always
        /// advertised ("max 5,000 rows"); before P3-111 nothing enforced it and 200,000 returned
        /// 21MB. Reachable beyond this via <c>offset</c>, which is why a cap is not a ceiling.</summary>
        public const int MaxCount = 5000;

        /// <summary>The bar period multiplier when absent. NT8's own default.</summary>
        public const int DefaultPeriodValue = 1;

        /// <summary>Upper bound on the period multiplier. Generous, because it is not what drives
        /// the response size -- <see cref="MaxCount"/> is. This exists so a nonsense value cannot
        /// reach NT8's BarsPeriod at all, not to express a view about how big a bar may be.</summary>
        public const int MaxPeriodValue = 1000000;

        /// <summary>The bar type when the caller does not name one.</summary>
        public const string DefaultPeriod = "Minute";

        /// <summary>
        /// Bars per response. Absent, blank or unparseable gives <see cref="DefaultCount"/>;
        /// anything else is clamped into [1, <see cref="MaxCount"/>]. Never throws and never
        /// returns 0.
        /// </summary>
        public static int ParseCount(string raw)
        {
            return BridgeQueryValue.ParseInt(raw, DefaultCount, 1, MaxCount);
        }

        /// <summary>
        /// How many of the most recent bars to skip before taking a page. Absent, blank,
        /// unparseable or negative gives 0.
        /// </summary>
        public static int ParseOffset(string raw)
        {
            return BridgeQueryValue.ParseInt(raw, 0, 0, int.MaxValue);
        }

        /// <summary>
        /// The bar period multiplier. Absent, blank or unparseable gives
        /// <see cref="DefaultPeriodValue"/>; clamped into [1, <see cref="MaxPeriodValue"/>].
        /// </summary>
        public static int ParsePeriodValue(string raw)
        {
            return BridgeQueryValue.ParseInt(raw, DefaultPeriodValue, 1, MaxPeriodValue);
        }

        /// <summary>
        /// The bar-type name to hand to NT8, or null with <paramref name="refusal"/> set if the
        /// caller named something that does not exist.
        ///
        /// ⚠️ ABSENT AND WRONG ARE DIFFERENT INPUTS -- the distinction the shipped defect did not
        /// make. Absent gets the default; wrong gets a refusal that LISTS what would have worked.
        /// Neither gets a stack trace, and neither is silently coerced to Minute: guessing that
        /// `period=Banana` meant Minute would answer a question the caller did not ask, with bars
        /// they would then reason over.
        /// </summary>
        public static string ResolvePeriod(string raw, IEnumerable<string> validNames, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(raw)) return DefaultPeriod;
            if (BridgeQueryValue.IsKnownName(raw, validNames)) return raw.Trim();
            refusal = BridgeQueryValue.RefusalFor("period", raw, validNames);
            return null;
        }

        /// <summary>
        /// How many bars to ASK NT8 for, given the page the caller wants. A page at
        /// <paramref name="offset"/> needs everything newer than it fetched too, because a bar
        /// series is windowed from its right edge -- so this is deliberately larger than
        /// <see cref="MaxCount"/> when paging back, while the RESPONSE stays capped.
        ///
        /// Saturates rather than overflowing: <c>offset=int.MaxValue</c> is a caller error, and
        /// wrapping it to a negative request size would be this defect wearing a new hat.
        /// </summary>
        public static int RequestSize(int count, int offset)
        {
            if (count < 1) count = 1;
            if (offset < 0) offset = 0;
            long total = (long)count + offset;
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }
}
