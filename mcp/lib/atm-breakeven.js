/**
 * P2-154. `nt_place_atm_order` accepted a breakeven pair the addon would then refuse,
 * so the operator (frequently an agent) learned at PLACEMENT, after a round-trip, rather
 * than at the schema. This refuses the obvious conflict at the tool boundary instead.
 *
 * A breakeven stop is placed `breakevenOffsetTicks` past entry once price runs
 * `breakevenTriggerTicks` in favour. If the offset is AT or PAST the trigger, the stop
 * would rest at or inside the market -- not a valid breakeven. The CANONICAL rule, and the
 * authority, is `DynamicAtmManager.ValidateBreakevenPlacement` in the vendored core
 * (`offset >= trigger` -> refuse), which `PlaceBracket` calls before creating anything.
 * That refusal STAYS: the bridge is one of several callers of `/api/order/atm`, and a
 * check that lived only here would be absent for every other path. This is additive, a
 * fast-fail so the caller does not have to round-trip to be told no. [[an-alarm-wired-to-a-dead-output]]
 *
 * ⚠️ WHY THIS ONLY FIRES WHEN BOTH VALUES ARE EXPLICITLY SUPPLIED, and never assumes the
 * addon's defaults (trigger 12, offset 2). P3-111's lesson, verbatim from
 * copier-config-request.js: a second copy of the addon's rule in the wrapper is how a
 * hand-typed enum came to FORBID values the addon accepts. If this file guessed a missing
 * value and the addon's defaults later changed, it could refuse a pair the addon would
 * take -- a false refusal the addon could never produce. So it judges only what the caller
 * WROTE: an explicit pair with `offset >= trigger`. Everything else (a single value against
 * a default, an omitted pair) is the addon's to decide, and it does. This is the exact
 * "PAIR" the defect names. And it is NOT a schema `default:` -- a default the receiver
 * merges is a WRITE, which is a different defect entirely (P1-73).
 */

/** Present means present. A supplied 0 is a value; only undefined/null/'' is absent. */
function suppliedNumber(v) {
  if (v === undefined || v === null || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/**
 * @param {object} args tool arguments for nt_place_atm_order
 * @returns {string|null} a refusal naming both values, or null when there is nothing to refuse.
 */
export function validateBreakevenPair(args = {}) {
  const trigger = suppliedNumber(args.breakevenTriggerTicks);
  const offset = suppliedNumber(args.breakevenOffsetTicks);

  // Only an EXPLICIT pair is judged here -- see the header. A missing half is the addon's.
  if (trigger === null || offset === null) return null;

  if (offset >= trigger) {
    return (
      `Invalid breakeven configuration: breakevenOffsetTicks (${offset}) must be less than ` +
      `breakevenTriggerTicks (${trigger}). A breakeven stop placed with offset at or past the ` +
      `trigger would rest at or inside the market and is not valid. (Refused at the tool ` +
      `boundary; the addon refuses the same pair.)`
    );
  }
  return null;
}
