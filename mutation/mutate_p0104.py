"""Mutation battery for P0-104: the panic kill-switch cancelled its own flatten order.

⚠️ THIS ONE CANCELLED THE STOPS, CANCELLED THE FLATTEN, LOCKED THE ACCOUNT, AND REPORTED SUCCESS.

`EmergencyFlatten` runs five steps per account: terminate strategies, cancel every working order,
`acc.Flatten(...)`, a second cancel pass "for residual bracket/OCO orders", then engage a lockout.
`acc.Flatten` is ASYNCHRONOUS -- it SUBMITS a `Close` market order and returns. The second pass then
enumerated `acc.Orders` for anything active and cancelled all of it, including the `Close` order
step 3 had put on the book a moment earlier.

Measured on the live box 2026-08-14, Sim101 long 11 MNQ with one resting limit:

    35541  Limit Buy 1     McpBridge  -> Cancelled                        <- step 2, correct
    35542  Market Sell 11  Close      -> Submitted -> Working -> CANCELLED <- step 4 kills the flatten

    {"success": true, "cancelledOrders": 2, "firstPassCancelled": 1, "residualCancelled": 1,
     "flattenedAccounts": 1, "errors": []}          ... and the account was STILL LONG 11.

Ordered the way an operator meets it: stops cancelled -> flatten cancelled -> account locked (so
`nt_place_order` refuses the exit they would place by hand, measured) -> `success: true`.

Two halves to the fix, and this battery defends both:

  * the SET ARITHMETIC moved into `BridgeFlattenPlan`, which names no NT8 type and is therefore
    EXECUTED by tests/BridgeTests.csproj rather than grepped -- P2-27's cheap pattern, the fourth
    file to use it;
  * the REPORT stopped claiming an outcome it never observed. The old counter incremented on the
    call to `acc.Flatten`, so it was true before anything could close.

What each mutant defends:

  * MUTANT 1 restores the shipped defect at the call site -- the residual pass enumerates every
    active order again. Killed by the source gate, because the call site lives in
    `McpBridgeAddOn.cs`, which no test build reaches (P2-27); the gate asserts BOTH that the plan is
    called and that the old expression is absent, because either alone can be satisfied by a patch
    that leaves the defect live beside the fix.

  * MUTANT 2 neuters the plan itself: it returns everything active, so the call site looks fixed and
    behaves exactly as before. This is the one a source gate CANNOT catch, and the reason the
    arithmetic was extracted into an executable file.

  * MUTANT 3 is THE WRONG FIX, inverted: it cancels only the orders THIS call submitted, so the
    flatten dies and the trader's stops survive. It passes any test that only asks "is the flatten
    excluded".

  * MUTANT 4 filters the "before" snapshot by active state -- the defect in the OPPOSITE direction.
    A bracket leg that was inactive before the flatten and reaches Working after it is then read as
    a new order and survives the cleanup, which is what the residual pass exists to prevent.

  * MUTANT 5 restores the old success expression, which could report `success: true` with a position
    still open because nothing in the method had ever looked at a position.

A crash counts as a kill (handover section 5.14).
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PLAN = os.path.join(REPO, 'addons', 'BridgeFlattenPlan.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    (BRIDGE,
     "the SHIPPED DEFECT at the call site: the residual pass enumerates every active order\n"
     "     again, so it cancels the Close order acc.Flatten just submitted",
     '                        var residualOrders = BridgeFlattenPlan.ResidualCancelSet(knownBeforeFlatten, activeAfterFlatten);',
     '                        var residualOrders = acc.Orders.Where(o => activeStates.Contains(o.OrderState)).ToList();'),

    (PLAN,
     "the PLAN is neutered -- it returns everything active, so the call site reads as fixed and\n"
     "     behaves exactly as the defect did. No source gate can see this one",
     '                if (known.Contains(order)) residual.Add(order);',
     '                residual.Add(order);'),

    (PLAN,
     "THE WRONG FIX, inverted: only the orders THIS call submitted are cancelled, so the flatten\n"
     "     dies and the trader's stops survive",
     '                if (known.Contains(order)) residual.Add(order);',
     '                if (!known.Contains(order)) residual.Add(order);'),

    (BRIDGE,
     "the 'before' snapshot is filtered by active state -- the defect in the OPPOSITE direction.\n"
     "     A bracket leg that was inactive before the flatten and active after it reads as a new\n"
     "     order and survives the cleanup the pass exists for",
     '                        var knownBeforeFlatten = acc.Orders.ToList();',
     '                        var knownBeforeFlatten = acc.Orders.Where(o => activeStates.Contains(o.OrderState)).ToList();'),

    (BRIDGE,
     "the old success expression returns, so a panic flatten that left a position open reports\n"
     "     success as long as it cancelled something on the way",
     '            bool success = errors.Count == 0 && accountsStillOpen.Count == 0;',
     '            bool success = errors.Count == 0 || (totalCancelled + flattenSubmitted) > 0;'),
]

ORIGINALS = {}
for target, _, _, _ in MUTANTS:
    if target not in ORIGINALS:
        ORIGINALS[target] = open(target, encoding='utf-8').read()


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    res = subprocess.run(
        ['dotnet', 'run', '--project', os.path.join('tests', 'BridgeTests.csproj')],
        cwd=REPO, capture_output=True, text=True,
        # encoding pinned: the default on Windows is cp1252, and a single non-ASCII
        # character in a test's message (the suite uses them) makes capture_output
        # raise UnicodeDecodeError on a reader THREAD -- res.stdout comes back None and
        # the battery dies before its first mutant. A battery that cannot run is not a
        # battery that passed.
        encoding='utf-8', errors='replace')
    if 'error CS' in (res.stdout + res.stderr):
        return 'BUILD FAILED'
    m = re.search(r'Passed = \d+, Failed = \d+', res.stdout)
    return m.group(0) if m else 'NO RESULT LINE'


print('=== baseline ===')
baseline = run()
print(' ', baseline)

m = re.search(r'Passed = (\d+), Failed = (\d+)', baseline)
if not m:
    print('\nREFUSING TO RUN: could not read a result line from the baseline.')
    sys.exit(2)
if int(m.group(2)) != 0:
    print('\nREFUSING TO RUN: baseline is RED (%s failing). Every mutant would score KILLED '
          'on pre-existing failures and this battery would prove nothing.' % m.group(2))
    sys.exit(2)

survivors = []
for target, name, old, new in MUTANTS:
    original = ORIGINALS[target]
    if original.count(old) != 1:
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(target, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    res = run()
    mm = re.search(r'Failed = (\d+)', res)
    killed = ('BUILD FAILED' in res) or ('NO RESULT LINE' in res) \
        or (mm is not None and int(mm.group(1)) > 0)
    print('  [%s] %s: %s' % ('KILLED' if killed else 'SURVIVED', name, res))
    if not killed:
        survivors.append(name)
    restore()

restore()
print('\nrestored originals;', run())
print('\nSURVIVORS:', survivors if survivors else 'none')

sys.exit(1 if survivors else 0)
