"""Mutation battery for P2-115: a health flag that could never be false.

`/api/health` computed `connectedToFeed = accountCount > 0`. A running NT8 always reports at
least its Simulator accounts, so the field was `true` on every call, on every box, forever. It is
not a weak measurement of the data feed. IT IS NOT A MEASUREMENT OF THE DATA FEED.

MEASURED LIVE, and the pair of readings is the whole argument:

    14:20 UTC  dormant Playback connection, no replay, NO TRADEABLE MARKET AT ALL
               -> feedConnected: true
               (MNQ frozen eight days at volume 0; ES never subscribed; three ATM orders
                placed on Sim101 sat at OrderState.Initialized and were never routed)

    14:54 UTC  real broker attached, MNQ a live book at 30151.75 / 30155 on 1,925,425 volume
               -> feedConnected: true

IT DID NOT CHANGE VALUE WHEN THE THING IT NAMES CHANGED COMPLETELY. That is the cheapest possible
demonstration that it measured nothing, and it is why the mutants below are grouped the way they
are: the first group restores "always true" in four different disguises, and the second attacks
the classification that makes `false` reachable at all.

⚠️ THE POSITIVE CONTROL IS THE LOAD-BEARING TEST HERE, not the negative one. Every defect in this
ticket is a FALSE that cannot happen, so the obvious fix -- and the obvious mutant -- is a
constant. Mutant 5 is a bare `return false`, and it is the one that would have shipped a health
endpoint reporting a permanent outage on a working box. A detector needs a negative test; a status
field needs both.

⚠️ AND THE ROUTE ITSELF IS IN NO TEST BUILD. `McpBridgeAddOn.cs` names NinjaTrader types the
harness cannot resolve (`P2-27`, still open), so the last two mutants are aimed at a SOURCE gate
rather than at behaviour, and they are labelled as such. They prove the wiring is present, not
that it works -- `nt_compile` and the live read are the only evidence for that half, and the
negative live half was NOT obtainable: showing `false` requires disconnecting the operator's
broker, which is not mine to do.

A crash counts as a kill (handover section 5.14).

Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))

# The battery's OWN stdout must be utf-8, or a non-ASCII character in a mutant description raises
# between applying a mutant and restoring it, leaving a live mutant in the tree.
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

FEED = os.path.join(REPO, 'addons', 'BridgeFeedStatus.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    # ---- group 1: the flag goes back to being unable to say NO ---------------------------
    (FEED,
     "THE SHIPPED DEFECT, restored in its purest form: the answer is always true. This is what\n"
     "     `accountCount > 0` amounted to, with the arithmetic removed",
     '            if (names == null || providers == null || statuses == null)\n'
     '                return false;',
     '            if (names == null || providers == null || statuses == null)\n'
     '                return true;'),

    (FEED,
     "a SIMULATED provider now counts as a live feed. This is the defect at the exact point it\n"
     "     matters: Simulator accounts are always present, so admitting them makes the answer\n"
     "     true on every box forever -- `accountCount > 0` by another route",
     '                if (IsSimulated(providers[i]))\n'
     '                    continue;',
     '                if (false)\n'
     '                    continue;'),

    (FEED,
     "PLAYBACK alone is re-admitted. Narrower than the mutant above and it is the LIVE case:\n"
     "     the box was on a dormant Playback connection with no market at all, and this is\n"
     "     precisely the reading that made the field wrong on 2026-08-15",
     '            return string.Equals(provider, "Simulator", StringComparison.OrdinalIgnoreCase)\n'
     '                || string.Equals(provider, "Playback", StringComparison.OrdinalIgnoreCase);',
     '            return string.Equals(provider, "Simulator", StringComparison.OrdinalIgnoreCase);'),

    (FEED,
     "the CONNECTION STATUS stops being consulted, so a disconnected broker still reports a\n"
     "     live feed. The provider half alone is not the measurement -- an account can be real\n"
     "     and its connection down, which is the ordinary case after a drop",
     '                if (!IsConnected(statuses[i]))\n'
     '                    continue;',
     '                if (false)\n'
     '                    continue;'),

    # ---- group 2: the opposite failure, and it is the one review would miss ----------------
    (FEED,
     "THE OPPOSITE DEFECT AND THE ONE TO KNOW: a constant `false`. Every requirement in this\n"
     "     ticket is about a TRUE that cannot become false, so a constant false satisfies all of\n"
     "     them and ships a health endpoint reporting a permanent outage on a working box. The\n"
     "     positive control is what bans it",
     '                return true;\n'
     '            }\n'
     '\n'
     '            return false;',
     '                return false;\n'
     '            }\n'
     '\n'
     '            return false;'),

    (FEED,
     "`Connected` becomes a CASE-INSENSITIVE SUBSTRING test, so `Disconnected` reads as\n"
     "     connected -- it contains the word. P2-38's shape at a new site: that defect was\n"
     "     `Name.StartsWith(\"Sim\")`, and this is the same mistake with the polarity inverted,\n"
     "     since here the LONGER string is the one that means the opposite.\n"
     "     ⚠️ THIS MUTANT WAS WRONG ON ITS FIRST RUN and survived: written as a case-SENSITIVE\n"
     "     `Contains(\"Connected\")`, it did not express its own defect, because `Disconnected`\n"
     "     does not contain `Connected` with a capital C. Fourth instance of `read what a mutant\n"
     "     DOES before calling it a missing test`",
     '            return string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase);',
     '            return status != null && status.IndexOf("Connected", StringComparison.OrdinalIgnoreCase) >= 0;'),

    (FEED,
     "a BLANK provider is admitted as real. Anything not positively identified must not count\n"
     "     toward a live feed; failing open on an unrecognised provider is how an unknown state\n"
     "     becomes a reassuring one",
     '                if (string.IsNullOrWhiteSpace(providers[i]))\n'
     '                    continue;',
     '                if (false)\n'
     '                    continue;'),

    (FEED,
     "the arrays stop being clamped to the SHORTEST length, so a ragged call indexes past the\n"
     "     end. The route builds three arrays in one loop and cannot produce this today -- but\n"
     "     the class is public and the next caller is the one that will",
     '            if (statuses.Length < length)\n'
     '                length = statuses.Length;',
     '            if (false)\n'
     '                length = statuses.Length;'),

    # ---- group 3: the wiring, via the SOURCE gate (labelled -- it proves less) -------------
    (BRIDGE,
     "SOURCE GATE: the route goes back to deriving the flag from the account count. Aimed at a\n"
     "     source assertion rather than at behaviour, because McpBridgeAddOn.cs is in NO test\n"
     "     build (P2-27) -- nothing here can execute the route",
     '                            connectedToFeed = BridgeFeedStatus.IsMarketDataConnected(names, providers, statuses);',
     '                            connectedToFeed = accountCount > 0;'),

    (BRIDGE,
     "SOURCE GATE, and it is the sharper of the two: the class is still CALLED but its answer\n"
     "     is thrown away and the flag is hardcoded true. A gate asserting only that the old\n"
     "     expression is GONE passes under this -- a value that is COMPUTED is not a value that\n"
     "     is USED, which is the weakness P1-105's and P2-109's batteries both found",
     '                            connectedToFeed = BridgeFeedStatus.IsMarketDataConnected(names, providers, statuses);',
     '                            BridgeFeedStatus.IsMarketDataConnected(names, providers, statuses);\n'
     '                            connectedToFeed = true;'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    res = subprocess.run(
        ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
        cwd=REPO, capture_output=True, text=True,
        # encoding pinned: the default on Windows is cp1252, and one non-ASCII character in a
        # test message makes capture_output raise UnicodeDecodeError on a reader THREAD --
        # res.stdout comes back None and the battery dies before its first mutant.
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
