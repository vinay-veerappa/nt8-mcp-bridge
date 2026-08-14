// P2-109. What "this request is about account X" means -- ONE definition, for every path.
//
// ⚠️ `nt_orders`' `account` PARAMETER WAS IGNORED. Measured live on 2026-08-14, two calls back to
// back:
//
//     nt_orders(account="Sim101", limit=8)  -> [ { account: "TAKEPROFITPRO524207503", ... } ]
//     nt_orders(limit=6)                    -> [ { account: "TAKEPROFITPRO524207503", ... } ]
//
// Byte-identical, and the single order in them is on a FUNDED account -- not the Sim101 the caller
// named. Sim101 genuinely had no working orders, so the honest answer was `[]`.
//
// THE INTERESTING PART IS WHERE IT WENT WRONG. Every layer is correct in isolation:
//
//   * `mcp/lib/tools.js` advertises `account`, `limit` and `offset`;
//   * `mcp/nt-mcp-server.js` builds the query string and sends all three;
//   * `GetOrders()` is a clean, correct read of every account's orders;
//   * and the one line joining them was `case "/api/orders": return GetOrders();`
//
// The route took no parameters, while the routes on either side of it were already passing
// `query["account"]`. Nothing is wrong with any component; the CONTRACT between them was never
// connected, and a read that silently widens its own scope looks exactly like a read that found
// more than you expected.
//
// This is `P1-90`'s family on a READ path, which that entry's header names explicitly: "for a
// write that means acting on the wrong account; for a read it means answering confidently about
// someone else's." Both `P0-104` and `P1-105` were diagnosed partly by reading order state.
//
// WHY THIS FILE EXISTS RATHER THAN A SECOND COPY OF THE PREDICATE. `BridgeClosePlan` already had
// this exact rule for deciding which positions a close request covers. Writing a second one for
// orders is how `P1-90` reached SIX sites and how `P1-100` ended up with three readers of one
// flag, each taught something different. So the rule moved here and both callers ask it.
//
// ⚠️ Extraction is not free: it broke two of `mutate_p1105.py`'s anchors, which were REPOINTED at
// this file rather than retired -- the same move `P1-100` made with `mutate_p292.py`. An anchor
// that stops matching scores a SURVIVOR, so a moved predicate with stale anchors is a battery
// quietly proving nothing.
//
// WHY IT NAMES NO NT8 TYPE: `McpBridgeAddOn.cs` is in no test build (`P2-27`), so anything inside
// it is pinnable only by source-text regex. This takes account names as strings, so
// `tests/BridgeTests.csproj` compiles and EXECUTES it.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeAccountScope
    {
        /// <summary>
        /// Whether an account is one this request named. An empty or absent request means EVERY
        /// account -- the long-standing contract of the read and close paths, kept deliberately.
        /// </summary>
        /// <remarks>
        /// ⚠️ The CALLER is expected to have already refused a name that resolves to nothing
        /// (<see cref="BridgeAccountResolver"/>, `P1-90`). A typo reaching here matches no
        /// account, and "no orders" is a far worse answer than "there is no account called that":
        /// the first reads as reassurance, and on a read path reassurance is the whole damage.
        /// </remarks>
        public static bool Matches(string accountName, string requestedAccount)
        {
            if (string.IsNullOrWhiteSpace(requestedAccount)) return true;
            if (string.IsNullOrWhiteSpace(accountName)) return false;
            return string.Equals(accountName.Trim(), requestedAccount.Trim(),
                                 StringComparison.OrdinalIgnoreCase);
        }
    }
}
