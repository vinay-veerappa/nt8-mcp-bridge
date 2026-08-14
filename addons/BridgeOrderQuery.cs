// P2-109, second half. `nt_orders` advertises THREE parameters and implemented NONE of them.
//
// The measured defect was the ignored `account` filter (see BridgeAccountScope's header), but the
// route that dropped it -- `case "/api/orders": return GetOrders();` -- dropped `limit` and
// `offset` with it, while the tool description promised "cursor pagination". So a caller asking
// for 8 orders got every order on 96 accounts, and an agent paging through with `offset` re-read
// page one forever.
//
// ⚠️ THE PARSING IS THE PART WORTH TESTING, and it is why this is a class rather than two
// `int.Parse` calls at the call site. `/api/bars` on the next line does:
//
//     int.Parse(query["count"] ?? "100")
//
// which handles ABSENT but THROWS on `count=abc` -- an unhandled FormatException on a read
// endpoint, from a caller typo. Absent and unparseable are different inputs and only one of them
// was considered. A query parameter is attacker-shaped by construction: it is a string from
// outside, and every branch here exists because some string reaches it.
//
// Three rules, each with a reason rather than a taste:
//
//   * UNPARSEABLE FALLS BACK TO THE DEFAULT, it does not throw and it does not mean zero.
//     `limit=abc` returning an error would be defensible; returning ZERO ORDERS would not, because
//     an empty list on a read path reads as "nothing is working" -- which is the exact class of
//     false reassurance `P2-109` is.
//
//   * THE LIMIT IS CLAMPED AT BOTH ENDS. `limit=0` and `limit=-5` mean "no orders", which no
//     caller wants and which is indistinguishable from a flat book; `limit=999999999` is a
//     request to serialise every order on 96 accounts into one HTTP response, which is how the
//     inventory endpoint measured 648KB per poll.
//
//   * A NEGATIVE OFFSET IS ZERO, never an index from the end. Python's semantics here would make
//     `offset=-1` silently return the LAST page while looking like a caller error.
//
// WHY IT NAMES NO NT8 TYPE: `McpBridgeAddOn.cs` is in no test build (`P2-27`). This takes strings
// and ints, so `tests/BridgeTests.csproj` compiles and EXECUTES it.
//
// ⚠️ P3-111 UPDATE: the three rules above are now KEPT IN `BridgeQueryValue`, and the methods below
// are thin bindings of the ORDER endpoint's numbers (50 / 500 / 1) to that shared arithmetic. The
// prediction two paragraphs up came true within hours -- `/api/bars` crashed on `count=abc`,
// `periodValue=xyz` AND `period=Banana`, served 21MB for `count=200000`, and ignored `offset`
// exactly as this endpoint had. Writing the same rules a second time for bars is how `P1-90`
// reached eight sites, so they moved instead.
//
// The mutation anchors moved WITH them (`P2-109`'s battery now points at `BridgeQueryValue.cs`),
// which strengthened them rather than merely preserving them: one mutant to the shared clamp is
// now evidence about BOTH endpoints, where before it was evidence about orders alone.
namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeOrderQuery
    {
        /// <summary>The page size when the caller does not ask for one. Matches the value the MCP
        /// tool schema has always advertised as its default.</summary>
        public const int DefaultLimit = 50;

        /// <summary>Upper bound on one page. Not a caller convenience -- a bound on the response
        /// this process will build in memory and serialise.</summary>
        public const int MaxLimit = 500;

        /// <summary>
        /// The page size for a raw query value. Absent, blank or unparseable gives the default;
        /// anything else is clamped into [1, <see cref="MaxLimit"/>]. Never returns 0, because an
        /// empty page and an empty book are indistinguishable to whoever reads the answer.
        /// </summary>
        public static int ParseLimit(string raw)
        {
            return BridgeQueryValue.ParseInt(raw, DefaultLimit, 1, MaxLimit);
        }

        /// <summary>
        /// The number of orders to skip. Absent, blank, unparseable or negative gives 0 --
        /// a negative offset is a caller error, not an index from the end.
        /// </summary>
        public static int ParseOffset(string raw)
        {
            return BridgeQueryValue.ParseInt(raw, 0, 0, int.MaxValue);
        }

        /// <summary>
        /// How many items of <paramref name="matched"/> this page actually carries, given an
        /// already-parsed limit and offset. Separated from the slicing itself so the arithmetic
        /// is testable without a list: an offset past the end is an EMPTY page, not an error and
        /// not a wrapped-around one.
        /// </summary>
        public static int PageSize(int matched, int limit, int offset)
        {
            return BridgeQueryValue.PageSize(matched, limit, offset);
        }

        /// <summary>
        /// Whether more results exist beyond this page -- the field an agent needs in order to
        /// know whether to page again. Derived from the same three numbers the page is, so it
        /// cannot disagree with what was actually returned.
        /// </summary>
        public static bool HasMore(int matched, int limit, int offset)
        {
            if (offset < 0) offset = 0;
            return matched > offset + PageSize(matched, limit, offset);
        }
    }
}
