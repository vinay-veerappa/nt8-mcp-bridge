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
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { TOOLS } from '../lib/tools.js';

// The addon source, anchored on THIS file's location -- not the cwd -- so the
// test behaves the same however the runner is launched. Same approach as
// BridgeSourceTests.cs in this repo: the test and the source it pins are two
// halves of one contract, and a path that depends on where you stood when you
// ran it is a path that silently stops loading.
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ADDON_SOURCE = path.resolve(__dirname, '..', '..', 'addons', 'McpBridgeAddOn.cs');

// Extract the real `knownActions` whitelist from the addon source. The addon is
// the authority: it decides what it accepts, and the wrapper's job is to advertise
// exactly that. A hand-transcribed copy of the list (which is what this test used
// to be) catches the wrapper drifting from a list that was true when someone typed
// it, and CANNOT see the addon change -- which is how P1-72 regressed.
//
// The whitelist is a HashSet initializer in CopierConfig():
//
//     var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
//     {
//         "get", "get_groups",
//         "set", "update",
//         ...
//         "set_mode"
//     };
//
// We strip comments first (the doc comments quote the defective pattern verbatim,
// and a check that forbids describing the bug it prevents gets the comment deleted
// instead), then match the string literals inside the HashSet initializer.
function stripComments(src) {
  // Block comments.
  src = src.replace(/\/\*[\s\S]*?\*\//g, '');
  // Line comments (naive -- does not handle // inside strings, but the whitelist
  // initializer contains none, and this is the same rule BridgeSourceTests.cs uses).
  return src.split('\n').map(l => {
    const i = l.indexOf('//');
    return i >= 0 ? l.substring(0, i) : l;
  }).join('\n');
}

function extractKnownActions(src) {
  const code = stripComments(src);
  // Match the HashSet<string> initializer block. The `var knownActions = new
  // HashSet<string>(...) { ... };` block is the only one of this shape in the file.
  const m = code.match(/knownActions\s*=\s*new\s+HashSet<string>\s*\([^)]*\)\s*\{([\s\S]*?)\}/);
  assert.ok(m, 'found the knownActions HashSet initializer in McpBridgeAddOn.cs');
  // Extract all string literals from the initializer body.
  const actions = [...m[1].matchAll(/"([^"]+)"/g)].map(x => x[1]);
  // set_mode has a comment above it; the comment is stripped, so the literal is
  // all that remains. Filter out anything that is not a lowercase action name --
  // the HashSet is OrdinalIgnoreCase so the addon stores them lowercase.
  return new Set(actions.filter(a => /^[a-z_]+$/.test(a)));
}

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

test('no order-shaping quantity or price carries a default', () => {
  // A quantity or a price the caller never sent is a different trade from the one
  // they asked for. None exist today; this is a floor, not a fix.
  const offenders = [];
  for (const t of TOOLS) {
    const p = t.inputSchema?.properties ?? {};
    for (const field of ['quantity', 'price', 'limitPrice', 'stopPrice']) {
      if (p[field] && 'default' in p[field]) {
        offenders.push(`${t.name}.${field} = ${p[field].default}`);
      }
    }
  }
  assert.deepEqual(offenders, [], 'No quantity or price may carry a schema default');
});

