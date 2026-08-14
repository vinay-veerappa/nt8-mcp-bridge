/**
 * lib/inventory-view.js — turning the guard's rule inventory into an answer.
 *
 * P2-103. `/api/riskguard/inventory` is the surface that answers "is the guard actually
 * protecting me, and to what limit?" — and it had NO MCP tool, so the agent driving the system
 * could not read it. Five of the core's mutation batteries (UI1, UI3, UI4, UI5, UI6) exist to
 * keep exactly this payload honest, and `F-9` was a defect in it. That honesty was bought and
 * was not being spent.
 *
 * ⚠️ WHY A VIEW AND NOT A PASSTHROUGH. Measured on the live box 2026-08-14:
 *
 *     /api/riskguard/inventory  ->  635,448 bytes   96 accounts   2,304 rule rows
 *
 * Returning that from a tool call would spend the context window on one read. The constraint is
 * CONTEXT, not bandwidth — 635KB over localhost costs nothing, so the summarising happens here,
 * in the wrapper, after the fetch. `measure-the-deployed-system` records the same number from the
 * other direction: fetch the real payload BEFORE designing the view.
 *
 * ⚠️ AND THE SUMMARY IS DERIVED FROM THE DETAIL, never computed alongside it. `F-9` was the guard
 * REPORTING one thing while DOING another — a rule called `Disabled` that the guard ran, and one
 * called live that could not fire. A summary assembled from its own counters would be free to
 * disagree with the rows underneath it in exactly that way. Every number below is folded out of
 * the same `rules` arrays the `account` view returns.
 *
 * WHY ITS OWN MODULE: importing nt-mcp-server.js starts its stdin readline loop, so anything a
 * test needs to assert on has to live somewhere with no side effects — the same move as
 * `lib/tools.js` and `lib/copier-config-request.js`, and the same move that made the addon's
 * account resolver testable (`P1-90`).
 */

/**
 * The rule states the guard reports, ordered by how much an operator should care.
 *
 * ⚠️ `ConfiguredNotEvaluated` is the dangerous one and it is not the scary-sounding one. It means
 * the config file describes a protection that NOTHING COMPUTES — it reads as protection and is
 * not protection. `Disabled` is honest by comparison: it says so. This ordering is why the
 * summary leads with the count that matters instead of listing states alphabetically.
 */
export const STATE_ORDER = [
  'Enforcing',
  'EvaluatedNotEnforcing',
  'ConfiguredNotEvaluated',
  'Inert',
  'Disabled',
];

/** Every rule row across every account, flattened. The one place the shape is walked. */
function allRules(inventory) {
  const accounts = (inventory && Array.isArray(inventory.accounts)) ? inventory.accounts : [];
  const out = [];
  for (const acct of accounts) {
    const rules = Array.isArray(acct && acct.rules) ? acct.rules : [];
    for (const rule of rules) out.push({ account: acct.accountName, rule });
  }
  return out;
}

/** Counts by state, in STATE_ORDER, including zeroes — an absent key reads as "not measured". */
function countByState(rows) {
  const counts = {};
  for (const state of STATE_ORDER) counts[state] = 0;
  for (const { rule } of rows) {
    const state = (rule && rule.state) || 'Unknown';
    counts[state] = (counts[state] || 0) + 1;
  }
  return counts;
}

/**
 * Restrict an inventory to one account. Returns null when the name matches nothing, so the caller
 * can say "no such account" rather than answering about all 96 — `P1-90` on a read path, which is
 * precisely what `P2-109` was.
 */
export function forAccount(inventory, accountName) {
  const accounts = (inventory && Array.isArray(inventory.accounts)) ? inventory.accounts : [];
  if (!accountName || !String(accountName).trim()) return null;
  const want = String(accountName).trim().toLowerCase();
  const found = accounts.find(a => String(a.accountName || '').trim().toLowerCase() === want);
  return found || null;
}

/** The account names the inventory covers, for a refusal message that can be acted on. */
export function accountNames(inventory) {
  const accounts = (inventory && Array.isArray(inventory.accounts)) ? inventory.accounts : [];
  return accounts.map(a => a.accountName).filter(Boolean);
}

/**
 * The default view: small enough to read, and honest about what it left out.
 *
 * `truncated` is deliberately explicit rather than implied by a length. A list that silently
 * stops is how a caller concludes there are only five problems.
 */
export function summarise(inventory, { maxNamed = 12 } = {}) {
  const rows = allRules(inventory);
  const byState = countByState(rows);

  // The rules the config claims and nothing computes, named by rule rather than by account: 96
  // accounts sharing one misconfiguration is ONE fact, and listing it 96 times buries it. This is
  // the `P2-41` shape — a PerAccount rule reading a global collection reported evidence for all
  // 96 accounts from one mapping.
  const configuredNotEvaluated = {};
  for (const { account, rule } of rows) {
    if (rule.state !== 'ConfiguredNotEvaluated') continue;
    const key = rule.name || rule.configPath || 'unnamed rule';
    if (!configuredNotEvaluated[key]) configuredNotEvaluated[key] = [];
    configuredNotEvaluated[key].push(account);
  }
  const dangerous = Object.keys(configuredNotEvaluated).sort().map(name => ({
    rule: name,
    accounts: configuredNotEvaluated[name].length,
    example: configuredNotEvaluated[name][0],
  }));

  const enforcing = rows.filter(r => r.rule.state === 'Enforcing');
  const enforcingNamed = enforcing.slice(0, maxNamed).map(({ account, rule }) => ({
    account, rule: rule.name, limit: rule.limit, current: rule.currentValue,
  }));

  return {
    takenUtc: inventory && inventory.takenUtc,
    mode: inventory && inventory.mode,
    isArmed: inventory && inventory.isArmed,
    accounts: accountNames(inventory).length,
    ruleRows: rows.length,
    byState,

    // ⚠️ The one-line answer to "is anything actually stopping me?". Zero is the correct and
    // expected reading in `shadow`; it is alarming only in `live`, and saying so here means the
    // number does not have to be interpreted by whoever reads it.
    enforcingCount: enforcing.length,
    enforcing: enforcingNamed,
    enforcingTruncated: enforcing.length > enforcingNamed.length,

    // The state that reads as protection and is not protection.
    configuredNotEvaluated: dangerous,

    // Rules the guard could not attribute to any account at all. Reported verbatim: this is the
    // field most likely to be empty, and an empty list here is a real answer.
    unevaluatedRules: (inventory && inventory.unevaluatedRules) || [],

    note: 'Summary derived from the same rule rows the account view returns. '
        + 'Use view="account" with an account name for the full rule list, '
        + 'or view="full" for the raw payload (~635KB on 96 accounts).',
  };
}
