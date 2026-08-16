/**
 * Tests for the nt_copier_config request builder.
 *
 * Why this file exists: `nt_copier_config` was the only way to reach the copier's
 * ratio converter, and its argument surface could express four of the fields the
 * engine actually stores. Worse, two defects were found while widening it:
 *
 *   P1-72  the schema advertised action: 'quarantine' and NOTHING implemented it.
 *          It fell through the bridge's if-chain into the read branch and returned
 *          success: true. A misbehaving follower stayed live while the caller
 *          believed it was quarantined.
 *   P1-73  the schema declared defaults (quantityRatio 1.0, autoConversion true).
 *          TradeCopierEngine.ApplyRelationshipRequest MERGES -- an absent key keeps
 *          the stored value, a present key overwrites it -- so a caller nudging one
 *          field silently reset the other. That is the destructive save pattern
 *          slice 3b deleted from the bridge, re-entering through a tool schema.
 *
 * The builder is a separate module rather than a function inside nt-mcp-server.js
 * because importing that file starts its stdin readline loop and the test would
 * hang. It is also the same rule the bridge follows for ApplyRelationshipRequest:
 * put the mapping where an executed test can reach it.
 *
 * Run: `npm test` (from mcp/ninjatrader-mcp), or `node --test`.
 *
 * ⚠️ NOT `node --test tests/` -- on Node >= 22 the directory is resolved as a module
 * path and the run dies with MODULE_NOT_FOUND. That failure looks exactly like "my
 * new tests fail because the code does not exist yet", which is the evidence
 * test-first work depends on, and it cost a false red baseline once already.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { buildCopierConfigRequest } from '../lib/copier-config-request.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// ─── Reads ──────────────────────────────────────────────────────────────

test('get uses HTTP GET and carries no body', () => {
  const req = buildCopierConfigRequest({ action: 'get' });
  assert.equal(req.method, 'GET');
  assert.equal(req.path, '/api/copier/config');
  assert.equal(req.body, null, 'a read must not carry a body');
});

test('get is the default action', () => {
  assert.equal(buildCopierConfigRequest({}).method, 'GET');
  assert.equal(buildCopierConfigRequest({}).body, null);
});

test('get passes leaderAccount as a query parameter, not a body', () => {
  const req = buildCopierConfigRequest({ action: 'get', leaderAccount: 'Sim101' });
  assert.equal(req.method, 'GET');
  assert.equal(req.path, '/api/copier/config?leaderAccount=Sim101');
  assert.equal(req.body, null);
});

test('an account name with a space survives as a query parameter', () => {
  const req = buildCopierConfigRequest({ action: 'get', leaderAccount: 'Sim ORB' });
  assert.equal(req.path, '/api/copier/config?leaderAccount=Sim%20ORB');
});

test('get_groups is a read too', () => {
  const req = buildCopierConfigRequest({ action: 'get_groups' });
  assert.equal(req.method, 'GET');
  assert.equal(req.path, '/api/copier/config?action=get_groups');
  assert.equal(req.body, null);
});

// ─── P1-73: no key the caller did not supply ────────────────────────────

test('P1-73: set sends ONLY the keys supplied', () => {
  const req = buildCopierConfigRequest({
    action: 'set',
    leaderAccount: 'Sim101',
    followerAccount: 'Sim-ORB',
    quantityRatio: 2,
  });
  assert.equal(req.method, 'POST');
  assert.deepEqual(Object.keys(req.body).sort(),
    ['action', 'followerAccount', 'leaderAccount', 'quantityRatio']);
  assert.equal(req.body.quantityRatio, 2);
});

test('P1-73: set does NOT invent auto-conversion when it was not asked for', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', quantityRatio: 2,
  });
  assert.equal('autoSymbolConversion' in req.body, false,
    'the engine merges: a manufactured key overwrites the stored value');
  assert.equal('autoConversion' in req.body, false);
});

// ─── P1-74: the name the engine actually reads ──────────────────────────

test('P1-74: autoConversion is TRANSLATED to autoSymbolConversion', () => {
  // The engine's field is AutoSymbolConversion and `autoConversion` is not in its
  // ConfigAliasMap, so Json.NET dropped it as an unknown member. The tool argument
  // keeps its documented name; the wire key is the one that works. Pinned on the
  // engine side by TestP1_74_AutoConversionIsNotAFieldAndIsSilentlyDropped.
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', autoConversion: false,
  });
  assert.equal(req.body.autoSymbolConversion, false);
  assert.equal('autoConversion' in req.body, false,
    'sending the dead key too would be harmless but dishonest about what is read');
});

test('P1-74: autoSymbolConversion may be given directly', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', autoSymbolConversion: true,
  });
  assert.equal(req.body.autoSymbolConversion, true);
});

test('P1-74: the explicit spelling wins over the alias', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    autoConversion: true, autoSymbolConversion: false,
  });
  assert.equal(req.body.autoSymbolConversion, false);
});

test('P1-74: group writes translate it too', () => {
  const req = buildCopierConfigRequest({
    action: 'set_group', groupName: 'Micros', autoConversion: false,
  });
  assert.equal(req.body.autoSymbolConversion, false);
  assert.equal('autoConversion' in req.body, false);
});

test('P1-73: set does NOT invent quantityRatio when only auto-conversion was asked for', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', autoConversion: false,
  });
  assert.equal('quantityRatio' in req.body, false);
  assert.equal(req.body.autoSymbolConversion, false);
});

test('P1-73: an explicit false or 0 IS sent -- it is a value, not an absence', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    autoConversion: false, quantityRatio: 0, maxSlippageTicks: 0,
  });
  assert.equal(req.body.autoSymbolConversion, false);
  assert.equal(req.body.quantityRatio, 0);
  assert.equal(req.body.maxSlippageTicks, 0);
});

test('P1-73: a relationship write REFUSES to guess the follower', () => {
  // The engine defaults a missing followerAccount to "SimCopy2" and the leader to
  // "Sim101", both real accounts on this box. An underspecified write would edit a
  // live relationship silently.
  assert.throws(
    () => buildCopierConfigRequest({ action: 'set', leaderAccount: 'Sim101', quantityRatio: 2 }),
    /followerAccount/,
  );
  assert.throws(
    () => buildCopierConfigRequest({ action: 'set', followerAccount: 'Sim-ORB', quantityRatio: 2 }),
    /leaderAccount/,
  );
});

// ─── P1-72: quarantine has to actually do something ─────────────────────

test('P1-72: quarantine is a WRITE that sets isQuarantined', () => {
  const req = buildCopierConfigRequest({
    action: 'quarantine', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    quarantineReason: 'slippage',
  });
  assert.equal(req.method, 'POST');
  assert.equal(req.body.action, 'set', 'the bridge has no quarantine branch; set carries the field');
  assert.equal(req.body.isQuarantined, true);
  assert.equal(req.body.quarantineReason, 'slippage');
});

test('P1-72: unquarantine clears it', () => {
  const req = buildCopierConfigRequest({
    action: 'unquarantine', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
  });
  assert.equal(req.body.isQuarantined, false);
});

test('P1-72: quarantine does not silently degrade into a read', () => {
  const req = buildCopierConfigRequest({
    action: 'quarantine', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
  });
  assert.notEqual(req.method, 'GET', 'this is the whole defect: it used to read and report success');
});

test('P1-72: quarantine needs both accounts -- it must not guess', () => {
  assert.throws(
    () => buildCopierConfigRequest({ action: 'quarantine', leaderAccount: 'Sim101' }),
    /followerAccount/,
  );
});

// ─── An unknown action must not become a read ──────────────────────────

test('an unknown action THROWS rather than falling through to a read', () => {
  // This is the mechanism behind P1-72. The bridge's if-chain ends in `else {read}`,
  // so every unrecognised action returns success: true and changes nothing.
  assert.throws(() => buildCopierConfigRequest({ action: 'quarrantine' }), /unknown action/i);
  assert.throws(() => buildCopierConfigRequest({ action: 'enable' }), /unknown action/i);
});

test('the error names the actions that do exist', () => {
  assert.throws(() => buildCopierConfigRequest({ action: 'nope' }), /set_group/);
});

// ─── The fields that had no way in at all ──────────────────────────────

test('sizingMode reaches the body', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    sizingMode: 'PerTickerMatrix',
  });
  assert.equal(req.body.sizingMode, 'PerTickerMatrix');
});

test('perTickerRatios and customSymbolMappings reach the body as objects', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    perTickerRatios: { NQ: 2, ES: 1 },
    customSymbolMappings: { MNQ: 'NQ' },
  });
  assert.deepEqual(req.body.perTickerRatios, { NQ: 2, ES: 1 });
  assert.deepEqual(req.body.customSymbolMappings, { MNQ: 'NQ' });
});

test('maxSlippageTicks, dailyLossLimit and isEnabled reach the body', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    maxSlippageTicks: 8, dailyLossLimit: 500, isEnabled: true,
  });
  assert.equal(req.body.maxSlippageTicks, 8);
  assert.equal(req.body.dailyLossLimit, 500);
  assert.equal(req.body.isEnabled, true);
});

test('an empty perTickerRatios map is sent -- clearing the matrix is a real request', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', perTickerRatios: {},
  });
  assert.deepEqual(req.body.perTickerRatios, {});
});

// ─── Arming ────────────────────────────────────────────────────────────

test('armedForLive is passed through with confirmLive', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    armedForLive: true, confirmLive: true,
  });
  assert.equal(req.body.armedForLive, true);
  assert.equal(req.body.confirmLive, true);
});

test('arming without confirmLive is REFUSED here, not silently downgraded', () => {
  // The engine's ApplyArmingGate quietly sets armed=false without confirmLive. That
  // is correct for the engine -- but through a tool it reads as "armed it, and it
  // says armed: false", which is the P0-68 shape. Refuse at the boundary instead.
  assert.throws(
    () => buildCopierConfigRequest({
      action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', armedForLive: true,
    }),
    /confirmLive/,
  );
});

test('disarming needs no confirmation', () => {
  const req = buildCopierConfigRequest({
    action: 'set', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB', armedForLive: false,
  });
  assert.equal(req.body.armedForLive, false);
});

// ─── Group actions ─────────────────────────────────────────────────────

test('set_group posts the group payload', () => {
  const req = buildCopierConfigRequest({
    action: 'set_group', groupName: 'Micros', leaderAccount: 'Sim101',
    followerAccounts: ['Sim-ORB', 'SimCopy2'], quantityRatio: 1,
  });
  assert.equal(req.method, 'POST');
  assert.equal(req.body.action, 'set_group');
  assert.equal(req.body.groupName, 'Micros');
  assert.deepEqual(req.body.followerAccounts, ['Sim-ORB', 'SimCopy2']);
});

test('group actions require a groupName', () => {
  for (const action of ['set_group', 'remove_group', 'add_follower_to_group', 'remove_follower_from_group']) {
    assert.throws(() => buildCopierConfigRequest({ action, followerAccount: 'Sim-ORB' }),
      /groupName/, `${action} must require groupName`);
  }
});

test('add_follower_to_group requires the follower', () => {
  assert.throws(
    () => buildCopierConfigRequest({ action: 'add_follower_to_group', groupName: 'Micros' }),
    /followerAccount/,
  );
});

test('remove_group does not need a follower', () => {
  const req = buildCopierConfigRequest({ action: 'remove_group', groupName: 'Micros' });
  assert.equal(req.body.action, 'remove_group');
  assert.equal('followerAccount' in req.body, false);
});

// ─── remove ────────────────────────────────────────────────────────────

test('remove needs both accounts -- it deletes a relationship', () => {
  assert.throws(
    () => buildCopierConfigRequest({ action: 'remove', leaderAccount: 'Sim101' }),
    /followerAccount/,
  );
  const req = buildCopierConfigRequest({
    action: 'remove', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
  });
  assert.equal(req.body.action, 'remove');
});

// ─── The builder must not mutate its input ─────────────────────────────

test('the builder does not mutate the caller args', () => {
  const args = { action: 'quarantine', leaderAccount: 'Sim101', followerAccount: 'Sim-ORB' };
  const copy = JSON.parse(JSON.stringify(args));
  buildCopierConfigRequest(args);
  assert.deepEqual(args, copy, 'a mapping function that rewrites its input is a read that mutates');
});

// ─── P2-129: three lists, and the gate watched the two that agreed ──────

/**
 * P2-129. `set_mode` was in the tool SCHEMA (advertised) and in the addon's
 * `knownActions` (implemented) -- those two lists agreed exactly, 14 for 14, and a
 * test in tool-schema.test.js proved it in both directions. The builder in between,
 * which is the only one of the three that RUNS, named neither `set_mode` nor any set
 * containing it, so every call threw `unknown action 'set_mode'` before reaching the
 * bridge at all.
 *
 * Measured live 2026-08-16: `nt_copier_config action=set_mode copierMode=shadow` was
 * refused by the wrapper while `curl` against the same addon accepted it and changed
 * the mode. The copier's global gate -- the one that decides whether ANY copy is
 * submitted, and the one P1-125 had just made visible on the operator's page -- was
 * unreachable through the tool that advertises it.
 *
 * ⚠️ THE LESSON IS THE GATE'S REGION, NOT THE LINE. A schema/addon agreement test is
 * the right idea aimed at the wrong pair: it compares what is DECLARED at each end and
 * cannot see the translation between them. The test below drives the BUILDER, because
 * that is the artefact that decides.
 */
