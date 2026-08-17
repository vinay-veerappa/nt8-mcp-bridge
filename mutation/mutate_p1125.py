"""Mutation battery for P1-125 and P3-122: the copier's mode, and which refusal binds.

P1-125. The browser UI at http://localhost:7890/ui -- the surface the operator actually uses --
showed the GUARD's mode in its header (`mode shadow - armed - cannot act`) and said NOTHING about
the copier's. The copier has had its own live/shadow/disabled switch since P3-34, deliberately
separate so the sim can copy while the guard observes. Measured before the fix:

    copierMode / notEnforcingReason / configConflicts in ui/index.html   ->  0
    the same three in McpBridgeAddOn.cs + CopierEnforcementView.cs       ->  21

A `disabled` copier -- submitting nothing, anywhere -- rendered identically to a working one. And
reporting ONE of two modes is worse than reporting neither: the reader concludes both were covered.

P3-122. The reason text was already built, and its ORDERING was wrong. `NotEnforcingReason` named
the mode LAST, with the stated reason that it "is the newest reason and the one an operator will
not think to check" -- right about which reason SURPRISES, wrong about which one BINDS:

    enabled | NOT armed | shadow  ->  "copies to SIMULATION followers only"   FALSE

In shadow the copy path blocks at COPY_BLOCKED_COPIER_SHADOW before any follower is reached, so it
copies to simulation followers too. RANK REFUSAL REASONS BY WHAT BINDS.

⚠️ THE TWO TICKETS ARE ONE BATTERY BECAUSE THEY ARE ONE FIX. A defect in a string that nothing
displays is not reachable by an operator; rendering the reason is what makes its ordering matter.

THE GROUPS BELOW, and what each is defending:
  1. the ordering itself, in both directions -- restoring the defect, AND deleting the sentence
     that is correct when the copier IS acting. A reorder breaks the second half silently.
  2. the sentence's claim about ARMING, which the reorder made reachable by unarmed rows.
  3. the severity wire contract: a NAME, never a number, and an unmapped rank is not health.
  4. the not-loaded cell, which is the state that must not look like a blank.
  5. SOURCE GATES on the route (McpBridgeAddOn.cs is in no test build -- P2-27) and on the page
     (ui/index.html is in no test build and never will be). Labelled: they prove wiring, not
     behaviour. `nt_compile` and the live read are the evidence for that half.

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

VIEW = os.path.join(REPO, 'addons', 'CopierEnforcementView.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')
PAGE = os.path.join(REPO, 'ui', 'index.html')

# (target file, description, find, replace)
MUTANTS = [
    # ---- group 1: the ordering, in both directions ----------------------------------------
    (VIEW,
     "THE SHIPPED DEFECT, restored exactly: the mode branch is taken only when the\n"
     "     relationship is ALSO armed, so an enabled, unarmed row under a `shadow` copier falls\n"
     "     through to the simulation sentence -- the P3-122 table's second line, verbatim",
     '            if (!copierModeIsActing)\n'
     '            {',
     '            if (!copierModeIsActing && armedForLive)\n'
     '            {'),

    (VIEW,
     "THE OPPOSITE FAILURE, and the half a reordering breaks in silence: the simulation\n"
     "     sentence is deleted, so an unarmed relationship under a LIVE copier -- which really\n"
     "     does copy to sim followers only -- is told about the mode instead. Every assertion\n"
     "     about the defect passes under this; only the positive control catches it",
     '            return new CopierRefusal("sim only",\n'
     '                "the relationship is not ArmedForLive, so it copies to SIMULATION "\n'
     '                + "followers only -- a live follower is refused.");',
     '            return new CopierRefusal("not enforcing",\n'
     '                "the relationship is not enforcing.");'),

    (VIEW,
     "the relationship's OWN state stops winning over the global one, so a row the operator\n"
     "     switched off is explained by the copier mode. Ranking by what binds is not ranking by\n"
     "     what is WIDEST: `disabled` is the switch they would actually go and flip",
     '            if (!isEnabled)\n'
     '                return new CopierRefusal("disabled", "the relationship is disabled.");',
     '            if (false)\n'
     '                return new CopierRefusal("disabled", "the relationship is disabled.");'),

    (VIEW,
     "the two lengths stop being one decision: NotEnforcingReason returns the LABEL. Two\n"
     "     renderings of one refusal is the whole design -- a page free to pick its own short\n"
     "     form is free to disagree with the sentence beside it, which is P3-122 in miniature",
     '            return refusal == null ? null : refusal.Sentence;',
     '            return refusal == null ? null : refusal.Label;'),

    # ---- group 2: what the moved sentence CLAIMS about arming ------------------------------
    (VIEW,
     "the old text's arming claim comes back unconditionally. This is the trap in the reorder\n"
     "     itself: the mode sentence used to be reachable only by armed rows and said so, and\n"
     "     moving it above `armedForLive` makes that assertion false for exactly the row the\n"
     "     ticket was filed about",
     '                string armedClause = armedForLive ? " and armed for live" : "";',
     '                string armedClause = " and armed for live";'),

    (VIEW,
     "and the other way: the clause is never stated, so an armed and an unarmed relationship\n"
     "     under a shadow copier read identically. They differ in what the operator has to do\n"
     "     next once the mode is fixed",
     '                string armedClause = armedForLive ? " and armed for live" : "";',
     '                string armedClause = "";'),

    # ---- group 3: the severity wire contract ----------------------------------------------
    (VIEW,
     "an unmapped severity rank reads as OK. A member added to CopierStatusSeverity upstream\n"
     "     and not mapped here is not evidence of health -- fail loud, in the direction that\n"
     "     gets looked at (CopierSnapshotJson.SeverityRank ranks an unknown verdict worst for\n"
     "     the same reason)",
     '            return "critical";\n'
     '        }',
     '            return "ok";\n'
     '        }'),

    (VIEW,
     "severity crosses the wire as a NUMBER. Load-bearing, and not a style point: the rows in\n"
     "     the SAME payload carry a numeric `severity` from CopierSnapshotJson where **0 is the\n"
     "     WORST**, so a page keying its colour off the wrong polarity paints an ORPHAN green",
     '            return "critical";\n'
     '        }',
     '            return rank.ToString();\n'
     '        }'),

    (VIEW,
     "warn is reported as info, so a `shadow` copier renders in the same grey as a working one.\n"
     "     The colour IS the finding on a page whose whole claim is that a non-acting copier is\n"
     "     visible without being looked for",
     '                case 2: return "warn";',
     '                case 2: return "info";'),

    (VIEW,
     "a blank mode is passed through instead of reading '(unset)'. An empty header field says\n"
     "     'nothing to report', which is the one thing an unset copier mode does not mean",
     '                Mode = string.IsNullOrWhiteSpace(copierMode) ? "(unset)" : copierMode,',
     '                Mode = copierMode,'),

    (VIEW,
     "the headline is REWORDED here rather than passed through. The words are\n"
     "     CopierStatusView's, shared with the WPF window; a second dialect at this surface is\n"
     "     how the two screens start describing one copier differently, which is P3-122",
     '                Headline = headline,\n'
     '                Detail = detail,',
     '                Headline = "COPIER",\n'
     '                Detail = detail,'),

    (VIEW,
     "the conflict count is dropped, so a follower copied TWICE -- covered by both a direct\n"
     "     relationship and a group -- is invisible on the one line that summarises the copier.\n"
     "     No single row can show it, because each row is individually correct",
     '                ConfigConflicts = configConflicts',
     '                ConfigConflicts = 0'),

    (VIEW,
     "the cell claims to be acting whatever the engine said. The severity would still be right,\n"
     "     so the banner still appears -- and the header, which is the always-visible half,\n"
     "     would read `copier shadow - acting`",
     '                IsActing = copierModeIsActing,',
     '                IsActing = true,'),

    (VIEW,
     "a refusal is produced even for a relationship that IS enforcing. An always-on alarm is\n"
     "     off (P2-98's FILL_NOT_MEASURED, P3-30's audit, P2-108): a `Note` column with a\n"
     "     sentence on every row trains the operator to skip the column",
     '            if (IsEnforcing(isEnabled, armedForLive, copierModeIsActing))\n'
     '                return null;',
     '            if (false)\n'
     '                return null;'),

    # ---- group 4: the state that must not look like a blank --------------------------------
    (VIEW,
     "a box with NO copier loaded reports ok. `TradeCopierEngine.Instance` is null when the\n"
     "     addon failed to load, and the header still has to say something -- a quiet indicator\n"
     "     there is read as fine",
     '                Loaded = false,\n'
     '                Mode = "(not loaded)",\n'
     '                IsActing = false,\n'
     '                Severity = "critical",',
     '                Loaded = false,\n'
     '                Mode = "(not loaded)",\n'
     '                IsActing = false,\n'
     '                Severity = "ok",'),

    (VIEW,
     "and a missing copier claims to be ACTING. Every caller that reads the cell to answer\n"
     "     'can anything be copied?' gets the unsafe answer without ever knowing this case\n"
     "     exists",
     '                Loaded = false,\n'
     '                Mode = "(not loaded)",\n'
     '                IsActing = false,',
     '                Loaded = false,\n'
     '                Mode = "(not loaded)",\n'
     '                IsActing = true,'),

    # ---- group 5: the wiring, via SOURCE gates (labelled -- they prove less) ---------------
    (BRIDGE,
     "SOURCE GATE: the route reverts to serving core's rows alone, with no system block. This\n"
     "     is the filed defect at the seam -- every field existed and the page was sent none of\n"
     "     them. Aimed at a source assertion because McpBridgeAddOn.cs is in NO test build",
     '                case "/api/copier/snapshot":\n'
     '                    return GetCopierSnapshot();',
     '                case "/api/copier/snapshot":\n'
     '                    return JObject.Parse(CopierSnapshotJson.ToJson(\n'
     '                        TradeCopierEngine.Instance == null ? null : TradeCopierEngine.Instance.GetSnapshot()));'),

    (BRIDGE,
     "SOURCE GATE, and the sharper one: Describe is still CALLED and its answer is thrown\n"
     "     away, the severity hardcoded to ok. A gate that a producer is invoked passes under\n"
     "     this -- a value that is COMPUTED is not a value that is USED, which four mutants in\n"
     "     these repos have now proved in a row",
     '                    copierMode, acting, (int)headline.Severity,\n'
     '                    headline.Text, headline.Detail, conflicts == null ? 0 : conflicts.Count),',
     '                    copierMode, acting, 0,\n'
     '                    "COPIER", "", conflicts == null ? 0 : conflicts.Count),'),

    (BRIDGE,
     "SOURCE GATE: the per-row refusal is dropped, so P3-122's ordering goes back to being a\n"
     "     defect in a string nothing displays -- correct, and unreachable by the operator",
     # P2-138 renamed the loop variable to rowObj when it added the JObject cast.
     '                rowObj["notEnforcingLabel"] = refusal == null ? null : refusal.Label;\n'
     '                rowObj["notEnforcingReason"] = refusal == null ? null : refusal.Sentence;',
     '                rowObj["enforcingChecked"] = true;'),

    (BRIDGE,
     "SOURCE GATE: the null-engine branch is removed, so a box with no copier throws instead\n"
     "     of answering. The route is reached by a 5-second poll; an exception there is a page\n"
     "     that says nothing at all about a system that is doing nothing at all",
     # P2-138 added the empty-fleet assignment between these two lines.
     '                payload["system"] = JObject.FromObject(CopierEnforcementView.NotLoadedCell(), camel);\n'
     '                payload["fleet"] = new JArray();\n'
     '                return payload;',
     '                return payload;'),

    (PAGE,
     "SOURCE GATE on the static page, which is in no test build and no battery can execute:\n"
     "     the header indicator is rendered only AFTER the error branch has returned, so the\n"
     "     one state where the page knows least leaves the indicator blank -- and a blank reads\n"
     "     as fine. This is the weakest evidence in the battery and it is labelled as such",
     '  renderCopierSystem(data.system);\n',
     ''),

    (PAGE,
     "SOURCE GATE: the page starts deciding for itself whether the copier is acting, by\n"
     "     comparing the mode to a literal. It agrees with the engine TODAY and drifts the\n"
     "     moment the set of acting modes changes -- P1-100, P2-98/P1-99, P1-105 and P3-111 are\n"
     "     all one predicate copied to a second reader",
     "    + ' &middot; ' + (sys.isActing ? 'acting'",
     "    + ' &middot; ' + ((sys.mode === \"live\") ? 'acting'"),
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
