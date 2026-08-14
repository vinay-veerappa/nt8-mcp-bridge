"""Mutation battery for P1-106: a lockout refused the order that would CLOSE the position.

⚠️ THIS IS THE HALF OF `P0-104` ITS FIX DELIBERATELY LEFT, and it is what turned "the flatten
failed" into "and you cannot fix it by hand". All three bridge order paths were:

    if (IsAccountLocked(account.Name))
        return new { error = "Order blocked: Account " + name + " is locked out." };

which does not care what the order DOES. Measured during `P0-104`'s reproduction on the live box
2026-08-14: Sim101 long 11 MNQ, locked by the panic switch, and a Sell was refused. The lockout
trapped the operator in the exact risk it exists to limit.

The guard has had this notion since `P1-44` (`IsPositionReducingOrder`, so a rate limit can never
cancel a protective order and leave a position naked). The bridge had it nowhere.

⚠️ **A test asserting only that a locked account refuses an ENTRY passes under the defect AND
under a gate deleted entirely.** That is why every mutant below has a matching negative control in
the suite, and why three of them are "allow too much" rather than "allow too little".

What each mutant defends:

  * MUTANT 1 restores the SHIPPED DEFECT in the predicate: a locked account refuses everything.
    Killed by the exit tests. It is the cheapest possible regression and the one most likely to
    come back from a merge.

  * MUTANT 2 DROPS THE QUANTITY CLAMP, so a `Sell 20` against a long 11 is admitted under a
    lockout -- an exit AND a new short 9. This is the load-bearing half: the clamp goes on what
    is NEW, exactly as in `P0-6`'s exit clamp and `P1-99`'s delta clamp. A suite that only tests
    clean exits cannot see it.

  * MUTANT 3 drops the OPPOSING-SIDE test, so scaling INTO a position is admitted under a
    lockout. "Reduces" becomes "touches the same instrument".

  * MUTANT 4 drops the FLAT test, so any order on a locked flat account is admitted. This is the
    one that makes the lockout ornamental, and a positive-only suite passes it -- the same shape
    as `P3-30`'s audit, which fired on a correctly protected account and passed three green
    acceptance tests.

  * MUTANT 5 admits BRACKETED orders when their entry would reduce. The stop and target legs take
    the opposite side, so the "exit" leaves resting orders that OPEN a short once the entry has
    closed the long. The deliberate asymmetry, restated as a defect.

  * MUTANT 6 sets `AllowedAsReducing` on every allowed order, including on unlocked accounts, so
    the "admitted under lockout" warning fires on every ordinary exit on all 96 accounts. **An
    alarm that is always on is off** -- the seventh instance of that class in this project, after
    `FILL_NOT_MEASURED`, `P3-30`'s audit, `LOCKOUT_STUCK`, `PEAK_GIVEBACK_BREACH` and two more.

  * MUTANT 7 reads the SIGN of the position quantity instead of its magnitude. NT8's
    `Position.Quantity` is ABSOLUTE -- the side is `MarketPosition` -- and reading the sign is
    `P0-96` verbatim, which doubled a follower's short behind 1311 green tests.

  * MUTANT 8 feeds the gate the resolved `OrderAction` label instead of the request's direction,
    at the call site. `OrderAction` is chosen by whoever submits the order (`P1-97`), so this
    reintroduces a label-as-truth read one statement after the code that fixed it. Killed by the
    source gate, because the call site lives in `McpBridgeAddOn.cs`, which no test build reaches
    (`P2-27`).

A crash counts as a kill (handover section 5.14).
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
GATE = os.path.join(REPO, 'addons', 'BridgeLockoutGate.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    (GATE,
     "the SHIPPED DEFECT: a locked account refuses every order, exit or not",
     '            if (!accountLocked)\n                return new LockoutDecision { Allowed = true };',
     '            if (!accountLocked)\n                return new LockoutDecision { Allowed = true };\n            return new LockoutDecision { Allowed = false, Reason = "Order blocked: locked out." };'),

    (GATE,
     "THE QUANTITY CLAMP IS DROPPED -- a Sell 20 against a long 11 is admitted under a lockout,\n"
     "     which is an exit AND a new short 9 opened on a locked account",
     '            if (orderQuantity > held)',
     '            if (false)'),

    (GATE,
     "the OPPOSING-SIDE test is dropped, so scaling INTO a position is admitted under a lockout",
     '            if (!opposes)',
     '            if (false)'),

    (GATE,
     "the FLAT test is dropped, so any order on a locked FLAT account is admitted and the\n"
     "     lockout becomes ornamental",
     '            if (!isLong && !isShort)',
     '            if (false)'),

    (GATE,
     "BRACKETED orders are admitted when their entry would reduce, leaving a stop and target\n"
     "     that OPEN the other side once the entry has closed the position",
     '            if (carriesBracket)',
     '            if (false)'),

    (GATE,
     "AllowedAsReducing is set on EVERY allowed order, including unlocked ones, so the\n"
     "     'admitted under lockout' warning fires on every ordinary exit on all 96 accounts",
     '                return new LockoutDecision { Allowed = true };',
     '                return new LockoutDecision { Allowed = true, AllowedAsReducing = true };'),

    (GATE,
     "the position quantity is read by SIGN rather than magnitude -- P0-96 verbatim, where NT8's\n"
     "     absolute Position.Quantity was read for a side and doubled a follower's short",
     '            int held = Math.Abs(positionQuantity);',
     '            int held = positionQuantity;'),

    (BRIDGE,
     "the call site feeds the gate the RESOLVED OrderAction label instead of the request's\n"
     "     direction, so the gate reads a label the caller chose (P1-97, one statement above)",
     '                actionStr.Equals("buy", StringComparison.OrdinalIgnoreCase),',
     '                resolvedAction.Equals("Buy", StringComparison.OrdinalIgnoreCase),'),
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

print('\n%d/%d mutants killed' % (len(MUTANTS) - len(survivors), len(MUTANTS)))
if survivors:
    print('\nSURVIVORS -- each is a test the suite does not have:')
    for s in survivors:
        print('  *', s)
sys.exit(1 if survivors else 0)
