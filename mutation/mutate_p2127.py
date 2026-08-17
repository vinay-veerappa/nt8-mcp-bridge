"""Mutation battery for P2-127 slice 1: the FLEET tree and the ONE ordering it sorts by.

Section 4 of nt8-riskguard's docs/UI_REDESIGN_DESIGN.md -- "one window, two panes, zero nav
tabs" -- was never built; the page landed as four stacked sections and the operator called it
"cluttered". Section 4.2 kills top-level nav tabs explicitly, and the operator re-confirmed
section 4 against their own counter-proposal on 2026-08-16. This slice is the DECISION CLASS,
taken first because ui/index.html is in no test build and no mutation battery -- the plan entry
says so: move the decisions into a class the harness compiles, "otherwise this grows a third
untested surface, and it will be the one the operator actually uses".

WHAT THIS DEFENDS, and it is measured rather than imagined. One live payload carries BOTH of:

    "rows":   [ { "verdict": "Idle", "severity": 5 } ]   <- CopierSnapshotJson, 0 is WORST
    "system": { "severity": "warn" }                     <- CopierStatusSeverity, 0 is BEST

Opposite polarity, one page. A tree that sorts by "the severity number" is silently wrong for
one of them and looks plausible either way. The core already carries this warning for its own
half: SeverityRank exists because casting CopierConformance sorts an ORPHAN -- a follower holding
a live position nobody manages -- into the middle.

THE GROUPS BELOW:
  1. the two scales, in both directions. Sharing one conversion is the defect; so is inverting
     the one that was already right.
  2. fail-closed on what cannot be READ, and NOT-fail-closed on what does not APPLY. These pull
     opposite ways and the difference is the finding: 95 of 97 accounts on the live box are in no
     copier relationship, so ranking those "worst" is an alarm that is always on.
  3. the tree's shape -- one node per FOLLOWER rather than per row, and no account listed twice.
  4. the ordering being TOTAL. List<T>.Sort is documented UNSTABLE and `groups` is a Dictionary
     whose enumeration order is unspecified, and equal ranks are the normal case here, not the
     exception -- all 95 unlinked accounts tie.
  5. a parent carrying its WORST child, which is what stops a collapsed node reassuring.

⚠️ THREE OF THESE COME FROM ARBITRATING THE AGENT LOOP BY HAND after it returned
NOT_CONVERGING. One (the duplicate follower) was a panel finding my acceptance tests had missed;
one (the unstable sort) the arbiter had REJECTED as "stable and correct", which it is not; and
one (the not-applicable rank) was a design call my ticket was silent on, which is exactly how a
model comes to make it by default.

A crash counts as a kill (nt8-riskguard handover section 5.14).

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

FLEET = os.path.join(REPO, 'addons', 'BridgeFleetView.cs')

# (target file, description, find, replace)
MUTANTS = [
    # ---- group 1: the two scales -----------------------------------------------------------
    (FLEET,
     "the system scale stops being inverted and is passed through as-is -- i.e. ONE shared\n"
     "     scale for both inputs, which is the whole defect. `critical` then ranks 3, below\n"
     "     `Shadow`, and the worst thing the system can say sorts into the middle",
     '                case "ok":       return 3;          // best system state -> lowest page priority\n'
     '                case "info":     return 2;\n'
     '                case "warn":     return 1;\n'
     '                case "critical": return WorstRank;  // worst system state -> top of page',
     '                case "ok":       return 0;\n'
     '                case "info":     return 1;\n'
     '                case "warn":     return 2;\n'
     '                case "critical": return 3;'),

    (FLEET,
     "THE OPPOSITE ERROR, and the one a fix for the above invites: the COPIER scale gets\n"
     "     inverted too, on the theory that both need converting. It was already right",
     '            if (copierSeverity >= 0 && copierSeverity <= 5)\n'
     '                return copierSeverity;',
     '            if (copierSeverity >= 0 && copierSeverity <= 5)\n'
     '                return 5 - copierSeverity;'),

    # ---- group 2: fail-closed, and where it does NOT apply ----------------------------------
    (FLEET,
     "an out-of-range copier severity is clamped toward HEALTHY instead of failing closed,\n"
     "     so a value nobody can read renders as the calmest row on the page",
     '            // An unreadable rank must sort below every real state so it cannot look healthy.\n'
     '            return UnknownRank;',
     '            return 5;'),

    (FLEET,
     "an unrecognised system severity name answers `ok`. The name arrives from JSON, so a\n"
     "     renamed enum member on the core side is the expected way this happens",
     '                default:         return UnknownRank;',
     '                default:         return 3;'),

    (FLEET,
     "an account in NO copier relationship is ranked WORST again -- the model's own default,\n"
     "     and on the live box it paints 95 permanent red rows. An alarm that is always on is\n"
     "     off, and this system has produced that shape seven times by other routes",
     '                        Rank = NotApplicableRank,',
     '                        Rank = UnknownRank,'),

    (FLEET,
     "not-applicable collapses into the BEST real rank instead of sitting above it, which is\n"
     "     the opposite lie: 95 accounts the copier says nothing about, rendered as healthy",
     '        public const int NotApplicableRank = 6;',
     '        public const int NotApplicableRank = 5;'),

    (FLEET,
     "an EMPTY set of children is healthy again, so a group with a leader and no followers --\n"
     "     copying nothing -- sorts to the bottom looking fine",
     '            return any ? worst : UnknownRank;',
     '            return any ? worst : 5;'),

    # ---- group 3: the tree's shape ---------------------------------------------------------
    (FLEET,
     "one node per ROW instead of per FOLLOWER, so a leader/follower pair holding two\n"
     "     per-instrument relationships is listed TWICE. Both live rows are instrument-less,\n"
     "     so every test written against the box as it stands passes under this",
     # P2-138 rewrote this block to merge the BADGE as well as the rank, so the mutant now
     # disables the lookup rather than replacing the body. Same effect: one node per ROW.
     '                    if (byFollower.TryGetValue(key, out existing))',
     '                    if (false && byFollower.TryGetValue(key, out existing))'),

    (FLEET,
     "a de-duplicated follower keeps its FIRST rank rather than its worst, so which of two\n"
     "     relationships is displayed depends on the order they arrived in",
     '                        if (rank < existing.Rank) existing.Rank = rank;',
     '                        if (rank > existing.Rank) existing.Rank = rank;'),

    (FLEET,
     "a follower is no longer marked linked, so every follower ALSO appears under Unlinked\n"
     "     accounts -- the same account in two places, which is the shape P2-127 must not ship",
     '                if (!string.IsNullOrEmpty(row.FollowerAccountName))\n'
     '                    linked.Add(row.FollowerAccountName);',
     '                if (false)\n'
     '                    linked.Add(row.FollowerAccountName);'),

    (FLEET,
     "an ungrouped row keys by its FOLLOWER rather than its leader, so two relationships from\n"
     "     one leader become two groups -- section 4 decision 2 says a 1:1 pair is a group of\n"
     "     one and groups are the only grouping",
     '                string key = string.IsNullOrWhiteSpace(row.GroupName)\n'
     '                    ? row.LeaderAccountName\n'
     '                    : row.GroupName;',
     '                string key = string.IsNullOrWhiteSpace(row.GroupName)\n'
     '                    ? row.FollowerAccountName\n'
     '                    : row.GroupName;'),

    # ---- group 4: the ordering is TOTAL ----------------------------------------------------
    (FLEET,
     "the name tie-break goes, leaving rank alone to order the tree. List<T>.Sort is\n"
     "     UNSTABLE and equal ranks are the normal case -- 95 unlinked accounts tie -- so the\n"
     "     page re-orders itself between refreshes that saw identical data",
     '            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");',
     '            return 0;'),

    (FLEET,
     "the sort runs BEST-first, which is the one ordering that makes a worst-first page\n"
     "     actively misleading rather than merely unsorted",
     '            int byRank = a.Rank.CompareTo(b.Rank);',
     '            int byRank = b.Rank.CompareTo(a.Rank);'),

    # ---- group 5: a parent carries its worst child -----------------------------------------
    (FLEET,
     "a group's rank stops being folded out of the children it renders and is stated\n"
     "     independently -- F-9's defect exactly, and the reason P2-103 recounts from the\n"
     "     detail rows. A collapsed group then reassures over a bad follower",
     '                    Rank = WorstOf(childRanks)',
     '                    Rank = 5'),

    (FLEET,
     "the Unlinked node is dropped when it has no children. ⚠️ THIS ONE SURVIVED A 15/15\n"
     "     BATTERY and is why the house rule about a one-round green exists: every other test\n"
     "     supplied a spare account, so the empty case was never driven. An absent node and an\n"
     "     empty one read identically to a renderer -- the loop's own CF-9 at a second surface",
     '            groupNodes.Add(unlinkedNode);',
     '            if (unlinkedChildren.Count > 0) groupNodes.Add(unlinkedNode);'),

    (FLEET,
     "WorstOf returns the LARGEST rather than the smallest, so every parent reports its\n"
     "     healthiest child. Reads as a plausible off-by-one and inverts the page's meaning",
     '                if (!any || rank < worst)',
     '                if (!any || rank > worst)'),
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
