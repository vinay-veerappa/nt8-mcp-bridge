"""Mutation battery for P2-109: `nt_orders` advertised three parameters and implemented NONE.

⚠️ THE `account` FILTER DID NOTHING. Measured live 2026-08-14 20:10Z, two calls back to back:

    nt_orders(account="Sim101", limit=8)  -> [ { account: "TAKEPROFITPRO524207503", ... } ]
    nt_orders(limit=6)                    -> [ { account: "TAKEPROFITPRO524207503", ... } ]

Byte-identical, and the one order in them is on a FUNDED account -- not the Sim101 that was
named. Sim101 had no working orders, so the honest answer was `[]`.

⚠️ THE FAILURE IS IN A JOIN, NOT A COMPONENT. Every layer was individually correct: the schema
advertised `account`/`limit`/`offset`, the MCP wrapper built the query string and sent all three,
and `GetOrders()` was a clean read of every account. The line between them was

    case "/api/orders":             return GetOrders();

taking nothing, between two routes already passing `query[...]`. **Nothing you could review in
isolation was wrong.** MUTANT 10 restores exactly that line.

⚠️ AND NOTE WHAT A CARELESS TEST WOULD ASSERT. "The filtered answer is a subset of the unfiltered
one" PASSES UNDER THE DEFECT -- every set is a subset of itself. The suite has to assert the two
answers DIFFER, which is what mutants 1 and 2 attack.

What each mutant defends:

  * MUTANT 1 makes the shared account predicate always true -- the defect's behaviour, now in the
    one place that decides it. A request naming Sim101 answers about all 96 accounts, and the same
    predicate decides which POSITIONS a close covers, so this is a liquidation bug too.

  * MUTANT 2 makes an omitted account match nothing, breaking the "no account means every account"
    contract in the direction that looks safe and reads as a flat book.

  * MUTANT 3 makes an unparseable limit mean ZERO rather than the default. `limit=abc` then returns
    an empty list, which is indistinguishable from "nothing is working" -- this defect's exact
    shape (a read that reassures) arriving through a different field.

  * MUTANT 4 drops the lower clamp, so `limit=0` and `limit=-5` return nothing.

  * MUTANT 5 drops the upper clamp, so `limit=999999999` serialises every order on 96 accounts into
    one response. The inventory endpoint measured 648KB per poll doing less than this.

  * MUTANT 6 lets a NEGATIVE offset through, which `GetRange` turns into an exception on a read
    endpoint -- and if it did not, Python-style semantics would silently return the last page.

  * MUTANT 7 makes an offset past the end wrap to a full page instead of an empty one, so an agent
    paging to the end reads page one again forever and never terminates.

  * MUTANT 8 makes the last partial page claim the full limit, an off-by-N that over-reads the
    matched list.

  * MUTANT 9 makes `HasMore` always false, so a caller with 96 accounts' worth of orders stops
    after the first page believing it saw everything. Silent truncation on a read is the same class
    as the silent widening this ticket is.

  * MUTANT 10 restores the SHIPPED DEFECT verbatim: the route calls `GetOrders()` with no
    arguments (SOURCE gate -- the route table lives in `McpBridgeAddOn.cs`, which no test build
    compiles, `P2-27`).

  * MUTANT 11 deletes the account resolution, so a typo'd account name is ignored and the call
    answers about every account -- `P1-90` on a read path (SOURCE gate).

  * MUTANT 12 gives the orders loop its own inline comparison instead of the shared predicate.
    Every behavioural test above still passes, because they test the predicate the loop no longer
    calls. This is `P2-107`'s and `P1-105`'s survivor restated a third time: extraction moves the
    untested boundary, it does not remove it (SOURCE gate).

A crash counts as a kill (handover section 5.14).
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
SCOPE = os.path.join(REPO, 'addons', 'BridgeAccountScope.cs')
QUERY = os.path.join(REPO, 'addons', 'BridgeOrderQuery.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    (SCOPE,
     "THE SHIPPED DEFECT in the one place that decides it: every account matches every request,\n"
     "     so naming Sim101 answers about all 96 -- and the same predicate scopes a CLOSE",
     '            if (string.IsNullOrWhiteSpace(accountName)) return false;\n            return string.Equals(accountName.Trim(), requestedAccount.Trim(),\n                                 StringComparison.OrdinalIgnoreCase);',
     '            return true;'),

    (SCOPE,
     "an omitted account matches NOTHING rather than every account -- broken in the direction\n"
     "     that looks safe and reads as a flat book",
     '            if (string.IsNullOrWhiteSpace(requestedAccount)) return true;',
     '            if (string.IsNullOrWhiteSpace(requestedAccount)) return false;'),

    (QUERY,
     "an UNPARSEABLE limit means zero rather than the default, so limit=abc returns an empty\n"
     "     list -- indistinguishable from 'nothing is working', which is this defect's shape",
     '            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out value))\n                return DefaultLimit;',
     '            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), out value))\n                return 0;'),

    (QUERY,
     "the lower clamp is dropped, so limit=0 and limit=-5 return nothing",
     '            if (value < 1) return 1;',
     '            if (value < 1) return value;'),

    (QUERY,
     "the upper clamp is dropped, so limit=999999999 serialises every order on 96 accounts\n"
     "     into a single response",
     '            if (value > MaxLimit) return MaxLimit;',
     '            if (value > MaxLimit) return value;'),

    (QUERY,
     "a NEGATIVE offset is passed through to GetRange, which throws on a read endpoint",
     '            return value < 0 ? 0 : value;',
     '            return value;'),

    (QUERY,
     "an offset PAST THE END wraps to a full page instead of an empty one, so an agent paging\n"
     "     to the end reads page one forever and never terminates",
     '            if (offset >= matched) return 0;',
     '            if (offset >= matched) return limit;'),

    (QUERY,
     "the last partial page claims the full limit -- an off-by-N that over-reads the list",
     '            return remaining < limit ? remaining : limit;',
     '            return limit;'),

    (QUERY,
     "HasMore is always false, so a caller stops after the first page believing it saw\n"
     "     everything. Silent truncation on a read, the mirror of this ticket's silent widening",
     '            return matched > offset + PageSize(matched, limit, offset);',
     '            return false;'),

    (BRIDGE,
     "THE SHIPPED DEFECT VERBATIM: the route calls GetOrders() with no arguments, discarding\n"
     "     all three parameters the wrapper faithfully sent (SOURCE gate)",
     '                    return GetOrders(query["account"], query["limit"], query["offset"]);',
     '                    return GetOrders(null, null, null);'),

    (BRIDGE,
     "the account resolution is deleted, so a typo is ignored and the read answers about every\n"
     "     account -- P1-90 on a read path (SOURCE gate)",
     '                if (ordersResolution.Refused) return new { error = ordersResolution.Error };',
     '                if (false) return new { error = ordersResolution.Error };'),

    (BRIDGE,
     "THE SHARPEST ONE: the orders loop gets its own inline comparison instead of the shared\n"
     "     predicate, so every behavioural test above still passes while the loop no longer calls\n"
     "     the thing they test (SOURCE gate)",
     '                if (!BridgeAccountScope.Matches(account.Name, requestedAccount)) continue;',
     '                if (requestedAccount != null && account.Name != requestedAccount) continue;'),
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
        # encoding pinned: the default on Windows is cp1252, and a single non-ASCII character in
        # a test's message makes capture_output raise UnicodeDecodeError on a reader THREAD --
        # res.stdout comes back None and the battery dies before its first mutant. Enforced for
        # every battery by tools/check_batteries_pin_encoding.py.
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
