/**
 * lib/account-filter.js — client-side account filtering for the zero-param read tools.
 *
 * nt_accounts and nt_positions reach addon endpoints (GetAccountInfo / GetPositions)
 * that take NO parameters, so an account filter cannot be pushed server-side the way
 * nt_orders pushes `account` into the query string. The filtering happens here, in the
 * wrapper, after the fetch — the same place nt_copier_snapshot filters its rows and
 * nt_riskguard_inventory's `forAccount` lives.
 *
 * WHY ITS OWN MODULE: importing nt-mcp-server.js starts its stdin readline loop, so a
 * test that reached in for these helpers would hang. This is the same split the repo
 * already made for lib/tools.js, lib/copier-config-request.js, and lib/inventory-view.js
 * — put the thing you need to assert on somewhere with no side effects.
 *
 * P1-90 on a READ path. A supplied account name that matches nothing is REFUSED naming
 * the available accounts, never answered about every account — the same rule
 * nt_riskguard_inventory's `forAccount` enforces.
 *
 * ⚠️ THE REFUSAL APPLIES TO nt_accounts, NOT nt_positions. The rows ARE accounts for
 * nt_accounts, so an empty match means the account does not exist — a refusal. For
 * nt_positions the rows are POSITIONS, so an empty match means the account has no open
 * positions — a valid answer, not a refusal. Treating those two the same was the first
 * draft of this function, and it turned "Sim101 has no positions" into "Sim101 does not
 * exist", which is reassurance about the wrong thing.
 */

/**
 * Filter account rows (from GetAccountInfo) by name. The rows ARE accounts, so a name
 * that matches nothing means the account does not exist — REFUSED naming the available
 * accounts. When no name is supplied, returns all rows.
 *
 * @returns {Array<object>|{error:string, availableSample:string[]}}
 */
export function filterAccounts(rows, accountName) {
  if (!accountName || !String(accountName).trim()) return rows;
  const want = String(accountName).trim().toLowerCase();
  const filtered = rows.filter(r => String(r.name || '').trim().toLowerCase() === want);
  if (filtered.length === 0) {
    const names = rows.map(r => r.name).filter(Boolean);
    return {
      error: `No account named '${accountName}' (${names.length} available). ` +
             'Refusing to answer about a different account.',
      availableSample: [...new Set(names)].slice(0, 10),
    };
  }
  return filtered;
}

/**
 * Filter position rows (from GetPositions) by account. The rows are POSITIONS, not
 * accounts, so an empty match means the account has no open positions — a valid answer,
 * returned as an empty array. When no name is supplied, returns all rows.
 *
 * @returns {Array<object>}
 */
export function filterPositions(rows, accountName) {
  if (!accountName || !String(accountName).trim()) return rows;
  const want = String(accountName).trim().toLowerCase();
  return rows.filter(r => String(r.account || '').trim().toLowerCase() === want);
}