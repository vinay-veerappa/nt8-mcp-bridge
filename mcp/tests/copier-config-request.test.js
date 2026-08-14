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

import { buildCopierConfigRequest } from '../lib/copier-config-request.js';

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
