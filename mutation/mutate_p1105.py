"""Mutation battery for P1-105: `nt_close_position` reported a close it had not observed.

⚠️ THE HANDLER ANSWERED `{"status": "flattened", "positionClosed": true}` WITH THE POSITION STILL
OPEN. `positionClosed = true` sat on the line after `account.Flatten(...)`, which is ASYNCHRONOUS,
so the field recorded that control reached that line. Measured on Sim101 2026-08-14 13:46:33Z:
long 11 MNQ, no lockout, guard in shadow, and `interventions.jsonl` shows NO `ORDER_UPDATE` for
the account at all -- nothing reached the book. `status` was a constant string in the return
expression and never a claim about anything.

THE SECOND-READER SHAPE. `EmergencyFlatten` learned all of this as `P0-104`. `ClosePosition` --
the other of exactly two `.Flatten(` call sites in `McpBridgeAddOn.cs` -- was never told, the same
way `IsAccountLocked` was never told what `CanTrade` had learned (`P1-100`). Half the mutants
below therefore attack the SHARING rather than the logic: the acting pass and the observing pass
must answer "which positions is this request about?" through one predicate, or the report is true
about a set the caller never named.

⚠️ A SUITE THAT ONLY DRIVES THE HAPPY PATH CANNOT SEE ANY OF THIS. `positionClosed` was `true` on
every healthy close too -- it was right for the wrong reason, which is why nothing caught it for
as long as nobody looked at a position afterwards. Mutants 1-3 are all "claim more than was
observed", and each needs a test that drives an UNSUCCESSFUL close to die.

What each mutant defends:

  * MUTANT 1 restores the SHIPPED DEFECT exactly: `PositionClosed` is true whenever anything was
    attempted. This is the cheapest possible regression and the one a merge brings back.

  * MUTANT 2 drops the `positionsStillOpen == 0` clause, so the live measurement -- one position
    matched, still open -- reports closed. The defect with the new fields still present.

  * MUTANT 3 drops the `positionsMatched > 0` clause, so a typo'd symbol that matched NOTHING
    reports a successful close. The failure the account resolver exists to prevent, arriving
    through the symbol field instead.

  * MUTANT 4 returns the constant "flattened" from `StatusFor`, which is what the old handler
    literally did.

  * MUTANT 5 collapses "submitted but not confirmed" into "not submitted", losing the one
    distinction that tells an operator whether to place the exit by hand.

  * MUTANT 6 makes `WantsEverySymbol` treat blank as a wildcard. ⚠️ THIS IS NOT HYPOTHETICAL -- it
    is what the class did when first written, and a test in the same commit disagreed with it and
    won: the handler defaults on `IsNullOrEmpty`, so `{"symbol": "   "}` would have reached the
    filter as three spaces and been read as a request to CLOSE EVERY POSITION on the account.

  * MUTANT 7 restores the `StartsWith` symbol filter, under which `symbol: "M"` was a request to
    close MNQ, MES, MCL and MGC together.

  * MUTANT 8 drops the empty-root guard, so an order with no instrument name matches a blank
    symbol request.

  * MUTANT 9 makes `MatchesAccount` ignore the name, so a request naming one account closes
    positions on all 96 -- including the funded one. `P1-100`'s lesson: a defect found on sim is
    not confined to sim.

  * MUTANT 10 makes an omitted account match NOTHING rather than everything, silently breaking
    the handler's long-standing contract in the safe-looking direction.

  * MUTANT 11 turns `InScope`'s AND into an OR.

  * MUTANT 12 stops `RootOf` splitting, so "MNQ 09-26" no longer answers a request for "MNQ".

  * MUTANT 13 reverts the endpoint's status to the constant string (SOURCE gate -- the expression
    lives in `McpBridgeAddOn.cs`, which no test build compiles, `P2-27`).

  * MUTANT 14 reverts `positionClosed` to a count of what was REQUESTED (SOURCE gate).

  * MUTANT 15 IS THE SHARPEST. It gives the OBSERVING pass its own hand-rolled symbol filter while
    leaving the acting pass on the shared predicate -- so eleven behavioural tests of
    `BridgeClosePlan` still pass while the report describes a different set of positions than the
    call acted on. This is `P2-107`'s survivor restated: there, a mutant reverted the one handler
    the defect was measured on to its old bare loop and walked around the whole mechanism.

  * MUTANT 16 reverts the cancel count to `+= toCancel.Count`, which credited every order in the
    list when the call threw (SOURCE gate).

  * MUTANT 17 removes the settle poll's exit condition so it reads once and stops. `Flatten` is
    asynchronous, so EVERY healthy close would then report `close_submitted_not_confirmed` -- an
    alarm that is always on is off, and this project has now met that class eight times.

  * MUTANT 18 deletes the account resolution, restoring `P1-90` at this seventh site: a typo'd
    account name matches nothing and the call reports on a scope the operator never named.

A crash counts as a kill (handover section 5.14).
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PLAN = os.path.join(REPO, 'addons', 'BridgeClosePlan.cs')
# P2-109 moved the account predicate out of BridgeClosePlan into its ONE home, because nt_orders
# needed the same question answered and the alternative was a second copy. Mutants 9 and 10 were
# REPOINTED here rather than retired -- an anchor that stops matching prints [SKIP] and scores a
# SURVIVOR, so a moved predicate with stale anchors is a battery quietly proving nothing.
SCOPE = os.path.join(REPO, 'addons', 'BridgeAccountScope.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    (PLAN,
     "the SHIPPED DEFECT: PositionClosed is true whenever anything was attempted",
     '            return positionsMatched > 0 && positionsStillOpen == 0;',
     '            return true;'),

    (PLAN,
     "the 'still open' clause is dropped, so the LIVE MEASUREMENT -- one position matched and\n"
     "     still open -- reports a successful close",
     '            return positionsMatched > 0 && positionsStillOpen == 0;',
     '            return positionsMatched > 0;'),

    (PLAN,
     "the 'matched anything' clause is dropped, so a symbol that matched NOTHING reports closed",
     '            return positionsMatched > 0 && positionsStillOpen == 0;',
     '            return positionsStillOpen == 0;'),

    (PLAN,
     "StatusFor returns the constant 'flattened', which is what the old handler literally did",
     '            if (positionsMatched == 0) return "nothing_to_close";',
     '            if (true) return "flattened";'),

    (PLAN,
     "'submitted but not confirmed' collapses into 'not submitted', losing the distinction that\n"
     "     tells an operator whether to place the exit by hand",
     '                return ordersSubmitted > 0 ? "close_submitted_not_confirmed" : "close_not_submitted";',
     '                return "close_not_submitted";'),

    (PLAN,
     "a BLANK symbol becomes a wildcard -- what this class did when first written, and\n"
     "     {\"symbol\": \"   \"} would then close every position on the account",
     '            if (requestedSymbol == null) return false;',
     '            if (string.IsNullOrWhiteSpace(requestedSymbol)) return true;'),

    (PLAN,
     "the StartsWith symbol filter is restored, under which symbol:'M' closed MNQ, MES, MCL\n"
     "     and MGC together",
     '            return string.Equals(want, have, StringComparison.OrdinalIgnoreCase);',
     '            return have.StartsWith(want, StringComparison.OrdinalIgnoreCase);'),

    (PLAN,
     "the empty-root guard is dropped, so an order with no instrument name matches a blank\n"
     "     symbol request",
     '            if (want.Length == 0 || have.Length == 0) return false;',
     '            if (false) return false;'),

    (SCOPE,
     "the account predicate ignores the name, so a request naming ONE account closes positions\n"
     "     on all 96 -- the funded one included -- and makes nt_orders answer about every account",
     '            if (string.IsNullOrWhiteSpace(requestedAccount)) return true;\n            if (string.IsNullOrWhiteSpace(accountName)) return false;',
     '            return true;\n#pragma warning disable 0162\n            if (string.IsNullOrWhiteSpace(accountName)) return false;'),

    (SCOPE,
     "an omitted account matches NOTHING rather than every account, breaking the handler's\n"
     "     contract in the safe-looking direction",
     '            if (string.IsNullOrWhiteSpace(requestedAccount)) return true;',
     '            if (string.IsNullOrWhiteSpace(requestedAccount)) return false;'),

    (PLAN,
     "InScope's AND becomes an OR",
     '            return MatchesAccount(accountName, requestedAccount)\n                && MatchesSymbol(instrumentFullName, requestedSymbol);',
     '            return MatchesAccount(accountName, requestedAccount)\n                || MatchesSymbol(instrumentFullName, requestedSymbol);'),

    (PLAN,
     "RootOf stops splitting, so 'MNQ 09-26' no longer answers a request for 'MNQ'",
     '            return space < 0 ? trimmed : trimmed.Substring(0, space);',
     '            return trimmed;'),

    (BRIDGE,
     "the endpoint's status reverts to the constant string (SOURCE gate)",
     '            string status = BridgeClosePlan.StatusFor(positionsMatched, positionsStillOpen.Count, flattenOrdersSubmitted);',
     '            string status = "flattened";'),

    (BRIDGE,
     "positionClosed reverts to a count of what was REQUESTED rather than observed (SOURCE gate)",
     '            bool positionClosed = BridgeClosePlan.PositionClosed(positionsMatched, positionsStillOpen.Count);',
     '            bool positionClosed = flattenRequested > 0;'),

    (BRIDGE,
     "THE SHARPEST ONE: the OBSERVING pass gets its own hand-rolled filter while the acting pass\n"
     "     keeps the shared predicate, so the report describes a different set of positions than\n"
     "     the call acted on -- and every behavioural test of BridgeClosePlan still passes",
     '                                if (!BridgeClosePlan.MatchesSymbol(pp.Instrument.FullName, symbol)) continue;',
     '                                if (!pp.Instrument.FullName.StartsWith(symbol, StringComparison.OrdinalIgnoreCase)) continue;'),

    (BRIDGE,
     "the cancel count reverts to += toCancel.Count, crediting every order in the list when the\n"
     "     call threw (SOURCE gate)",
     '                        try { account.Cancel(new[] { ord }); cancelledOrdersCount++; }',
     '                        try { account.Cancel(toCancel); cancelledOrdersCount += toCancel.Count; }'),

    (BRIDGE,
     "the settle poll loses its exit condition and reads once, so EVERY healthy close reports\n"
     "     'submitted but not confirmed' -- an alarm that is always on is off (SOURCE gate)",
     '                if (positionsStillOpen.Count == 0) break;',
     '                break;'),

    (BRIDGE,
     "the account resolution is deleted, restoring P1-90 at this seventh site: a typo matches\n"
     "     nothing and the call reports on a scope the operator never named (SOURCE gate)",
     '                if (closeResolution.Refused) return new { error = closeResolution.Error };',
     '                if (false) return new { error = closeResolution.Error };'),
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
