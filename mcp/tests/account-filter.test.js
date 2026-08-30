/**
 * Tests for lib/account-filter.js — the client-side account filter for nt_accounts
 * and nt_positions.
 *
 * P1-90 on a read path. The addon's GetAccountInfo / GetPositions take no params, so
 * an account filter cannot be pushed server-side. The wrapper filters after the fetch.
 *
 * ⚠️ The two tools get DIFFERENT semantics, and that is the point of these tests:
 *   - filterAccounts: rows ARE accounts → empty match = account does not exist → REFUSE
 *   - filterPositions: rows are positions → empty match = no open positions → return []
 *
 * The first draft used one function for both and turned "Sim101 has no positions" into
 * "Sim101 does not exist" — reassurance about the wrong thing.
 */
import test from 'node:test';
import assert from 'node:assert/strict';
import { filterAccounts, filterPositions } from '../lib/account-filter.js';

const ACCOUNTS = [
  { name: 'Sim101', cashValue: 100000 },
  { name: 'Sim102', cashValue: 50000 },
  { name: 'LiveProd', cashValue: 250000 },
];

const POSITIONS = [
  { account: 'Sim101', symbol: 'NQ 09-26', quantity: 1 },
  { account: 'Sim101', symbol: 'ES 09-26', quantity: 2 },
  { account: 'LiveProd', symbol: 'CL 10-26', quantity: -1 },
];

// ─── filterAccounts (rows ARE accounts — empty match is a refusal) ──────────

test('filterAccounts: no account supplied returns all rows', () => {
  assert.equal(filterAccounts(ACCOUNTS, undefined).length, 3);
  assert.equal(filterAccounts(ACCOUNTS, '').length, 3);
  assert.equal(filterAccounts(ACCOUNTS, '   ').length, 3);
});

test('filterAccounts: a matching name returns only its row', () => {
  const out = filterAccounts(ACCOUNTS, 'Sim101');
  assert.equal(out.length, 1);
  assert.equal(out[0].name, 'Sim101');
});

test('filterAccounts: match is case-insensitive and trims whitespace', () => {
  const out = filterAccounts(ACCOUNTS, '  sim101  ');
  assert.equal(out.length, 1);
  assert.equal(out[0].name, 'Sim101');
});

test('filterAccounts: a name that matches nothing is REFUSED', () => {
  const out = filterAccounts(ACCOUNTS, 'NoSuchAccount');
  assert.ok(out.error, 'a refusal object, not an empty array');
  assert.match(out.error, /NoSuchAccount/);
  assert.match(out.error, /Refusing/);
  assert.ok(Array.isArray(out.availableSample));
  assert.ok(out.availableSample.includes('Sim101'));
});

test('filterAccounts: refusal reports the real count of available accounts', () => {
  assert.match(filterAccounts(ACCOUNTS, 'Ghost').error, /3 available/);
});

test('filterAccounts: availableSample is capped at 10', () => {
  const big = Array.from({ length: 200 }, (_, i) => ({ name: `Acct${i}` }));
  const out = filterAccounts(big, 'Ghost');
  assert.ok(out.availableSample.length <= 10);
});

test('filterAccounts: an empty book with a name supplied is a refusal', () => {
  const out = filterAccounts([], 'Sim101');
  assert.ok(out.error);
});

// ─── filterPositions (rows are POSITIONS — empty match is a valid answer) ───

test('filterPositions: no account supplied returns all rows', () => {
  assert.equal(filterPositions(POSITIONS, undefined).length, 3);
  assert.equal(filterPositions(POSITIONS, '').length, 3);
});

test('filterPositions: a matching account returns only its positions', () => {
  const out = filterPositions(POSITIONS, 'Sim101');
  assert.equal(out.length, 2);
  assert.ok(out.every(p => p.account === 'Sim101'));
});

test('filterPositions: match is case-insensitive and trims whitespace', () => {
  const out = filterPositions(POSITIONS, '  liveprod  ');
  assert.equal(out.length, 1);
  assert.equal(out[0].account, 'LiveProd');
});

test('filterPositions: an account with NO positions returns an empty array, NOT a refusal', () => {
  // Sim102 exists (it is in ACCOUNTS) but has no positions. This must be [], not an error.
  // The first draft of this function returned a refusal here, which read as "Sim102 does
  // not exist" — reassurance about the wrong thing.
  const out = filterPositions(POSITIONS, 'Sim102');
  assert.ok(Array.isArray(out), 'an array, not a refusal object');
  assert.equal(out.length, 0, 'Sim102 has no positions, and that is a valid answer');
});

test('filterPositions: a name that matches nothing also returns an empty array', () => {
  // filterPositions cannot know whether the account exists — it only sees position rows.
  // An empty array is the honest answer: "no positions for this account". The caller can
  // cross-check with nt_accounts if they need to know whether the account exists at all.
  const out = filterPositions(POSITIONS, 'GhostAccount');
  assert.ok(Array.isArray(out));
  assert.equal(out.length, 0);
});

test('filterPositions: a malformed row (missing account field) is skipped', () => {
  const rows = [{ symbol: 'NQ' }, { account: 'Sim101', symbol: 'ES' }];
  const out = filterPositions(rows, 'Sim101');
  assert.equal(out.length, 1);
});