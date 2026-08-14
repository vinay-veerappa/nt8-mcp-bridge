/**
 * P2-103. The inventory view: does the summary tell the truth about the payload it summarises?
 *
 * ⚠️ The defect class this defends against is `F-9`: the guard REPORTING one thing while DOING
 * another -- a rule called `Disabled` that the guard ran, and one called live that could not
 * fire. A summary that kept its own counters would be free to disagree with the rows beneath it
 * in exactly that way, and nothing would ever contradict it. So the tests below check the summary
 * AGAINST the detail, not against hand-written expected numbers.
 *
 * The fixture's shape and proportions come from the real box, measured 2026-08-14:
 * 96 accounts, 2304 rule rows, 384 ConfiguredNotEvaluated arising from FOUR distinct rules.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { summarise, forAccount, accountNames, STATE_ORDER } from '../lib/inventory-view.js';

/** A miniature of the live payload: 3 accounts x 4 rules, with every state represented. */
function fixture() {
  const rulesFor = (acct) => ([
    { name: 'Daily loss limit',    configPath: 'PnLRules.DailyLossLimit', state: 'EvaluatedNotEnforcing', limit: -1000, currentValue: 487 },
    { name: 'Trailing drawdown',   configPath: 'PnLRules.TrailingDrawdown', state: acct === 'Sim101' ? 'Enforcing' : 'EvaluatedNotEnforcing', limit: 1500, currentValue: 98995 },
    { name: 'Prop suite armed',    configPath: 'PropFirm.Armed', state: 'ConfiguredNotEvaluated', limit: null, currentValue: null },
    { name: 'News events file',    configPath: 'PropFirm.LocalNewsEventsFilePath', state: 'ConfiguredNotEvaluated', limit: null, currentValue: null },
  ]);
  return {
    takenUtc: '2026-08-14T21:04:38Z',
    mode: 'shadow',
    isArmed: true,
    accounts: ['Sim101', 'Sim-ORB', 'TAKEPROFITPRO524207503'].map(n => ({
      accountName: n, isExcluded: false, isLockedOut: false, rules: rulesFor(n),
    })),
    unevaluatedRules: [{ name: 'Consistency cap', state: 'ConfiguredNotEvaluated' }],
  };
}

test('P2-103: every count is derived from the rule rows, not kept alongside them', () => {
  const inv = fixture();
  const s = summarise(inv);

  // Recount independently from the fixture and require agreement. If the summary ever grows its
  // own counter, this fails -- which is F-9's shape caught at the seam it would enter through.
  const rows = inv.accounts.flatMap(a => a.rules);
  assert.equal(s.ruleRows, rows.length, 'ruleRows equals the actual number of rule rows');
  assert.equal(s.accounts, inv.accounts.length);

  for (const state of STATE_ORDER) {
    const expected = rows.filter(r => r.state === state).length;
    assert.equal(s.byState[state], expected, `byState.${state} matches the rows`);
  }
  const summed = Object.values(s.byState).reduce((a, b) => a + b, 0);
  assert.equal(summed, rows.length, 'the states partition the rows -- none double-counted or lost');
});

test('P2-103: every state appears even at zero, because an absent key reads as "not measured"', () => {
  const inv = fixture();
  // Remove every Enforcing row so the count is genuinely zero.
  for (const a of inv.accounts) for (const r of a.rules) if (r.state === 'Enforcing') r.state = 'Disabled';
  const s = summarise(inv);

  for (const state of STATE_ORDER) {
    assert.ok(Object.prototype.hasOwnProperty.call(s.byState, state),
      `${state} is present as a key even when zero`);
  }
  assert.equal(s.byState.Enforcing, 0);
  assert.equal(s.enforcingCount, 0);
  assert.deepEqual(s.enforcing, [], 'and nothing is named as enforcing');
});

