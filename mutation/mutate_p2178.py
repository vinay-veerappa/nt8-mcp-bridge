# -*- coding: utf-8 -*-
"""Mutation battery for P2-178: `nt_extract_trades` (and nt_capture_chart's fillTime) stamped
an EASTERN wall-clock with a literal `Z`, so every consumer read it as UTC.

MEASURED 2026-08-20: the tool reported an execution at `2026-08-20T09:57:51.985Z`;
interventions.jsonl had the same order at `13:57:52Z`. Four hours apart -- ET->UTC in August.

THE GROUPS:

  1. THE OFFSET IS DST-DEPENDENT, NOT A CONSTANT. This is the mutant that matters most: adding a
     fixed four hours -- the tempting wrong fix the plan entry explicitly warns against -- is
     RIGHT in August (EDT -4) and an hour wrong in January (EST -5). The measured-defect test
     (August) passes under it; only the winter case in the DST test kills it. That is why the
     DST test exists.
  2. THE CONVERSION HAPPENS AT ALL, and in the right DIRECTION. Not-converting stamps the ET
     wall-clock as UTC (the original defect); converting the wrong way subtracts instead of adds.
  3. THE Z IS STILL EMITTED. Dropping it would leave the instant right and the label absent --
     a consumer that keys on the Z would then guess.
  4. KIND NORMALISATION IS LOAD-BEARING. Without SpecifyKind, a Kind-tagged input throws inside
     ConvertTimeToUtc and takes out the whole trade-list response.
  5. THE SPRING-FORWARD GUARD. An impossible wall-clock (2:30am ET on the spring-forward Sunday)
     throws in ConvertTimeToUtc; the guard nudges it past the gap instead of dropping the response.
  6. ⚠️ BOTH CALL SITES, in the file the harness cannot execute. ExtractTrades was the measured
     one; fillTime carries the identical false-Z, and fixing only the measured site is the
     failure this repo keeps repeating. [[fix-the-class-not-the-instance]].

⚠️ A CRASH IS NOT AUTOMATICALLY A KILL (P2-148). The harness prints its result line LAST, so an
unhandled exception leaves 'NO RESULT LINE'; a crash counts only if a `[FAIL]` printed first.

Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

# ⚠️ REQUIRED, gate: tools/check_batteries_pin_encoding.py.
# [[a-battery-must-reach-its-restore-line]].
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
CONV = os.path.join(REPO, 'addons', 'BridgeTradeTime.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

MUTANTS = [
    # ---- group 1: the offset is DST-dependent, not a constant ------------------------------
    (CONV,
     "⚠️ THE WRONG FIX THE PLAN WARNS AGAINST: a fixed +4h subtraction instead of a real\n"
     "     conversion. It is RIGHT in August and an hour wrong in January -- the measured-defect\n"
     "     test passes under it, and only the DST test's winter case kills it",
     '            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(wall, sourceZone);',
     '            DateTime utc = wall.AddHours(4);'),

    # ---- group 2: the conversion happens, and in the right direction -----------------------
    (CONV,
     "the wall-clock is stamped as if it were ALREADY UTC -- the original defect exactly: 09:57\n"
     "     ET reported as 09:57Z",
     '            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(wall, sourceZone);',
     '            DateTime utc = wall;'),

    (CONV,
     "the conversion runs the WRONG WAY (FromUtc treats the wall-clock as UTC and converts it TO\n"
     "     Eastern), so 09:57 ET becomes 05:57Z -- off by twice the offset",
     '            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(wall, sourceZone);',
     '            DateTime utc = TimeZoneInfo.ConvertTimeFromUtc(wall, sourceZone);'),

    # ---- group 3: the Z is still emitted ---------------------------------------------------
    (CONV,
     "the trailing Z is dropped, so the instant is right and the label that names its zone is\n"
     "     gone -- a consumer keying on the Z is back to guessing",
     '            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");',
     '            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fff");'),

    # ---- group 4: Kind normalisation is load-bearing ---------------------------------------
    (CONV,
     "SpecifyKind is removed, so a Kind-tagged (Utc/Local) input throws inside ConvertTimeToUtc\n"
     "     and takes out the whole trade-list response for one value",
     '            DateTime wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);',
     '            DateTime wall = wallClock;'),

    # ---- group 5: the spring-forward guard -------------------------------------------------
    (CONV,
     "the invalid-time guard is neutered, so the spring-forward gap (2:30am ET) throws in\n"
     "     ConvertTimeToUtc rather than being nudged past the gap",
     '            if (sourceZone.IsInvalidTime(wall))',
     '            if (false)'),

    # ---- group 6: both call sites, in the file the harness cannot execute ------------------
    (BRIDGE,
     "⚠️ ExtractTrades reverts to the false Z on the Eastern exec.Time -- the MEASURED defect,\n"
     "     restored. Only the source gate holds this half; McpBridgeAddOn.cs is in no test build",
     '                        time = BridgeTradeTime.ToUtcIso(exec.Time),',
     '                        time = exec.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),'),

    (BRIDGE,
     "fillTime reverts to the false Z on targetExec.Time -- the SAME defect on the site that was\n"
     "     not the one measured, which is the one that gets left behind",
     '                resDict["fillTime"] = BridgeTradeTime.ToUtcIso(targetExec.Time);',
     '                resDict["fillTime"] = targetExec.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    try:
        p = subprocess.run(
            ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
            cwd=REPO, capture_output=True, text=True,
            encoding='utf-8', errors='replace', timeout=900)
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'
    out = (p.stdout or '') + (p.stderr or '')
    if 'error CS' in out:
        return 'BUILD FAILED'
    m = re.search(r'Passed = \d+, Failed = \d+', out)
    if not m and '[FAIL]' not in out:
        return 'NO RESULT LINE + NO ASSERTION FAILED (harness died undetected)'
    return m.group(0) if m else 'NO RESULT LINE'


print('=== baseline ===')
baseline = run()
print('  %s' % baseline)
if 'Failed = 0' not in baseline:
    print('baseline is RED; a battery against a red baseline scores nothing')
    sys.exit(2)

survivors = []
for target, name, old, new in MUTANTS:
    original = ORIGINALS[target]
    if original.count(old) != 1:
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(target, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    try:
        res = run()
        mm = re.search(r'Failed = (\d+)', res)
        undetected_crash = 'NO ASSERTION FAILED' in res
        killed = (not undetected_crash) and (
            ('BUILD FAILED' in res) or ('NO RESULT LINE' in res)
            or (mm is not None and int(mm.group(1)) > 0))
        print('  [%s] %s: %s' % ('KILLED' if killed else 'SURVIVED', name, res))
        if not killed:
            survivors.append(name)
    finally:
        restore()

restore()
print('\nrestored originals;', run())
print('\n%d/%d mutants killed' % (len(MUTANTS) - len(survivors), len(MUTANTS)))
if survivors:
    print('\nSURVIVORS -- each is a test the suite does not have:')
    for s in survivors:
        print('  *', s)
sys.exit(1 if survivors else 0)
