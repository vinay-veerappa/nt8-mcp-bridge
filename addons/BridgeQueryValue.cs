// P3-111. Reading a value out of a query string, once, for every endpoint that does it.
//
// ⚠️ A QUERY PARAMETER IS A STRING FROM OUTSIDE. Every branch below exists because some string
// actually reaches it. `/api/bars` did this:
//
//     int.Parse(query["periodValue"] ?? "1"), int.Parse(query["count"] ?? "100")
//
// The `??` handles the parameter being ABSENT. Nothing handled it being PRESENT AND
// UNPARSEABLE -- and those are different inputs. Measured on the live box 2026-08-14:
//
//     GET /api/bars?symbol=MNQ 09-26&count=abc         -> HTTP 500 + a .NET stack trace
//     GET /api/bars?symbol=MNQ 09-26&periodValue=xyz   -> HTTP 500 + a .NET stack trace
//     GET /api/bars?symbol=MNQ 09-26&period=Banana     -> HTTP 500 + a .NET stack trace
//     GET /api/bars?symbol=MNQ 09-26                   -> HTTP 200, 100 bars  (control)
//
// Three of the four values a caller supplies crash the endpoint on a typo. `Enum.Parse` is the
// same defect wearing a different type: it is `int.Parse` for names.
//
// ⚠️ AND THE RANGE END IS WORSE THAN THE PARSE END, because it is SILENT. Also measured:
//
//     count=0          -> 0 bars      indistinguishable from "this instrument has no data"
//     count=-5         -> 0 bars      same
//     count=5000       -> 531,658 bytes
//     count=200000     -> 21,285,727 bytes        <- twenty-one megabytes, served happily
//     count=1000000    -> 1,000,000 bars
//     count=5000000    -> 0 bars      silently, having presumably failed inside
//
// while the MCP tool schema advertises **"max 5,000 rows"** in two places. The receiver
// implemented no bound at all, so the contract's only statement about size was false in both
// directions: it under-promised what would be served, and over-promised that anything was
// bounded. That is `P1-72`'s shape (advertised and not implemented) meeting `P2-109`'s.
//
// THE RULES, each with a reason rather than a taste:
//
//   * UNPARSEABLE FALLS BACK TO THE DEFAULT. It does not throw, and it does not mean zero.
//     Throwing turns a caller typo into a 500 with a stack trace; zero turns it into an empty
//     result that reads as "no data", which is the more expensive lie.
//
//   * CLAMPED AT BOTH ENDS, and the lower bound is 1, never 0. An empty page and an empty book
//     are indistinguishable to whoever reads the answer -- the same reasoning as
//     `BridgeOrderQuery.ParseLimit`, and the same reasoning that made `P2-109` invisible.
//
//   * A NEGATIVE OFFSET IS 0, never an index from the end.
//
// WHY THIS FILE AND NOT A COPY IN GetBars: `BridgeOrderQuery` already had this exact arithmetic
// for `/api/orders`, written hours earlier for `P2-109`. A second copy for `/api/bars` is how
// `P1-90` reached eight sites and how `P1-100` ended with three readers of one flag. So the
// arithmetic moved here and `BridgeOrderQuery` delegates.
//
// WHY IT NAMES NO NT8 TYPE: `McpBridgeAddOn.cs` is in no test build (`P2-27`). This takes strings
// and ints, so `tests/BridgeTests.csproj` compiles and EXECUTES it. The enum helper deliberately
// takes the valid names as strings rather than a `Type`, for the same reason.
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeQueryValue
    {
        /// <summary>
        /// An integer query parameter. Absent, blank or unparseable gives <paramref name="fallback"/>;
        /// anything else is clamped into [<paramref name="min"/>, <paramref name="max"/>].
        /// Never throws — this is the whole point.
        /// </summary>
        public static int ParseInt(string raw, int fallback, int min, int max)
        {
            // A caller-supplied range that is inside-out is a programming error at the CALL site,
            // not a bad request. Refusing to invent an answer beats silently picking one.
            if (min > max) throw new ArgumentException("min must not exceed max", "min");

            int value;
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out value))
                value = fallback;

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// Whether a caller's value names one of <paramref name="validNames"/>, case-insensitively.
        /// Blank is NOT a match: the caller's default is applied before this is reached, so a blank
        /// arriving here is a caller bug and matching it to the first valid name would hide it.
        /// </summary>
        public static bool IsKnownName(string raw, IEnumerable<string> validNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (validNames == null) return false;
            var want = raw.Trim();
            return validNames.Any(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The operator-facing refusal for a value that names nothing. It LISTS the valid names,
        /// for the same reason `BridgeAccountResolver` lists the available accounts: a refusal
        /// that does not say what would have worked costs the caller another round trip, and a
        /// stack trace says nothing at all.
        /// </summary>
        public static string RefusalFor(string parameterName, string raw, IEnumerable<string> validNames)
        {
            var names = (validNames ?? Enumerable.Empty<string>()).ToList();
            return string.Format(
                "'{0}' is not a valid {1}. Valid values: {2}. "
                + "Refusing rather than guessing which one you meant.",
                raw, parameterName,
                names.Count == 0 ? "(none available)" : string.Join(", ", names.ToArray()));
        }

        /// <summary>
        /// The window of a bar series a caller asked for, counting BACKWARDS from the most recent
        /// bar: skip <paramref name="offset"/> of the newest, then take up to <paramref name="count"/>.
        ///
        /// ⚠️ The direction is the whole reason paging exists here. `/api/orders` pages forward
        /// through a list; a bar series is read from the RIGHT EDGE, because "the last 100 bars" is
        /// what every caller means and "bars 0..99" is what nobody means. So `offset=100, count=100`
        /// is the hundred bars BEFORE the most recent hundred, which is how a caller reaches further
        /// back than the per-request cap without the cap being a ceiling on what is knowable.
        ///
        /// Returns false for an empty window — an offset past the start of the series is not an
        /// error, it is simply the end of the history.
        /// </summary>
        public static bool BarWindow(int available, int count, int offset, out int start, out int take)
        {
            start = 0;
            take = 0;
            if (available <= 0 || count <= 0) return false;
            if (offset < 0) offset = 0;

            int end = available - offset;      // exclusive; the newest bar the caller wants
            if (end <= 0) return false;        // paged off the front of the series

            start = end - count;
            if (start < 0) start = 0;          // fewer bars exist than were asked for; give what there is
            take = end - start;
            return take > 0;
        }

        /// <summary>
        /// How many items a page carries, given a total, a limit and an offset. An offset at or
        /// past the end is an EMPTY page — not an error, and not a wrapped-around one.
        /// </summary>
        public static int PageSize(int total, int limit, int offset)
        {
            if (total <= 0 || limit <= 0) return 0;
            if (offset < 0) offset = 0;
            if (offset >= total) return 0;
            int remaining = total - offset;
            return remaining < limit ? remaining : limit;
        }
    }
}
