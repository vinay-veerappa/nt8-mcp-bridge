/**
 * Tests for the MCP tool schemas — P1-91.
 *
 * These assert on the REAL exported schema objects, not on source text, which is
 * why `lib/tools.js` exists as its own module. See that file's header.
 *
 * What P1-91 is. `P1-90` fixed the NT8 bridge so that an order naming an account
 * it cannot resolve is REFUSED rather than placed on an arbitrary one. Four tool
 * schemas here still advertised `default: 'Sim101'` on `account` — two of them
 * ORDER tools. Two problems:
 *
 *   1. The contract misdescribes the addon. It tells a caller that omitting
 *      `account` targets Sim101. The addon refuses.
 *   2. The one that matters: an MCP client is PERMITTED to materialise a schema
 *      default into the request. A client that does would inject a real,
 *      connected account name into an order call. The bridge would resolve it
 *      happily and the refusal would never be reached — P1-90 re-created one
 *      layer out. This is `P1-73`'s shape exactly: a schema default that became a
 *      supplied argument because the receiver merged it.
 *
 * It was measured that the client in use today does NOT materialise defaults.
 * That is a property of that client, not of the contract, and is not a reason to
 * leave a default on a field that names an account.
 */
import test from 'node:test';
import assert from 'node:assert/strict';

import { TOOLS } from '../lib/tools.js';

const byName = new Map(TOOLS.map((t) => [t.name, t]));
const props = (name) => byName.get(name)?.inputSchema?.properties ?? {};
const required = (name) => byName.get(name)?.inputSchema?.required ?? [];

// The tools whose NT8 handler now refuses a missing or unresolvable account
// (nt8-mcp-bridge, BridgeAccountResolver + the six call sites). For these, and
// only these, the schema must say the account is required.
//
// nt_place_order is in this list and was NOT in P1-91's filed set of four: it
// carries no default, but it does not require `account` either, and its handler
// refuses without one.
const REFUSING = [
  'nt_place_order',
  'nt_place_oco_order',
  'nt_place_atm_order',
  'nt_compliance_report',
  'nt_deploy_strategy',
];

test('no tool advertises a default account — the class, not the four instances', () => {
  const offenders = TOOLS
    .filter((t) => t.inputSchema?.properties?.account)
    .filter((t) => 'default' in t.inputSchema.properties.account)
    .map((t) => `${t.name} (default: ${t.inputSchema.properties.account.default})`);

  assert.deepEqual(offenders, [],
    'A schema default for an account is a silently-supplied argument, not '
    + 'documentation. An MCP client may inject it, and the NT8 addon would then '
    + 'resolve a name the caller never sent (P1-90/P1-91).');
});

test('nor a default for any other field that decides where money goes', () => {
  // Widened deliberately. P1-91 was about `account`, but the defect class is
  // "a default on a field whose wrong value is a real trade", and the cheapest
  // time to pin the rest of the class is now.
  const DANGEROUS = ['account', 'action', 'quantity', 'price', 'limitPrice', 'stopPrice'];
  const offenders = [];
  for (const t of TOOLS) {
    const p = t.inputSchema?.properties ?? {};
    for (const field of DANGEROUS) {
      if (p[field] && 'default' in p[field]) {
        offenders.push(`${t.name}.${field} = ${p[field].default}`);
      }
    }
  }
  assert.deepEqual(offenders, [], 'No order-shaping field may carry a schema default');
});

test('every tool whose handler refuses a missing account declares it required', () => {
  for (const name of REFUSING) {
    assert.ok(byName.has(name), `${name} still exists`);
    assert.ok(props(name).account, `${name} still takes an account`);
    assert.ok(required(name).includes('account'),
      `${name} must declare account required: its NT8 handler refuses without one, `
      + 'so leaving it optional fails late and further from the cause');
  }
});

test('the tools that were already required stay required', () => {
  // Guards against a patch that rewrites a `required` array instead of appending
  // to it. Losing `idempotencyKey` from an order tool would let a duplicate order
  // through, which is a worse defect than the one being fixed.
  assert.ok(required('nt_place_order').includes('idempotencyKey'));
  assert.ok(required('nt_place_order').includes('symbol'));
  assert.ok(required('nt_place_atm_order').includes('idempotencyKey'));
  assert.ok(required('nt_deploy_strategy').includes('strategy'));
  assert.ok(required('nt_deploy_strategy').includes('instrument'));
  assert.ok(required('nt_place_oco_order').includes('limitPrice'));
  assert.ok(required('nt_place_oco_order').includes('stopPrice'));
});

test('account stays OPTIONAL where omitting it legitimately means all accounts', () => {
  // ⚠️ This test exists to stop the fix being over-applied, and it is as important
  // as the ones above. 14 tools take an account; only the five in REFUSING have a
  // handler that refuses without one. For a read like nt_orders or nt_fill_events,
  // omitting the account can reasonably mean "across all accounts" — making it
  // required would break a working call and would not fix anything.
  //
  // If one of these later grows a handler that refuses, move it into REFUSING
  // deliberately, with the handler change in the same commit.
  const OPTIONAL_BY_DESIGN = [
    'nt_orders',
    'nt_fill_events',
    'nt_trade_chart',
    'nt_riskguard_state',
    'nt_extract_trades',
    'nt_stop_strategy',
    'nt_set_strategy_param',
  ];
  for (const name of OPTIONAL_BY_DESIGN) {
    assert.ok(byName.has(name), `${name} still exists`);
    assert.ok(!required(name).includes('account'),
      `${name} must NOT require an account: omitting it means all accounts here`);
  }
});

test('the schemas are still structurally sound after any edit', () => {
  // Cheap structural floor. A patch that corrupts one schema object would
  // otherwise only show up as a tool silently vanishing from tools/list.
  assert.equal(TOOLS.length, 52, 'tool count unchanged');
  for (const t of TOOLS) {
    assert.equal(typeof t.name, 'string', 'every tool has a name');
    assert.ok(t.name.startsWith('nt_'), `${t.name} keeps the nt_ prefix`);
    assert.equal(typeof t.description, 'string', `${t.name} has a description`);
    assert.equal(t.inputSchema?.type, 'object', `${t.name} has an object inputSchema`);
    for (const r of t.inputSchema.required ?? []) {
      assert.ok(t.inputSchema.properties?.[r],
        `${t.name} requires "${r}", which must therefore be a declared property`);
    }
  }
});
