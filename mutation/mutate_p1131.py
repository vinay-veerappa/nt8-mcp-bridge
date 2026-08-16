"""Mutation battery for P1-131: "would severing this connection strand the order?"

THE FOURTH QUESTION over NT8's OrderState enum. The core carries three predicates over the same
enum -- OccupiesSlot ("must I not place a second one?"), ProvidesCoverage ("is the position
protected?") and AcceptsModification ("can I Change() it now?") -- and the third's comment opens
"The third question, added 2026-08-10 after a live trade". The bridge needed a fourth and instead
hand-wrote a list of seven states called OccupiesSlotForBridge.

MEASURED LIVE, Sunday session, while the funded 50K held a real position:

    nt_connection      TPT  openPositions: 1  workingOrders: 7
    nt_orders(funded)  4 orders, all "Working"          <- real bracket legs
    nt_orders(Sim101)  3 orders, all "CancelPending"    <- stuck ~5 hours, cannot fill

⚠️ THE FIRST INSTINCT -- borrow the core's OccupiesSlot -- IS THE ONE MUTANT THAT MATTERS. It
excludes Departing on purpose, so it would have discarded exactly those three orders: the
strongest case of something at the broker this process can no longer manage. Group 1 restores
that reading.

The old list also omitted six non-terminal states, EVERY omission in the direction that permits a
disconnect, because BridgeConnectionPlan.WouldStrand refuses only when the count is above zero.
Two twin-state asymmetries are the tell it was remembered rather than derived: ChangePending was
in the list and ChangeSubmitted was not; CancelPending was in and CancelSubmitted was not.

⚠️ GROUP 3 IS THE ONE TO KNOW. McpBridgeAddOn.cs is in NO test build (P2-27), so the wiring is
held by source gates only. A mutant that restores the old hand-rolled list at a call site walks
around the entire class while every behavioural test above still passes.

A crash counts as a kill (nt8-riskguard handover section 5.14).

Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

LIVE = os.path.join(REPO, 'addons', 'BridgeOrderLiveness.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

MUTANTS = [
    # ---- group 1: the question itself -------------------------------------------------------
    (LIVE,
     "THE FIRST INSTINCT, restored: a cancel in flight is treated as gone, which is the\n"
     "     core's OccupiesSlot reading. The three orders measured stuck for FIVE HOURS drop out\n"
     "     of the stranding count and the disconnect stops being refused for them",
     '                case "Filled":\n'
     '                case "Cancelled":\n'
     '                case "Rejected":',
     '                case "Filled":\n'
     '                case "Cancelled":\n'
     '                case "Rejected":\n'
     '                case "CancelPending":\n'
     '                case "CancelSubmitted":'),

    (LIVE,
     "Rejected stops being terminal -- the exact state the old inline filter in GetOrders\n"
     "     forgot, which is how a rejected order was served by an endpoint advertising\n"
     "     \"active/working orders\"",
     '                case "Rejected":\n'
     '                    return true;',
     '                    return true;'),

    (LIVE,
     "the two questions stop being complements: stranded is derived from terminal directly\n"
     "     rather than from its negation, so every terminal order is reported as stranded and\n"
     "     every live one as safe -- the page inverted",
     '            return !IsTerminal(orderStateName);',
     '            return IsTerminal(orderStateName);'),

    # ---- group 2: the fail-safe default -----------------------------------------------------
    (LIVE,
     "an unrecognised state name FAILS OPEN. A state a future NT8 adds then reads as gone,\n"
     "     and the disconnect is assessed as if nothing is out there. The two directions do not\n"
     "     cost the same: a false YES is a refusal the operator can override",
     '            if (string.IsNullOrWhiteSpace(orderStateName)) return false;\n'
     '            switch (orderStateName.Trim())',
     '            if (string.IsNullOrWhiteSpace(orderStateName)) return true;\n'
     '            switch (orderStateName.Trim())'),

    (LIVE,
     "the match goes case-insensitive, so a state that merely LOOKS like a terminal one is\n"
     "     assumed terminal. Widening a terminal test is the fail-open direction",
     '            switch (orderStateName.Trim())',
     '            switch (orderStateName.Trim().ToLowerInvariant())'),

    # ---- group 3: the wiring, which no behavioural test can reach ---------------------------
    (BRIDGE,
     "⚠️ THE OLD HAND-ROLLED LIST IS RESTORED at the connection count, so the one path the\n"
     "     defect was measured on walks around the whole class while every behavioural test of\n"
     "     the predicate still passes. McpBridgeAddOn.cs is in no test build (P2-27)",
     'if (o != null && BridgeOrderLiveness.WouldBeStrandedByDisconnect(o.OrderState.ToString())) workingOrders++;\n'
     '                }\n'
     '\n'
     '                // ⚠️ ALL the providers on the connection',
     'if (o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)) workingOrders++;\n'
     '                }\n'
     '\n'
     '                // ⚠️ ALL the providers on the connection'),

    (BRIDGE,
     "the orders route goes back to its own two-state list, forgetting Rejected exactly as\n"
     "     it did before",
     '                    if (BridgeOrderLiveness.IsTerminal(order.OrderState.ToString())) continue;',
     '                    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    res = subprocess.run(
        ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
        cwd=REPO, capture_output=True, text=True,
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
    print('\nREFUSING TO RUN: baseline is RED (%s failing).' % m.group(2))
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

print('\n%d/%d mutants killed' % (len(MUTANTS) - len(survivors), len(MUTANTS)))
if survivors:
    print('\nSURVIVORS -- each is a test the suite does not have:')
    for s in survivors:
        print('  *', s)
sys.exit(1 if survivors else 0)
