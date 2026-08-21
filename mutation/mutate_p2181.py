# -*- coding: utf-8 -*-
"""Mutation battery for P2-181: the bridge twin of P2-150. PlaceOcoOrder read the exit legs'
OrderState in the same breath as Submit() and derived "partial_submit" from it.

Submit is ASYNCHRONOUS: at that instant the legs are Initialized/Submitted, and the Rejected
verdict arrives 20-200ms later on OnOrderUpdate. So the read could never catch a rejection and
"partial_submit" was a status no live input could set -- [[a-green-that-can-never-be-red]]. The
fix reports "pending_legs" with the leg ids. SOURCE GATE only: McpBridgeAddOn.cs is in no test
build (P2-27), so a regression is caught by reading the text, and this battery proves the gate
actually fails on the regression rather than passing vacuously.

⚠️ A CRASH IS NOT AUTOMATICALLY A KILL (P2-148): a crash counts only if a `[FAIL]` printed first.
Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

# ⚠️ REQUIRED, gate: tools/check_batteries_pin_encoding.py.
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

MUTANTS = [
    (BRIDGE,
     "the OCO status regresses to the dead 'partial_submit' ternary -- the synchronous verdict\n"
     "     restored. The source gate must fail on the reappearance of the word",
     '                status = "pending_legs",\n'
     '                ocoId = ocoId,',
     '                status = "partial_submit",\n'
     '                ocoId = ocoId,'),

    (BRIDGE,
     "the honest 'pending_legs' status becomes 'submitted', over-claiming that the protective\n"
     "     legs are accepted when acceptance is not yet known",
     '                status = "pending_legs",\n'
     '                ocoId = ocoId,',
     '                status = "submitted",\n'
     '                ocoId = ocoId,'),

    (BRIDGE,
     "the synchronous rejected-orders read is RE-INTRODUCED -- the exact defect, restored. The\n"
     "     gate bans `rejectedOrders` from the OCO region, so its reappearance must fail",
     '                if (validOrders.Length > 0)\n'
     '                {\n'
     '                    account.Submit(validOrders);\n'
     '                }',
     '                if (validOrders.Length > 0)\n'
     '                {\n'
     '                    account.Submit(validOrders);\n'
     '                }\n'
     '                List<string> rejectedOrders = new List<string>();'),
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