test('P2-129: every action the ADDON accepts can be built by this wrapper', () => {
  // Extracted from the addon source, never transcribed -- a hand-typed copy cannot
  // see the addon change, which is how P1-72 regressed twice.
  const src = fs.readFileSync(
    path.resolve(__dirname, '..', '..', 'addons', 'McpBridgeAddOn.cs'), 'utf8');
  const block = src
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n').map(l => { const i = l.indexOf('//'); return i >= 0 ? l.slice(0, i) : l; })
    .join('\n')
    .match(/knownActions\s*=\s*new\s+HashSet<string>\s*\([^)]*\)\s*\{([\s\S]*?)\}/);
  assert.ok(block, 'found the addon knownActions initializer -- without it this pin is a comment');

  const addonActions = [...block[1].matchAll(/"([^"]+)"/g)]
    .map(x => x[1]).filter(a => /^[a-z_]+$/.test(a));
  assert.ok(addonActions.length >= 14,
    `extracted a plausible whitelist (got ${addonActions.length})`);

  // Every argument any action might need, supplied at once: this test is about which
  // actions are REACHABLE, not about their argument rules, which are tested above.
  const everyArg = {
    leaderAccount: 'Sim101', followerAccount: 'Sim-ORB',
    groupName: 'G', followerAccounts: ['Sim-ORB'], copierMode: 'shadow',
  };

  const unreachable = [];
  for (const action of addonActions) {
    try {
      const req = buildCopierConfigRequest({ action, ...everyArg });
      // Reachable is not enough: what it SENDS must be an action the addon knows,
      // or the call is refused at the far end instead of at this one.
      const sent = req.method === 'GET'
        ? (new URL(req.path, 'http://x').searchParams.get('action') ?? 'get')
        : req.body.action;
      if (!addonActions.includes(sent)) unreachable.push(`${action} -> sends "${sent}"`);
    } catch (e) {
      unreachable.push(`${action} -> ${e.message.split('.')[0]}`);
    }
  }

  assert.deepEqual(unreachable, [],
    'every action the addon implements is reachable through the builder. Unreachable: '
    + unreachable.join('; '));
});

