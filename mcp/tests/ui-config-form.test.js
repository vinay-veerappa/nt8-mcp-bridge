/**
 * The editable guard-config form in `ui/index.html`.
 *
 * WHAT THIS DEFENDS AGAINST, AND IT IS ONE THING. The form's field list is a hand-typed set of
 * dotted paths into a config object that lives in another repo. Nothing at all connected the two:
 * a path with a typo, or a path for a field that gets renamed in nt8-riskguard, renders the row as
 * `absent`, is silently never sent, and the operator reads a form that is missing a limit they
 * believe they set. That is `P1-72` in its usual shape -- a surface describing a contract it
 * cannot see -- and it is the reason this file exists.
 *
 * ⚠️ THE FIXTURE IS THE REAL PAYLOAD, measured off the running box on 2026-08-16: 7,276 bytes,
 * ~120 AccountFirmMap rows, 8 FirmProfiles. Only those two maps are truncated here (to 2 and 1),
 * because they are exactly what the form deliberately does not edit. EVERY SCALAR PATH IS
 * UNTOUCHED. A fixture invented from the field list would make this test a mirror.
 *
 * ⚠️ WHAT THIS DOES NOT COVER, said plainly rather than left to be assumed. It reads ONE bounded
 * region of the page -- the `CFG_FIELDS` array literal -- and nothing else. It does not evaluate
 * the page, does not exercise `collectConfig`, `saveConfig` or the confirmation, and cannot see a
 * rendering defect. The page has no DOM harness; adding one is worth doing and is not this. State
 * the region a gate inspects.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PAGE = path.join(HERE, '..', '..', 'ui', 'index.html');
const FIXTURE = path.join(HERE, 'fixture-riskguard-config.json');

/**
 * Pull the CFG_FIELDS array literal out of the page and evaluate JUST it.
 *
 * ⚠️ Bounded on BOTH ends and it refuses rather than guessing. An earlier generation of gates in
 * these repos split on an opening token and read everything after it -- `check_expected_survivors`
 * read a trailing comment as a declaration, `check_anchors` skipped 18 anchors while printing ok.
 * If the array cannot be found or cannot be evaluated, that is a failure, not an empty list: a
 * gate that silently inspects nothing reports success forever.
 */
function readFieldList() {
  const html = fs.readFileSync(PAGE, 'utf8');
  const start = html.indexOf('var CFG_FIELDS = [');
  assert.ok(start >= 0, 'CFG_FIELDS not found in ui/index.html -- this gate would inspect nothing');
  const open = html.indexOf('[', start);
  const end = html.indexOf('\n];', open);
  assert.ok(end > open, 'CFG_FIELDS is not terminated by a line-initial "];" -- refusing to guess its extent');
  const literal = html.slice(open, end + 2);
  const fields = new Function('return ' + literal + ';')();
  assert.ok(Array.isArray(fields) && fields.length > 0, 'CFG_FIELDS evaluated to nothing');
  return fields;
}

function dig(obj, dotted) {
  const parts = dotted.split('.');
  let v = obj;
  for (const p of parts) {
    if (v === null || typeof v !== 'object') return undefined;
    v = v[p];
  }
  return v;
}

const fields = readFieldList();
const editable = fields.filter(f => f[0] !== '');
const groups = fields.filter(f => f[0] === '');
const cfg = JSON.parse(fs.readFileSync(FIXTURE, 'utf8')).config;

test('every editable field resolves against the real config payload', () => {
  const missing = [];
  for (const [p] of editable) {
    if (dig(cfg, p) === undefined) missing.push(p);
  }
  assert.deepEqual(missing, [],
    'these form paths do not exist in the config the bridge actually returns, so they render as '
    + '"absent" and are never sent');
});

test('the field list is not empty and is not all groups', () => {
  // The negative control for the test above: `missing` is empty when there is nothing to check,
  // so an emptied CFG_FIELDS would pass it. Closing the last instance disarms the gate.
  assert.ok(editable.length >= 15, `expected the ~20 operator knobs, found ${editable.length}`);
  assert.ok(groups.length >= 4, `expected grouped sections, found ${groups.length}`);
});

test('no field is listed twice', () => {
  const seen = new Set(), dupes = [];
  for (const [p] of editable) {
    if (seen.has(p)) dupes.push(p);
    seen.add(p);
  }
  assert.deepEqual(dupes, [], 'a duplicated path renders two inputs that overwrite each other');
});

test('every field declares a kind this form knows how to render', () => {
  const known = new Set(['int', 'num', 'bool', 'select']);
  const bad = editable.filter(f => !known.has(f[2])).map(f => `${f[0]} (${f[2]})`);
  assert.deepEqual(bad, [], 'an unknown kind falls through to a free text input');
});

test('every select declares its options, and every option is a string', () => {
  for (const f of editable.filter(f => f[2] === 'select')) {
    assert.ok(Array.isArray(f[3]) && f[3].length > 0, `${f[0]} is a select with no options`);
    for (const o of f[3]) assert.equal(typeof o, 'string', `${f[0]} has a non-string option`);
  }
});

/**
 * ⚠️ The three whitelists are hand-typed in the page and ENFORCED in the addon, which is the
 * `P1-72` shape the page's own comment admits to. This pins the two that are cheap to state and
 * that have each already been a defect:
 *
 *   * Mode -- `disabled` is the COPIER's mode (`IsRecognisedCopierMode`), not the guard's. The
 *     guard's preflight refuses anything outside shadow/live. Offering `disabled` here would put
 *     a value in the dropdown that writes a config the guard cannot arm on.
 *   * Alerts.MinSeverity -- `F-6`'s floor is FAIL-OPEN. `RankOf` answers 0 for an unrecognised
 *     string, so anything not in this exact set pushes the ENTIRE audit stream.
 */
test('the Mode dropdown offers exactly the guard modes, and not the copier`s', () => {
  const mode = editable.find(f => f[0] === 'Mode');
  assert.ok(mode, 'Mode is not on the form');
  assert.deepEqual(mode[3].slice().sort(), ['live', 'shadow']);
  assert.ok(!mode[3].includes('disabled'),
    '`disabled` is TradeCopierEngine.IsRecognisedCopierMode, not a guard mode -- preflight refuses it');
});

test('the severity dropdown offers exactly the ranks the sink recognises', () => {
  const sev = editable.find(f => f[0] === 'Alerts.MinSeverity');
  assert.ok(sev, 'Alerts.MinSeverity is not on the form');
  assert.deepEqual(sev[3].slice().sort(), ['critical', 'info', 'warning']);
});

test('the stop-guard action offers exactly what preflight accepts', () => {
  const om = editable.find(f => f[0] === 'StopGuard.OnMissing');
  assert.ok(om, 'StopGuard.OnMissing is not on the form');
  assert.deepEqual(om[3].slice().sort(), ['AutoStop', 'Flatten']);
});

/**
 * The form must not offer the collections it cannot edit. Rendering `FirmMirror.AccountFirmMap`
 * as one input would send a string where ~120 mappings live -- `P?-65` with two more zeros.
 */
test('the form offers no path into a collection it cannot edit', () => {
  const forbidden = ['FirmMirror.AccountFirmMap', 'FirmMirror.FirmProfiles', 'WindowsET', 'Profiles'];
  for (const [p] of editable) {
    for (const f of forbidden) {
      assert.ok(p !== f && !p.startsWith(f + '.'),
        `${p} reaches into ${f}, which the form reports as a count and must leave untouched`);
    }
  }
});