test('a defaulted `action` must itself be a READ', () => {
  // ⚠️ THIS TEST WAS WRONG ON ITS FIRST DRAFT, and the wrong version is worth
  // recording. It forbade a `default` on `action` outright, which would have made
  // the implementer delete two SAFE defaults to go green:
  //
  //   nt_prop_limits.action        default 'get'   enum [get, set]
  //   nt_trade_journal.action      default 'list'  enum [list, add, tag, export]
  //
  // Defaulting to the read is the correct direction — it is the fail-closed
  // choice, and forbidding it would have made the tool worse to satisfy a test.
  // That is the "a too-broad test gets the CODE broken to satisfy it" failure.
  //
  // The real rule is about which way the default falls. An omitted `action` may
  // resolve to a read; it may never resolve to something that moves a position:
  //
  //   nt_alert.action                        default 'webhook'     enum includes 'flatten'
  //   nt_multi_account_orchestrator.action   default 'sync_hedge'  enum includes 'group_flatten'
  //
  // `sync_hedge` adjusts positions across accounts. An omitted action doing that
  // is P1-90's class: something consequential happening that the caller did not
  // name.
  const READ_ACTIONS = new Set([
    'get', 'list', 'status', 'read', 'export', 'report', 'summary',
  ]);
  const offenders = [];
  for (const t of TOOLS) {
    const action = t.inputSchema?.properties?.action;
    if (!action || !('default' in action)) continue;
    if (!READ_ACTIONS.has(String(action.default))) {
      offenders.push(`${t.name}.action = ${action.default} (enum: ${(action.enum || []).join('|')})`);
    }
  }
  assert.deepEqual(offenders, [],
    'An omitted `action` may fall through to a read, never to something that '
    + 'moves a position. Either drop the default or make the default the read.');
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
    'nt_chart',
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
  // 52 -> 54 in P2-103, which added nt_riskguard_inventory and nt_copier_snapshot: the two
  // read-only surfaces that answer "is the guard actually protecting me", which five of the
  // core's mutation batteries exist to keep honest and which no tool could reach.
  //
  // ⚠️ Bumping this is meant to cost a moment's thought. It is the third exact-count gate to
  // fire in one session (the addon's ResolveOrRefuse site count, twice) and each time it made
  // the author state that the addition was deliberate. The `>= N` version of this assertion
  // let a mutant survive earlier today.
  //
  // 56 -> 53: F-18 consolidated the three chart-capture tools (nt_capture_chart,
  // nt_chart_snapshot, nt_trade_chart) into one nt_chart with a mode enum, and retired the
  // permanent NOT_IMPLEMENTED stub nt_script_execute. Net -4 removed, +1 added.
  //
  // 53 -> 53: review pass retired the nt_subscribe stub (it returned fabricated URLs and
  // subscribed to nothing) and added nt_charts (GET /api/chart/list existed with no tool),
  // re-exposing chart discovery. Net -1 removed, +1 added.
  //
  // 53 -> 59: F-19/20/21/22 pass — export lifecycle (nt_list_exports, nt_delete_export),
  // indicator authoring parity (nt_list_indicators, nt_indicator_source, nt_create_indicator),
  // and nt_events_since (the honest successor to the retired nt_subscribe stub: a stateless
  // audit-tail poll, not a fabricated subscription). Six added, none removed.
  //
  // 59 -> 60: nt_optimize_strategy added for NT8 Strategy Analyzer optimization
  assert.equal(TOOLS.length, 60, 'tool count unchanged');
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

/**
 * P1-72, REGRESSED — measured on the live box 2026-08-13, not inferred.
 *
 * `P1-72` was "nt_copier_config advertised a quarantine action that nothing
 * implemented", closed 2026-08-13. The enum still lists `quarantine` and
 * `unquarantine`, and the addon still refuses both:
 *
 *     POST /api/copier/config {"action":"quarantine",...}
 *       -> {"success":false,"error":"UNKNOWN_COPIER_ACTION"}
 *
 * It fails CLOSED and loudly, because `P1-88` made an unrecognised action a
 * refusal rather than a silent read — so this is a contract defect, not a
 * dangerous one. But the contract is what a model reads to decide what to send,
 * and the enum is the only description of this surface it ever sees.
 *
 * The second half is worse than the first: the field that ACTUALLY releases a
 * quarantine — `isQuarantined`, sent with `action: "set"`, which is what the
 * browser page posts — was not in the schema at all. So the wrapper advertised
 * two ways to do it that do not work, and omitted the one that does.
 */
test('P1-72: the copier action enum names only actions the addon accepts', () => {
  const action = props('nt_copier_config').action;
  assert.ok(Array.isArray(action?.enum), 'nt_copier_config still declares an action enum');

  // The addon's REAL whitelist, extracted from addons/McpBridgeAddOn.cs -- not a
  // hand-transcribed copy. This is the point of the merge: the wrapper and the
  // addon are two halves of one contract, and a contract with its two sides in
  // two repos cannot be pinned in one commit. The old test compared against a
  // transcription with a comment naming where it came from; it caught the wrapper
  // drifting from a list that was true when someone typed it, and CANNOT see the
  // addon change. That is this project's recurring defect: a comment standing in
  // for a gate.
  assert.ok(fs.existsSync(ADDON_SOURCE),
    `the addon source is readable at ${ADDON_SOURCE} -- without it the pin is a comment`);
  const ADDON_ACCEPTS = extractKnownActions(fs.readFileSync(ADDON_SOURCE, 'utf8'));
  assert.ok(ADDON_ACCEPTS.size > 0,
    'extracted a non-empty action whitelist from the addon source');

  for (const a of action.enum) {
    assert.ok(ADDON_ACCEPTS.has(a),
      `action "${a}" is advertised but the addon answers UNKNOWN_COPIER_ACTION for it`);
  }

  // The reverse direction: an action the addon accepts but the wrapper does not
  // advertise is a feature the model cannot reach. Both directions are drift.
  const undeclared = [...ADDON_ACCEPTS].filter(a => !action.enum.includes(a));
  assert.deepEqual(undeclared, [],
    `the wrapper advertises every addon action (undeclared: ${undeclared.join(', ')})`);

  assert.ok(!action.enum.includes('quarantine'),
    'quarantine is not an addon action -- P1-72, and it came back');
  assert.ok(!action.enum.includes('unquarantine'),
    'unquarantine is not an addon action either');
});

test('P1-72: the field that really releases a quarantine is declared', () => {
  const p = props('nt_copier_config');
  assert.ok(p.isQuarantined, 'isQuarantined is a declared property');
  assert.equal(p.isQuarantined.type, 'boolean');
  assert.ok(!('default' in p.isQuarantined),
    'and carries NO default -- the engine merges, so a materialised default would '
    + 'quarantine or release a relationship nobody asked to change (P1-73)');
});

/**
 * P3-34. The copier's global live/shadow/disabled mode. It shipped in core
 * v1.15.0 settable only by editing copier_config.json, and the bridge gained
 * `set_mode` for it — but a wrapper that does not name the action cannot reach it.
 */
test('P3-34: the copier mode is reachable and unambiguous', () => {
  const p = props('nt_copier_config');

  assert.ok(p.action.enum.includes('set_mode'), 'set_mode is advertised');

  assert.ok(p.copierMode, 'copierMode is a declared property');
  assert.deepEqual(p.copierMode.enum, ['live', 'shadow', 'disabled'],
    'and offers exactly the modes the addon recognises -- anything else is refused, '
    + 'because the gate fails closed');
  assert.ok(!('default' in p.copierMode),
    'with no default: a client that materialised one would change how every copy '
    + 'behaves, on a read (P1-73)');

  // `mode` already existed on this tool meaning the copy TRIGGER source. Two
  // fields called mode-something, with different meanings, on one tool, is how a
  // wrong write gets sent confidently.
  assert.deepEqual(p.mode.enum, ['Executions', 'Orders'],
    'the pre-existing `mode` still means the copy trigger source');
  assert.ok(/trigger/i.test(p.mode.description),
    '`mode` says so in its description');
  assert.ok(/not the global|copierMode/i.test(p.mode.description),
    'and distinguishes itself from copierMode, because the two are one letter apart '
    + 'in intent and worlds apart in effect');
});

// P1-102. `nt_lockout` reaches `POST /api/lockout`, a route that existed for months with NO
// tool calling it -- recovering a locked-out account meant a raw curl with the bridge token
// read off disk.
//
// ⚠️ The enum is PINNED TO THE ADDON, not hand-typed, for the reason the copier's is: `P1-72`
// drifted TWICE, and both times the schema advertised actions the receiver answered UNKNOWN_ to.
// A hand-written list is true when someone types it and cannot see the addon change.
function extractLockoutActions(src) {
  const code = stripComments(src);
  // `internal static readonly string[] LockoutActions = { "status", "unlock", ... };`
  const m = code.match(/LockoutActions\s*=\s*\{([^}]*)\}/);
  assert.ok(m, 'found the LockoutActions array in McpBridgeAddOn.cs');
  return [...m[1].matchAll(/"([^"]+)"/g)].map(x => x[1]);
}

test('P1-102: nt_lockout exists and its actions are pinned to the addon whitelist', () => {
  const tool = TOOLS.find(t => t.name === 'nt_lockout');
  assert.ok(tool, 'nt_lockout is declared -- the route had no tool at all before P1-102');

  const addonActions = extractLockoutActions(fs.readFileSync(ADDON_SOURCE, 'utf8'));
  const p = tool.inputSchema.properties;

  assert.deepEqual([...p.action.enum].sort(), [...addonActions].sort(),
    'the advertised actions are EXACTLY what the addon accepts. Advertising one it refuses is '
    + 'P1-72; omitting one it accepts hides a capability');

  // ⚠️ The specific action a caller is most likely to try, and the one that must NOT appear.
  assert.ok(!p.action.enum.includes('lock'),
    'there is no "lock" action -- the addon does not implement it. Before P1-102 it answered '
    + '{success:true, isLockedOut:false}, a report that contradicts itself and still says success');
  assert.ok(!addonActions.includes('lock'),
    'and the addon still does not implement it, so this test fails the day someone adds one '
    + 'side without the other');
});

test('P1-102: unlock REMOVES protection, so nothing about it may be defaulted', () => {
  const tool = TOOLS.find(t => t.name === 'nt_lockout');
  const p = tool.inputSchema.properties;

  // P1-90 recorded that HandleLockout fed a GUESSED account name straight into UnlockAccount
  // with no existence check -- omitting the field unlocked Sim101, and a typo returned
  // success:true for an account that does not exist. The resolver fixed the addon; a
  // permissive schema would re-open it from the other side.
  assert.ok(!('default' in p.account),
    'account carries NO default -- a defaulted account on a write that removes protection is '
    + 'exactly how P1-90 unlocked Sim101 by omission');
  assert.ok(!('default' in p.action),
    'and neither does action, so "I sent nothing" cannot become "I cleared a lockout"');

  assert.ok(tool.inputSchema.required.includes('account'),
    'account is REQUIRED');
  assert.ok(tool.inputSchema.required.includes('action'),
    'and so is action -- this tool can remove protection, so the caller states intent');

  assert.ok(/remove/i.test(tool.description) || /REMOVE/.test(tool.description),
    'the description says plainly that it removes protection');
});

// ─────────────────────────────────────────────────────────────────────────────
// F-16: the JOIN between what is ADVERTISED and what is DISPATCHED.
//
// Every test above this line asks whether a schema is right. None of them asks
// whether the tool it describes is REACHABLE. Those are two files -- `lib/tools.js`
// advertises, `nt-mcp-server.js` dispatches -- and until now nothing compared them.
//
// That is `P2-109`'s exact shape at a new site. There, `tools.js` advertised
// `account`, `limit` and `offset`, `nt-mcp-server.js` SENT all three, `GetOrders()`
// was a clean read, and the line between them was `case "/api/orders": return
// GetOrders();`, taking nothing. Every component was individually correct and
// nothing reviewable in isolation was wrong.
//
// ⚠️ THE TWO DIRECTIONS FAIL DIFFERENTLY, and only one of them is loud:
//
//   advertised, not dispatched -> the client offers a tool, the call reaches the
//     dispatcher's default branch, and the caller gets an error. Annoying, visible.
//
//   dispatched, not advertised -> THE TOOL IS INVISIBLE. It cannot be called by any
//     client that reads tools/list, and nothing anywhere reports a problem. This is
//     `P1-102` verbatim: /api/lockout existed on the addon for months with no tool in
//     front of it, and `P2-103`'s two inventory surfaces had five mutation batteries
//     keeping their payloads honest while no tool could reach them. The honesty was
//     bought and was not being spent.
//
// Weigh the quiet failure above the loud one -- so this asserts BOTH directions, and
// names which is which in the message.
test('F-16: every advertised tool is dispatched, and every dispatched tool is advertised', () => {
  const serverPath = path.resolve(__dirname, '..', 'nt-mcp-server.js');
  const src = fs.readFileSync(serverPath, 'utf8');

  // The dispatcher is one switch over the tool name. Read the case labels rather
  // than importing the module: importing nt-mcp-server.js starts its stdin loop and
  // hangs the test run, which is the reason lib/tools.js was extracted in the first
  // place (F-16's own note said extraction had to come first -- it has).
  const dispatched = new Set(
    [...src.matchAll(/case\s+'(nt_[a-z0-9_]+)'\s*:/g)].map(m => m[1]));
  const advertised = new Set(TOOLS.map(t => t.name));

  // Positive control on the REGION, not on the code under test. If the regex ever
  // stops matching -- a reformat, a switch replaced by a lookup table -- both
  // difference sets go empty and this test passes while inspecting nothing. Five
  // gates in the sibling repo have been caught exactly that way.
  assert.ok(dispatched.size > 50,
    `the dispatcher's case labels are still readable (found ${dispatched.size}); `
    + 'if this drops, the regex has stopped matching and the two assertions below '
    + 'are comparing against an empty set rather than proving anything');

  const advertisedNotDispatched = [...advertised].filter(n => !dispatched.has(n)).sort();
  const dispatchedNotAdvertised = [...dispatched].filter(n => !advertised.has(n)).sort();

  assert.deepEqual(advertisedNotDispatched, [],
    'these tools are advertised to the client and reach no handler: '
    + advertisedNotDispatched.join(', '));

  assert.deepEqual(dispatchedNotAdvertised, [],
    'these tools are IMPLEMENTED AND INVISIBLE -- no client that reads tools/list can '
    + 'call them, and nothing else reports it (P1-102, P2-103): '
    + dispatchedNotAdvertised.join(', '));

  // And the two counts agree with the exact-count gate above, so a tool added to both
  // sides still has to be stated deliberately in one place.
  assert.equal(dispatched.size, advertised.size,
    'the dispatcher and the tool list are the same size');
});

// ─────────────────────────────────────────────────────────────────────────────
// Review pass: the schema is a contract, and four clauses of it were false.
//
//   * nt_subscribe was a STUB: it returned fabricated URLs ("status: subscribed")
//     and subscribed to nothing — P1-72's exact shape, a tool that advertises and
//     does not implement. Retired rather than implemented: the SSE endpoint is a
//     long-lived connection an MCP tool call (one request, one response) cannot
//     model.
//
//   * nt_orders and nt_fill_events advertise limit/offset/count bounds in prose
//     ("max 5,000 rows" already burned this repo once — P3-111). The addon clamps;
//     the schema now says so with minimum/maximum instead of hoping the caller
//     reads the addon source.
//
//   * nt_charts: GET /api/chart/list shipped in the chart-discovery release with
//     no tool in front of it, which is P1-102's shape — implemented and invisible.
// ─────────────────────────────────────────────────────────────────────────────
test('the nt_subscribe stub is gone, not silently still advertised', () => {
  // A stub that answers "subscribed" without subscribing is worse than no tool:
  // it is a green light for a capability that does not exist.
  assert.ok(!byName.has('nt_subscribe'), 'nt_subscribe must not come back without a real SSE client');
});

test('nt_charts is advertised AND dispatched (GET /api/chart/list)', () => {
  const tool = byName.get('nt_charts');
  assert.ok(tool, 'nt_charts exists in the advertised list');
  const serverSrc = fs.readFileSync(path.resolve(__dirname, '..', 'nt-mcp-server.js'), 'utf8');
  assert.match(serverSrc, /case\s+'nt_charts'\s*:/, 'nt_charts has a dispatch case');
  // And the endpoint it names is real: the route literal must exist in the addon.
  const addonSrc = fs.readFileSync(ADDON_SOURCE, 'utf8');
  assert.match(addonSrc, /case\s+"\/api\/chart\/list"/,
    'GET /api/chart/list exists in the addon, so the tool cannot drift into a 404');
});

test('pagination bounds advertised by nt_orders match the addon clamp (1..500)', () => {
  const p = props('nt_orders');
  assert.equal(p.limit.minimum, 1, 'limit minimum matches the addon clamp floor');
  assert.equal(p.limit.maximum, 500, 'limit maximum matches BridgeOrderQuery.MaxLimit');
  assert.ok(p.offset.minimum === undefined || p.offset.minimum >= 0, 'offset is non-negative');
});

test('pagination bounds advertised by nt_fill_events match the addon clamp (1..1000)', () => {
  const p = props('nt_fill_events');
  assert.equal(p.count.minimum, 1);
  assert.equal(p.count.maximum, 1000);
  assert.ok(p.offset.minimum === undefined || p.offset.minimum >= 0, 'offset is non-negative');
});

// ─────────────────────────────────────────────────────────────────────────────
// F-19 / F-20 / F-21 / F-22: the review's four deferred items.
//
// Each schema gate pins the SAME property the C# side implements, because the
// wrapper-addon contract has burned this repo when its two halves lived apart
// (P1-72 enum drift, P2-109 dropped params, the OCO limitPrice/targetPrice split).
// ─────────────────────────────────────────────────────────────────────────────
test('F-22: nt_events_since requires since, names no hub, and is honest about polling', () => {
  const tool = byName.get('nt_events_since');
  assert.ok(tool, 'nt_events_since exists');
  assert.ok(required('nt_events_since').includes('since'),
    'since is required — an omitted instant has no safe meaning for "events since"');
  assert.match(tool.description, /poll/i,
    'the description says POLL — the retired nt_subscribe stub is not coming back as a '
    + 'tool that silently claims to stream');
  assert.ok(!('default' in tool.inputSchema.properties),
    'no fabricated defaults: nt_subscribe returned hubUrl defaults for a hub that does not exist');
  // And the addon route it names is real.
  const addonSrc = fs.readFileSync(ADDON_SOURCE, 'utf8');
  assert.match(addonSrc, /case\s+"\/api\/events\/since"/, '/api/events/since exists in the addon');
});

test('F-19: export lifecycle tools exist and delete is gated to mcp_*.csv', () => {
  assert.ok(byName.get('nt_list_exports'), 'nt_list_exports exists');
  const del = byName.get('nt_delete_export');
  assert.ok(del, 'nt_delete_export exists');
  assert.ok(required('nt_delete_export').includes('name'), 'delete requires a name');
  assert.match(del.description, /mcp_\*\.csv/i,
    'the description states the same gate the addon enforces (mcp_ prefix, .csv)');
  const addonSrc = fs.readFileSync(ADDON_SOURCE, 'utf8');
  assert.match(addonSrc, /case\s+"\/api\/exports"/, 'GET /api/exports exists in the addon');
  assert.match(addonSrc, /case\s+"\/api\/exports\/delete"/, 'POST /api/exports/delete exists in the addon');
});

test('F-20: indicator authoring parity with the strategy trio', () => {
  for (const name of ['nt_list_indicators', 'nt_indicator_source', 'nt_create_indicator']) {
    assert.ok(byName.get(name), `${name} exists`);
  }
  assert.ok(required('nt_indicator_source').includes('name'), 'source read requires a name');
  assert.deepEqual(required('nt_create_indicator'), ['name', 'source'],
    'create requires name+source, exactly like nt_create_strategy');
  const addonSrc = fs.readFileSync(ADDON_SOURCE, 'utf8');
  for (const route of ['/api/indicators', '/api/indicator/source', '/api/indicator/create']) {
    assert.match(addonSrc, new RegExp(`case\\s+"${route.replace(/\//g, '\\/')}"`),
      `${route} exists in the addon`);
  }
  // And the indicator path gate mirrors the strategy one — a weaker second gate would
  // make the read a traversal hole SafeStrategyPath closed.
  assert.match(addonSrc, /SafeIndicatorPath/,
    'the addon routes indicator paths through a Safe path gate, not hand-rolled concat');
});

test('F-21: nt_bars format is opt-in and rows stays the default', () => {
  const p = props('nt_bars');
  assert.ok(p.format, 'format is declared');
  assert.deepEqual(p.format.enum, ['rows', 'columnar'],
    'format is a closed enum — an unexpected value would be a silent shape change');
  assert.equal(p.format.default, 'rows',
    'rows is the default: the response shape a caller gets without asking must not have changed');
});

test('nt_optimize_strategy declares required fields and enum choices', () => {
  const t = byName.get('nt_optimize_strategy');
  assert.ok(t, 'nt_optimize_strategy is declared');
  assert.deepEqual(required('nt_optimize_strategy'), ['strategy', 'symbol', 'paramRanges']);
  const p = props('nt_optimize_strategy');
  assert.ok(p.strategy && p.symbol && p.paramRanges, 'declares core optimization properties');
  assert.ok(p.optimizer.enum.includes('DefaultOptimizer'), 'includes DefaultOptimizer');
  assert.ok(p.optimizer.enum.includes('GeneticOptimizer'), 'includes GeneticOptimizer');
  assert.ok(p.fitness.enum.includes('MaxProfitFactor'), 'includes MaxProfitFactor');
  assert.ok(p.generations && p.generationSize, 'declares genetic parameters');
});