test('P2-103: enforcingCount is the number an operator actually asks for', () => {
  const s = summarise(fixture());
  const expected = fixture().accounts
    .flatMap(a => a.rules).filter(r => r.state === 'Enforcing').length;
  assert.equal(s.enforcingCount, expected);
  assert.equal(s.enforcingCount, 1, 'the fixture has exactly one enforcing rule');
  assert.equal(s.enforcing[0].account, 'Sim101');
  assert.equal(s.enforcing[0].rule, 'Trailing drawdown');
  assert.equal(s.enforcing[0].limit, 1500, 'and it carries the LIMIT, not just the name');
});

test('P2-103: ConfiguredNotEvaluated is collapsed by RULE, not listed per account', () => {
  const s = summarise(fixture());

  // 3 accounts x 2 such rules = 6 rows, but only TWO facts. On the live box it is 384 rows and
  // four facts. Listing them per account buries the finding under its own repetition -- the
  // P2-41 shape, where a PerAccount rule reading a global collection reported evidence for all
  // 96 accounts from one mapping.
  assert.equal(s.byState.ConfiguredNotEvaluated, 6, 'six rows underneath');
  assert.equal(s.configuredNotEvaluated.length, 2, 'but two distinct rules reported');

  const names = s.configuredNotEvaluated.map(r => r.rule).sort();
  assert.deepEqual(names, ['News events file', 'Prop suite armed']);
  for (const entry of s.configuredNotEvaluated) {
    assert.equal(entry.accounts, 3, 'each names how many accounts it affects');
    assert.ok(entry.example, 'and an example account, so it can be checked by hand');
  }
});

test('P2-103: a truncated list SAYS it was truncated', () => {
  const inv = fixture();
  for (const a of inv.accounts) for (const r of a.rules) r.state = 'Enforcing';
  const s = summarise(inv, { maxNamed: 2 });

  assert.equal(s.enforcingCount, 12, 'the COUNT is complete');
  assert.equal(s.enforcing.length, 2, 'the named list is capped');
  assert.equal(s.enforcingTruncated, true,
    'and it says so -- a list that silently stops is how a reader concludes there are only two');
});

test('P2-103: forAccount refuses rather than answering about a different account', () => {
  const inv = fixture();

  assert.equal(forAccount(inv, 'Sim101').accountName, 'Sim101');
  assert.equal(forAccount(inv, 'sim101').accountName, 'Sim101', 'case-insensitive');
  assert.equal(forAccount(inv, '  Sim101 ').accountName, 'Sim101', 'trimmed');

  // P1-90 on a read path -- which is exactly what P2-109 was, measured returning a FUNDED
  // account's order for a request naming Sim101.
  assert.equal(forAccount(inv, 'Sim1O1'), null, 'a typo resolves to nothing, not to something');
  assert.equal(forAccount(inv, ''), null, 'and neither does an empty name');
  assert.equal(forAccount(inv, '   '), null);
  assert.equal(forAccount(inv, null), null);
});

test('P2-103: a malformed or empty payload does not throw on a read path', () => {
  // A read that 500s is worse than a read that says it found nothing -- P3-111 is filed for
  // exactly this shape on /api/bars.
  for (const bad of [null, undefined, {}, { accounts: null }, { accounts: [] }]) {
    const s = summarise(bad);
    assert.equal(s.ruleRows, 0);
    assert.equal(s.accounts, 0);
    assert.equal(s.enforcingCount, 0);
    assert.deepEqual(s.configuredNotEvaluated, []);
  }
  assert.deepEqual(accountNames(null), []);
  assert.equal(forAccount(null, 'Sim101'), null);
});

test('P2-103: an account whose rules are missing counts as an account with no rules', () => {
  const inv = fixture();
  delete inv.accounts[1].rules;
  const s = summarise(inv);
  assert.equal(s.accounts, 3, 'the account is still counted');
  assert.equal(s.ruleRows, 8, 'and contributes no rule rows rather than throwing');
});
