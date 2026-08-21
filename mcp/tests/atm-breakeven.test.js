/**
 * Tests for P2-154's breakeven-pair validator.
 *
 * `nt_place_atm_order` accepted a breakeven pair (offset >= trigger) the addon would then
 * refuse, so the caller learned at placement, after a round-trip. This validator refuses the
 * explicit conflict at the tool boundary. The discriminators are the NEGATIVE cases: a
 * validator that refused everything would pass every "refused" assertion, so the "allowed"
 * cases -- a valid pair, and any input with a value missing -- are what prove it is not
 * over-restricting the way P3-111's enum did. [[detector-needs-a-negative-test]]
 *
 * Run: `node --test` from mcp/.  ⚠️ NOT `node --test tests/` -- on Node >= 22 that resolves
 * the directory as a module and fails MODULE_NOT_FOUND, which reads like a test failure.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { validateBreakevenPair } from '../lib/atm-breakeven.js';

test('an explicit offset >= trigger pair is refused, naming both values', () => {
  const r = validateBreakevenPair({ breakevenOffsetTicks: 15, breakevenTriggerTicks: 12 });
  assert.ok(r, 'a conflicting pair must be refused');
  assert.match(r, /15/, 'the refusal names the offset');
  assert.match(r, /12/, 'the refusal names the trigger');
  assert.match(r, /breakevenOffsetTicks/, 'and names the field');
});

test('offset EQUAL to trigger is refused -- the boundary is inclusive, like the core', () => {
  // ValidateBreakevenPlacement uses `>=`: a stop exactly at the trigger rests on the market.
  assert.ok(validateBreakevenPair({ breakevenOffsetTicks: 12, breakevenTriggerTicks: 12 }));
});

test('a valid pair (offset < trigger) is allowed', () => {
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: 2, breakevenTriggerTicks: 12 }), null);
});

test('a missing HALF is not judged here -- that is the addon\'s, with its own defaults', () => {
  // The whole point of firing only on an EXPLICIT pair: never assume a default and thereby
  // refuse something the addon would accept (P3-111's false-refusal class).
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: 15 }), null,
    'offset alone: the trigger default is the addon\'s business');
  assert.equal(validateBreakevenPair({ breakevenTriggerTicks: 2 }), null,
    'trigger alone: the offset default is the addon\'s business');
  assert.equal(validateBreakevenPair({}), null, 'neither supplied: nothing to judge');
});

test('a zero is a value, not an absence', () => {
  // offset 0 with trigger 0 is a conflict (0 >= 0); offset 0 with trigger 5 is valid.
  assert.ok(validateBreakevenPair({ breakevenOffsetTicks: 0, breakevenTriggerTicks: 0 }));
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: 0, breakevenTriggerTicks: 5 }), null);
});

test('non-numeric junk is treated as absent, not coerced', () => {
  // Number('') is 0 and Number('x') is NaN; both must read as "not supplied" so a typo does
  // not accidentally manufacture a comparison.
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: 'x', breakevenTriggerTicks: 12 }), null);
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: '', breakevenTriggerTicks: 12 }), null);
});

test('string-encoded numbers still compare (a client may send them as text)', () => {
  assert.ok(validateBreakevenPair({ breakevenOffsetTicks: '15', breakevenTriggerTicks: '12' }));
  assert.equal(validateBreakevenPair({ breakevenOffsetTicks: '2', breakevenTriggerTicks: '12' }), null);
});
