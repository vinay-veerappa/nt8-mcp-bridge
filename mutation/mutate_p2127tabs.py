"""Mutation battery for P2-127 slice 3: the INSPECTOR's three tabs.

Section 4 of nt8-riskguard's docs/UI_REDESIGN_DESIGN.md specifies an inspector with three tabs --
[copier] [risk] [rare] -- and they are the ONLY tabs in the application. Section 4.2 killed
TOP-LEVEL nav tabs and recorded the decision "so nobody re-adds them"; these are not those. They
live inside the inspector, scoped to the selection.

WHY TABS ARE DANGEROUS AT ALL, which is 4.2's own reason. This page's value is that `Inert`,
`ConfiguredNotEvaluated` and a non-acting copier are visible WITHOUT BEING LOOKED FOR. Anything
that puts a section behind a click has to carry that section's worst state into the always-visible
strip -- folded out of the same payload the section renders, never from its own counters (`F-9`;
`P2-103` recounts from the detail rows for this reason).

MEASURED ON THE DEPLOYED BOX, 2026-08-17, and it is what the risk tab folds:

    GET /api/riskguard/inventory   97 accounts x 23 rules = 2231 rule rows
                                   unevaluatedRules: EMPTY
                                   EvaluatedNotEnforcing 1129 / Inert 559 / Disabled 543

So on a box with accounts loaded, **Inert is the worst state that exists**, and a strip watching
only `unevaluatedRules` -- the state this page was built to surface -- renders three clean tabs over
the condition that actually exists. Live after the fix: `risk` reads `Inert (559)`, and selecting one
account narrows it to `Inert (3)`.

THE GROUPS BELOW:
  1. the rank scale. LOWER IS WORSE, shared with the fleet tree; Inert must rank worse than
     EvaluatedNotEnforcing, and an UNRECOGNISED state must rank worst rather than best.
  2. ⚠️ KNOWN-CLEAN vs UNREADABLE, which is the finding the panel got right and the first
     implementation got wrong. `WorstOf(new int[0])` returns `UnknownRank`, which IS `WorstRank`, so
     a rare tab with zero conflicts rendered as the WORST thing on the strip while its own badge read
     "No conflicts". What a surface REPORTS disagreeing with what it RANKS is the defect, in the
     direction that trains an operator to discount the strip.
     [[an-inapplicable-state-is-not-unreadable]].
  3. the strip's shape: three tabs ALWAYS, in section 4's order, each with a non-empty badge. An
     absent tab and an empty one read identically to whatever renders them -- that was the sixteenth
     mutant of slice 1's battery and it survived a green suite.
  4. the SELECTION. The inspector follows it; a strip that keeps showing fleet-wide totals beside one
     account's config answers a question nobody asked. Case-insensitively, as the core compares
     account names everywhere.

A crash counts as a kill.

Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

# ⚠️ Both halves of the encoding pin. The DECODE half is on the subprocess capture below; this is
# the ENCODE half, and without it one non-ASCII character in a mutant description raises
# UnicodeEncodeError inside print() AFTER a mutant is applied and BEFORE it is restored -- leaving a
# LIVE MUTANT in the tree. [[a-battery-must-reach-its-restore-line]].
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
TABS = os.path.join(REPO, 'addons', 'BridgeInspectorTabs.cs')

MUTANTS = [
    # ---- group 1: the rank scale ---------------------------------------------------------------
    (TABS,
     "⚠️ THE SCALE IS INVERTED: Inert becomes BETTER than EvaluatedNotEnforcing. On the\n"
     "     measured box that sinks 559 Inert rows below 1129 evaluated ones and the strip reads\n"
     "     clean -- the exact hazard section 4.2 killed top-level tabs over",
     '                case "ConfiguredNotEvaluated": return 0;\n'
     '                case "Inert": return 1;\n'
     '                case "Disabled": return 2;\n'
     '                case "EvaluatedNotEnforcing": return 3;',
     '                case "ConfiguredNotEvaluated": return 3;\n'
     '                case "Inert": return 2;\n'
     '                case "Disabled": return 1;\n'
     '                case "EvaluatedNotEnforcing": return 0;'),

    (TABS,
     "an UNRECOGNISED rule state becomes the BEST rank instead of the worst. A state this code\n"
     "     has never heard of is the one case where it cannot know, and calling it healthy is the\n"
     "     fail-OPEN direction -- BridgeFleetView.RankOfSystemSeverity exists to do the opposite",
     '                default: return BridgeFleetView.RankOfSystemSeverity(state);',
     '                default: return CleanRank;'),

    (TABS,
     "an EMPTY state string becomes clean rather than unknown, so a rule row the producer sent\n"
     "     without a state reads as healthy",
     '            if (string.IsNullOrEmpty(state))\n'
     '                return BridgeFleetView.UnknownRank;',
     '            if (string.IsNullOrEmpty(state))\n'
     '                return CleanRank;'),

    # ---- group 2: known-clean vs unreadable ----------------------------------------------------
    (TABS,
     "⚠️ THE DEFECT THE PANEL CAUGHT, RESTORED: a rare tab with ZERO conflicts folds an empty\n"
     "     set again. WorstOf(new int[0]) is UnknownRank, which IS WorstRank -- so the tab renders\n"
     "     as the worst item on the always-visible strip while its own badge reads 'No conflicts'",
     '            int worstRank = configConflicts > 0 ? BridgeFleetView.WorstRank : CleanRank;',
     '            int worstRank = configConflicts > 0 ? BridgeFleetView.WorstRank : BridgeFleetView.WorstOf(new int[0]);'),

    (TABS,
     "CleanRank rises to NotApplicableRank, so a clean tab claims the question does not apply\n"
     "     to this box. It DOES apply and the answer is 'nothing wrong' -- BridgeFleetView's own\n"
     "     comment keeps those two apart deliberately",
     '        public const int CleanRank = 5;',
     '        public const int CleanRank = BridgeFleetView.NotApplicableRank;'),

    (TABS,
     "a real config conflict stops being the worst and becomes clean, so the one thing the rare\n"
     "     tab exists to surface is the thing it hides",
     '            int worstRank = configConflicts > 0 ? BridgeFleetView.WorstRank : CleanRank;',
     '            int worstRank = CleanRank;'),

    # ---- group 4: the selection ----------------------------------------------------------------
    (TABS,
     "⚠️ THE SELECTION IS IGNORED: every row is counted whatever is selected, so choosing one\n"
     "     account shows the whole fleet's 559 beside that account's own config. Every\n"
     "     unselected-case assertion still passes",
     '                if (selectedAccount == null\n'
     '                    || string.Equals(row.AccountName, selectedAccount, StringComparison.OrdinalIgnoreCase))',
     '                if (true)'),

    (TABS,
     "the account filter becomes case-SENSITIVE, so a selection that differs only in case\n"
     "     returns a well-formed 'No data' tab for an account that is present -- a quiet wrong\n"
     "     answer, and the core compares account names OrdinalIgnoreCase everywhere",
     '                    || string.Equals(row.AccountName, selectedAccount, StringComparison.OrdinalIgnoreCase))',
     '                    || string.Equals(row.AccountName, selectedAccount, StringComparison.Ordinal))'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    res = subprocess.run(
        ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
        cwd=REPO, capture_output=True, text=True,
        # The DECODE half of the pin: the Windows default is cp1252, and one non-ASCII character in
        # a test message makes capture_output raise UnicodeDecodeError on a reader THREAD, so
        # res.stdout comes back None and the battery dies before its first mutant.
        encoding='utf-8', errors='replace')
    if 'error CS' in (res.stdout + res.stderr):
        return 'BUILD FAILED'
    m = re.search(r'Passed = \d+, Failed = \d+', res.stdout)
    return m.group(0) if m else 'NO RESULT LINE'


print('=== baseline ===')
baseline = run()
print(' ', baseline)
if 'Failed = 0' not in baseline:
    print('\nREFUSING TO RUN: the baseline is not green, so nothing below scores anything.')
    sys.exit(2)

survivors = []
for path, name, old, new in MUTANTS:
    original = ORIGINALS[path]
    if original.count(old) != 1:
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(path, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    # try/finally as well as the encoding pin above: the pin closes the failure that has actually
    # happened, the finally closes every other way of leaving the loop with a mutant applied.
    try:
        res = run()
        mm = re.search(r'Failed = (\d+)', res)
        killed = ('BUILD FAILED' in res) or ('NO RESULT LINE' in res) \
            or (mm is not None and int(mm.group(1)) > 0)
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
