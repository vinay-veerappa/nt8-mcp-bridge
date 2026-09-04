"""Mutation battery for P3-111: `/api/bars` -- four defects filed as one line.

FILED AS: "`/api/bars` does `int.Parse(query["count"] ?? "100")` -- absent is handled,
unparseable throws." Probing the deployed box BEFORE writing code (`measure-the-deployed-system`)
found the endpoint broken at both ends of every parameter it takes. Measured 2026-08-14:

    count=abc | periodValue=xyz | period=Banana   -> HTTP 500 + a .NET stack trace
    (control: no count -> 200, 100 bars; count=5 -> 5 bars)

    count=5000     ->    531,658 bytes
    count=200000   -> 21,285,727 bytes      <- twenty-one megabytes, served happily
    count=1000000  -> 1,000,000 bars
    count=5000000  -> 0 bars, silently
    count=0 / -5   -> 0 bars, indistinguishable from "this instrument has no data"
  while the MCP tool schema advertised **"max 5,000 rows"** in two places.

    offset=0 and offset=500 -> BYTE-IDENTICAL payloads.

⚠️ THE PARSE CRASH WAS THE LEAST OF THEM. A 500 with a stack trace is ugly and LOUD; the caller
knows something went wrong. The unbounded response and the ignored offset are SILENT, and the
count=0 case is worse than either, because "0 bars" is a well-formed answer that reads as a fact
about the market. Weigh the quiet failure above the noisy one.

⚠️ AND `offset` MAKES THE CAP HONEST. Clamping count to 5,000 without implementing offset would
bound the response by bounding what is KNOWABLE -- callers would be pushed back to /api/bars/export
for anything longer. Mutant 7 attacks exactly that: it caps the REQUEST as well as the response,
which looks like a tightening and silently makes every page past the first return the same bars.

What each mutant defends:

  * MUTANT 1 restores the seam defect that is still REPRESENTABLE: the route discards `offset`,
    exactly as /api/orders discarded all three of its parameters. SOURCE gate.

    ⚠️ IT WAS WRITTEN AS "the route parses at the switch" AND SURVIVED, AND THE SURVIVOR WAS THE
    AUTHOR'S. The mutant passed `query["count"] ?? "100"` -- still a STRING, still handed to
    ParseCount, still correct. It did not restore the defect, so no test could kill it and no
    test was missing. The filed defect cannot be written as a mutant at all any more: GetBars
    takes no int, so `int.Parse` at the route does not COMPILE. The signature is the gate, and
    `TestP3_111_TheBarsRouteHandsTheRawStringsThrough` asserts that property directly.

    Second instance of P1-99's lesson: **a surviving mutant does not always mean a missing
    test.** There it was a mutant unkillable by construction; here it was a mutant that never
    expressed the defect it was named after. Read what the mutant DOES before writing a test for
    it -- the alternative is a test invented to satisfy a mutation that changes nothing, which is
    how a too-broad test gets the code broken to satisfy it.

  * MUTANT 2 restores the second crash: `Enum.Parse` straight onto the caller's string.
    `Enum.Parse` is `int.Parse` for names, and it was at TWO sites -- /api/bars and
    /api/bars/export -- of which only one was filed.

  * MUTANT 3 makes an unknown period silently mean Minute instead of refusing. This is the
    dangerous direction: the caller gets bars, reasons over them, and is never told they are not
    the bars they asked for. P1-90's "guessing" on a read path.

  * MUTANT 4 drops the enforced cap back to the advertised-only one, restoring the 21MB response.

  * MUTANT 5 makes the window read from the LEFT edge, so "the last 100 bars" returns the OLDEST
    100. Every price-based conclusion drawn from it is then about the wrong week.

  * MUTANT 6 ignores the offset in the window -- the measured defect, byte-identical payloads.

  * MUTANT 7 caps the REQUEST at MaxCount too, so paging past the first page silently repeats it.

  * MUTANT 8 makes `hasMore` `start > 0`, which reports "no more" whenever the request was exactly
    filled -- an agent stops one page early believing it read the whole series. ⚠️ THIS IS NOT A
    HYPOTHETICAL: it is what was very nearly shipped, caught while writing the return statement.

  * MUTANT 9 lets an absurd offset overflow to a negative request size.

  * MUTANT 10 makes the empty window return a page rather than nothing, so a pager never
    terminates -- the same non-termination P2-109's offset mutant defends on the orders side.
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
BARS = os.path.join(REPO, 'addons', 'BridgeBarsQuery.cs')
VALUE = os.path.join(REPO, 'addons', 'BridgeQueryValue.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    (BRIDGE,
     "THE MEASURED SEAM DEFECT: the route drops `offset` on the floor, exactly as it did for\n"
     "     all three of /api/orders' parameters -- the wrapper sends it and the switch discards\n"
     "     it, and every component either side is individually correct (SOURCE gate)",
     # ANCHOR REFRESHED 2026-09-04: the route gained a `query["format"]` argument, so the
     # find-string stopped matching (0 hits) and this battery scored a SURVIVOR as killed.
     # check_anchors.py caught it; CI could not PRINT it (cp1252 crash mid-report).
     '                    return GetBars(query["symbol"], query["period"], query["periodValue"],\n'
     '                        query["count"], query["offset"], query["format"]);',
     '                    return GetBars(query["symbol"], query["period"], query["periodValue"],\n'
     '                        query["count"], null, query["format"]);'),

    (BRIDGE,
     "the SECOND crash restored: period=Banana goes straight to Enum.Parse. Enum.Parse is\n"
     "     int.Parse for names, and the second site was never filed (SOURCE gate)",
     '            var periodName = BridgeBarsQuery.ResolvePeriod(\n'
     '                periodStr, Enum.GetNames(typeof(BarsPeriodType)), out refusal);\n'
     '            if (periodName == null) return new { symbol, error = refusal };',
     '            var periodName = periodStr ?? "Minute";'),

    (BARS,
     "an unknown period silently means Minute instead of refusing, so the caller gets bars,\n"
     "     reasons over them, and is never told they are not the bars they asked for",
     '            refusal = BridgeQueryValue.RefusalFor("period", raw, validNames);\n            return null;',
     '            return DefaultPeriod;'),

    (BARS,
     "the cap goes back to being advertised-only, restoring the MEASURED 21,285,727-byte\n"
     "     response for count=200000",
     '            return BridgeQueryValue.ParseInt(raw, DefaultCount, 1, MaxCount);',
     '            return BridgeQueryValue.ParseInt(raw, DefaultCount, 1, int.MaxValue);'),

    (VALUE,
     "the window reads from the LEFT edge, so 'the last 100 bars' returns the OLDEST 100 and\n"
     "     every conclusion drawn from them is about the wrong week",
     '            start = end - count;',
     '            start = 0;'),

    (VALUE,
     "THE MEASURED DEFECT: offset is ignored, so offset=0 and offset=500 return byte-identical\n"
     "     payloads. 'The filter returns a subset' passes under this -- the answers must DIFFER",
     '            int end = available - offset;      // exclusive; the newest bar the caller wants',
     '            int end = available;'),

    (BARS,
     "the REQUEST is capped at MaxCount as well as the response, which looks like a tightening\n"
     "     and silently makes every page past the first return the same bars",
     '            long total = (long)count + offset;\n            return total > int.MaxValue ? int.MaxValue : (int)total;',
     '            long total = (long)count + offset;\n            return total > MaxCount ? MaxCount : (int)total;'),

    (BRIDGE,
     "hasMore becomes `start > 0`, which reports 'no more' whenever the request was exactly\n"
     "     filled, so an agent stops one page early. NEARLY SHIPPED (SOURCE gate)",
     '                    hasMore = available >= requestSize,',
     '                    hasMore = start > 0,'),

    (BARS,
     "an absurd offset OVERFLOWS to a negative request size instead of saturating -- this\n"
     "     ticket's own defect wearing a new hat",
     '            long total = (long)count + offset;',
     '            int total = count + offset;'),

    (VALUE,
     "an empty window returns a page instead of nothing, so a pager reading to the end of\n"
     "     history never terminates",
     '            if (end <= 0) return false;        // paged off the front of the series',
     '            if (end <= 0) { take = count; return true; }'),
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