test('P2-129: set_mode is global -- it names no relationship and carries the mode', () => {
  const req = buildCopierConfigRequest({ action: 'set_mode', copierMode: 'shadow' });
  assert.equal(req.method, 'POST');
  assert.equal(req.body.action, 'set_mode');
  assert.equal(req.body.copierMode, 'shadow');

  // ⚠️ It must NOT be routed through the relationship branch. That branch requires a
  // leader and a follower, and demanding them here would name a scope this action does
  // not have: set_mode changes what EVERY relationship does.
  assert.ok(!('leaderAccount' in req.body),
    'a global mode change names no leader -- requiring one would be a scope this action lacks');
  assert.ok(!('followerAccount' in req.body), 'and no follower');

  // The mode itself is required: `set_mode` with nothing to set would reach the bridge
  // as a request to set the mode to nothing.
  assert.throws(() => buildCopierConfigRequest({ action: 'set_mode' }), /copierMode/);

  // ⚠️ And the VALUE is deliberately not validated here. The addon owns which modes
  // exist and fails closed on the rest; a second list in the wrapper is how P3-111's
  // hand-typed `period` enum came to forbid twelve values the addon serves.
  assert.doesNotThrow(
    () => buildCopierConfigRequest({ action: 'set_mode', copierMode: 'nonsense' }),
    'an unrecognised mode is the ADDON\'s refusal to make, not this file\'s');
});

test('P2-129: the refusal message lists every action the builder accepts', () => {
  // A refusal that names an incomplete menu sends the caller looking for another tool.
  // `set_mode` was missing from the accepted set AND from this message, for the same
  // reason and for the same length of time.
  let message = '';
  try { buildCopierConfigRequest({ action: 'no_such_action' }); } catch (e) { message = e.message; }
  assert.match(message, /unknown action/);
  for (const a of ['get', 'set', 'set_mode', 'set_group']) {
    assert.ok(message.includes(a), `the refusal names "${a}" as available`);
  }
});
