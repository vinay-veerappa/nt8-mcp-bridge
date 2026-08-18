"""Mutation battery for P2-127 slice 4: the EVENTS pane and section 4 decision 4's SYSTEM ROW.

These are the last two unbuilt regions of `nt8-riskguard/docs/UI_REDESIGN_DESIGN.md` §4 --
"EVENTS -- filtered to selection" across the bottom, and the row shown when nothing is selected.

⚠️ EVERY THRESHOLD BELOW WAS SET BY MEASURING THE DEPLOYED BOX, 2026-08-17, not by reading code:

    interventions.jsonl        43 766 928 bytes for ONE DAY
      last 3 MB                     10 877 lines
        SUBSCRIBE                    8 148   75%
        ORDER_UPDATE                 1 199   11%     -> 86% is per-tick telemetry
        INTERVENTION                    97           <- what an operator actually wants
        ATM_STOP_ORDER_NOT_FOUND         63
        NAKED_POSITION                   20
        ARMED_ON_START                   84  (identical sentence each time)
        CONNECTION_CHANGE               171  (identical sentence each time)
    guard summary              mode "shadow", isArmed true, unevaluatedRules EMPTY
    copier system              mode "live", isActing TRUE, severity "warn", conflicts 0
    /api/connections           97 accounts: 91 on connection null, 6 on "TPT" all Connected

FOUR OF THOSE CHANGED A DECISION:

  1. 86% telemetry means a pane rendering the tail verbatim shows nothing but `SUBSCRIBE`. That is
     §4.2's hazard by another route -- this page exists so a bad state is seen WITHOUT BEING LOOKED
     FOR, and 8 148 subscribe lines hide 20 naked positions as well as a nav tab would.
  2. 84 identical `ARMED_ON_START` lines mean a denylist alone is not enough: consecutive repeats
     collapse, and the COUNT is kept, because "the connection cycled 84 times" is a different fact
     from "the connection cycled" -- each of those cycles wiped the ATM registry (`P2-136`).
  3. 91 accounts on NO connection mean "any disconnected account = feed down" paints this box
     permanently red. Fail-closed is for what you cannot READ; an account attached to nothing is a
     question that does not apply. [[an-inapplicable-state-is-not-unreadable]].
  4. ⚠️ GUARD "shadow" AND COPIER "live" AND ACTING, in one process at one instant. §2.1 records it:
     "`P3-34` (open) | the copier is ENFORCING regardless of guard mode | a single 'armed' indicator
     would be a lie". Three cells is not tidiness; one cell is factually wrong on this box today.

THE GROUPS:

  1. the telemetry filter, and the DIRECTION of its failure. A denylist means an unknown type
     RENDERS; an allowlist means the events added by the most recent fix are invisible, which is
     exactly when nobody remembers to register them. [[an-alarm-wired-to-a-dead-output]].
  2. collapsing. Consecutive, not global; the count survives; the timestamp is the run's LATEST.
  3. ⚠️ THE SELECTION, WHICH IS WHERE A LITERAL READING OF §4 IS DANGEROUS. Box-wide events are
     logged with an EMPTY account (`LogFromComponent`) or the literal `"SYSTEM"` (`LogEvent`), so
     "filtered to selection" taken literally HIDES `ATM_MONITOR_NO_DISPATCHER`, every `ERROR`, and
     `ATM_BRACKET_RESTORE_FAILED` the moment an operator clicks an account -- which is the normal
     way to use this page.
  4. ordering and the cap. The cap is applied LAST: capping first spends the whole budget on one run
     of 84 identical lines, which is the measured case rather than a hypothetical.
  5. severity, in BOTH directions. An unknown type must not rank routine (the fail-quiet direction);
     ordinary startup lines must, or every boot looks like a problem and the pane cries wolf.
  6. the system row: three cells always, `CONFIGURED and not EVALUATED` red "everywhere, always"
     per §2.1, the feed's inapplicable-vs-unreadable split, and the copier deferring to its own
     producer instead of re-deriving a verdict `P3-122` already had to correct once.
  7. the WIRING, source-gated, because `McpBridgeAddOn.cs` is the one bridge source the test project
     cannot compile and `ui/index.html` is in no test build at all. `P2-138` is what that costs: 199
     lines of view logic, tested, mutation-covered, deployed, and served to nobody.

A crash counts as a kill. Exits non-zero on any survivor, and exits 2 rather than running against a
red baseline.
"""
import os
import re
import subprocess
import sys

# ⚠️ Both halves of the encoding pin; tools/check_batteries_pin_encoding.py is the gate. Without the
# ENCODE half one non-ASCII character in a mutant description raises UnicodeEncodeError inside
# print() AFTER a mutant is applied and BEFORE it is restored, leaving a LIVE MUTANT in the tree.
# [[a-battery-must-reach-its-restore-line]].
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
EVENTS = os.path.join(REPO, 'addons', 'BridgeEventsView.cs')
SYSROW = os.path.join(REPO, 'addons', 'BridgeSystemRow.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')
PAGE = os.path.join(REPO, 'ui', 'index.html')

MUTANTS = [
    # ---- group 1: the telemetry filter ---------------------------------------------------------
    (EVENTS,
     "⚠️ THE MEASURED 86% COMES BACK: nothing is treated as telemetry, so the pane renders 8 148\n"
     "     SUBSCRIBE lines and the 20 naked positions among them are unfindable. §4.2's hazard by\n"
     "     another route -- a bad state that has to be looked for",
     '            return !string.IsNullOrWhiteSpace(eventType) && Telemetry.Contains(eventType.Trim());',
     '            return false;'),

    (EVENTS,
     "⚠️ THE DENYLIST BECOMES AN ALLOWLIST: anything not explicitly ranked is dropped, so the\n"
     "     events added by the most recent fix are invisible until somebody registers them -- and\n"
     "     that is exactly when nobody remembers. An alarm wired to a surface that will not show it",
     '            return !string.IsNullOrWhiteSpace(eventType) && Telemetry.Contains(eventType.Trim());',
     '            return string.IsNullOrWhiteSpace(eventType)\n'
     '                || !(Critical.Contains(eventType.Trim()) || Warning.Contains(eventType.Trim())\n'
     '                     || Routine.Contains(eventType.Trim()));'),

    (EVENTS,
     "FSM_UNDERCOVERED joins the telemetry set, one word away from FSM_TRANSITION which belongs\n"
     "     there. A transition is the machine working; undercovered is the machine reporting a\n"
     "     position with LESS PROTECTION than it should have",
     '            "FSM_TRANSITION",',
     '            "FSM_TRANSITION",\n'
     '            "FSM_UNDERCOVERED",'),

    (EVENTS,
     "⚠️ A REAL DEFECT THIS BATTERY'S SUITE FOUND, RESTORED: date parsing goes back to Json.NET's\n"
     "     default, which converts an ISO-8601 string into a Date TOKEN -- so `timestamp_utc` is\n"
     "     re-rendered in the machine's LOCALE and LOCAL TIME ('8/17/2026 10:30:00 PM') from a field\n"
     "     whose name says UTC. A silent timezone shift on an event log is wrongness an operator\n"
     "     cannot see and would correlate against a chart with",
     '                    reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;',
     '                    reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.DateTime;'),

    # ---- group 2: collapsing --------------------------------------------------------------------
    (EVENTS,
     "repeats stop collapsing, so 84 identical ARMED_ON_START rows fill the pane. The denylist\n"
     "     alone does not touch these -- none of them is telemetry, all of them are one sentence",
     '                bool same = last != null\n'
     '                    && string.Equals(last.EventType, row.EventType, StringComparison.OrdinalIgnoreCase)',
     '                bool same = false\n'
     '                    && string.Equals(last.EventType, row.EventType, StringComparison.OrdinalIgnoreCase)'),

    (EVENTS,
     "the COUNT is dropped: 84 cycles and 1 cycle render identically. Each of those cycles wiped\n"
     "     the ATM bracket registry, so the number is the finding",
     '                    last.Count = last.Count + 1;',
     '                    last.Count = last.Count;'),

    (EVENTS,
     "collapsing becomes GLOBAL rather than consecutive, so this morning's naked position merges\n"
     "     with one from a minute ago into a single row stamped at one of the two times. The\n"
     "     timestamp is the actionable half, so that is worse than either row alone",
     '                EventRow last = result.Count > 0 ? result[result.Count - 1] : null;',
     '                EventRow last = result.FirstOrDefault(r =>\n'
     '                    string.Equals(r.EventType, row.EventType, StringComparison.OrdinalIgnoreCase)\n'
     '                    && string.Equals(r.Account ?? "", row.Account ?? "", StringComparison.OrdinalIgnoreCase));'),

    (EVENTS,
     "a collapsed run keeps its OLDEST timestamp, so a condition still firing now is reported at\n"
     "     the time it started -- and an operator reads it as historical",
     '                    last.Utc = row.Utc;',
     '                    last.Utc = last.Utc;'),

    # ---- group 3: the selection -----------------------------------------------------------------
    (EVENTS,
     "⚠️ §4 READ LITERALLY: box-wide events are hidden the moment an account is selected. Every\n"
     "     ERROR, ATM_MONITOR_NO_DISPATCHER and ATM_BRACKET_RESTORE_FAILED is logged with NO\n"
     "     account, and clicking an account is the normal way to use this page. §4.2 killed\n"
     "     top-level tabs over precisely this",
     '                if (selectedAccount == null\n'
     '                    || row.IsSystemScoped\n'
     '                    || string.Equals(row.Account, selectedAccount, StringComparison.OrdinalIgnoreCase))',
     '                if (selectedAccount == null\n'
     '                    || string.Equals(row.Account, selectedAccount, StringComparison.OrdinalIgnoreCase))'),

    (EVENTS,
     "only a BLANK account counts as box-wide, so the events logged with the literal string\n"
     "     \"SYSTEM\" -- INITIALIZE, ARMED_ON_START and every ERROR -- vanish behind a selection.\n"
     "     Half of them, which is the half that reads as working",
     '            return string.IsNullOrWhiteSpace(account)\n'
     '                || string.Equals(account.Trim(), "SYSTEM", StringComparison.OrdinalIgnoreCase);',
     '            return string.IsNullOrWhiteSpace(account);'),

    (EVENTS,
     "EVERYTHING is box-wide, so the filter is vacuous and one account's selection shows the\n"
     "     whole fleet's events. Every box-wide assertion still passes",
     '            return string.IsNullOrWhiteSpace(account)\n'
     '                || string.Equals(account.Trim(), "SYSTEM", StringComparison.OrdinalIgnoreCase);',
     '            return true;'),

    # ---- group 4: ordering and the cap ---------------------------------------------------------
    (EVENTS,
     "the cap is applied BEFORE collapsing, so one run of 84 identical startup lines consumes the\n"
     "     whole budget and the pane holds nothing else. The measured case, not a hypothetical",
     '            List<EventRow> collapsed = Collapse(parsed);\n'
     '            List<EventRow> filtered = Filter(collapsed, selectedAccount);',
     '            List<EventRow> collapsed = Collapse(max > 0 && parsed.Count > max\n'
     '                ? parsed.Skip(parsed.Count - max).ToList() : parsed);\n'
     '            List<EventRow> filtered = Filter(collapsed, selectedAccount);'),

    (EVENTS,
     "oldest first, so the cap keeps the OLDEST events and the pane shows the start of the tail\n"
     "     while the thing that just happened is cut off",
     '            filtered.Reverse();   // newest first',
     '            // newest first'),

    # ---- group 5: severity, both directions ----------------------------------------------------
    (EVENTS,
     "⚠️ AN UNKNOWN EVENT TYPE RANKS ROUTINE: the fail-QUIET direction, and the events most worth\n"
     "     surfacing are the ones a recent fix added. The newest alarm sorts to the bottom of the\n"
     "     pane in the same colour as a startup message",
     '            if (Routine.Contains(key)) return 3;\n'
     '\n'
     '            return 1;',
     '            return 3;'),

    (EVENTS,
     "an INTERVENTION -- the guard actually acting on an account -- stops being the worst rank,\n"
     "     so the 97 measured interventions render the same as routine startup noise",
     '            if (Critical.Contains(key)) return BridgeFleetView.WorstRank;   // 0',
     '            if (Critical.Contains(key)) return 3;'),

    (EVENTS,
     "the ROUTINE set empties, so every ordinary startup line ranks as a warning and the pane is\n"
     "     amber on every boot. An alarm that is always on is off -- and this is the direction a\n"
     "     loud default fails in if nothing names the routine cases",
     '            "INITIALIZE",\n'
     '            "ARMED_ON_START",\n'
     '            "CONNECTION_CHANGE",',
     '            "__NOTHING_IS_ROUTINE__",'),

    (EVENTS,
     "an EMPTY pane folds to WorstOf(empty), which is UnknownRank -- and UnknownRank IS WorstRank.\n"
     "     A log that was read and held nothing notable would render as the worst thing on screen,\n"
     "     which is the defect CleanRank was added for on the rare tab",
     '            if (ranks.Count == 0) return BridgeInspectorTabs.CleanRank;',
     '            if (ranks.Count == 0) return BridgeFleetView.WorstOf(new int[0]);'),

    # ---- group 6: the system row ---------------------------------------------------------------
    (SYSROW,
     "⚠️ P3-34 RESTORED: the guard and copier cells merge into one, so the box measured today --\n"
     "     guard 'shadow' enforcing nothing while the copier is 'live' and ACTING -- gets a single\n"
     "     indicator that is wrong whichever way it folds",
     '                BuildFeedCell(connections),\n'
     '                BuildGuardCell(guardSummary),\n'
     '                BuildCopierCell(copierSystem)',
     '                BuildFeedCell(connections),\n'
     '                BuildGuardCell(guardSummary)'),

    (SYSROW,
     "⚠️ §2.1's UNCONDITIONAL RULE BREAKS: CONFIGURED-and-not-EVALUATED stops outranking the\n"
     "     mode, so an armed LIVE guard with a rule nothing reads renders clean. Four shipped\n"
     "     defects were that state, and the config file reads as protection in every one",
     '            if (unevaluatedCount > 0)\n'
     '            {\n'
     '                return new SystemCell\n'
     '                {\n'
     '                    Id = GuardCell,',
     '            if (false)\n'
     '            {\n'
     '                return new SystemCell\n'
     '                {\n'
     '                    Id = GuardCell,'),

    (SYSROW,
     "shadow ranks CLEAN, so a guard evaluating everything and ENFORCING NOTHING renders as\n"
     "     protection. That is how an operator comes to believe a -$1,000 daily limit will stop\n"
     "     something",
     '                    Rank = 3,\n'
     '                    Badge = "Shadow",',
     '                    Rank = BridgeInspectorTabs.CleanRank,\n'
     '                    Badge = "Shadow",'),

    (SYSROW,
     "shadow ranks WORST, so the cell is red for the entire shadow-validation period this project\n"
     "     is deliberately in, and an operator learns to ignore it before it ever means anything",
     '                    Rank = 3,\n'
     '                    Badge = "Shadow",',
     '                    Rank = BridgeFleetView.WorstRank,\n'
     '                    Badge = "Shadow",'),

    (SYSROW,
     "⚠️ THE FEED COUNTS ACCOUNTS ATTACHED TO NOTHING: 91 of the 97 on this box, so the cell is\n"
     "     red on every poll forever. This is the defect that painted 95 of 97 accounts as the\n"
     "     worst thing on the fleet tree, in a second place",
     '                    if (string.IsNullOrWhiteSpace(name)) continue;   // attached to nothing',
     '                    if (string.IsNullOrWhiteSpace(name)) name = "(none)";'),

    (SYSROW,
     "NO named connection reads CLEAN, so a box where nothing can answer \"is data arriving\"\n"
     "     reports that it is. §3: liveness is not optional, because a stalled feed and an idle\n"
     "     one look identical",
     '                    Rank = BridgeFleetView.UnknownRank,\n'
     '                    Badge = "No connection",',
     '                    Rank = BridgeInspectorTabs.CleanRank,\n'
     '                    Badge = "No connection",'),

    (SYSROW,
     "a NAMED connection that is not connected reads clean, which leaves the feed cell with no\n"
     "     input at all that makes it red -- a status that cannot go red should not exist",
     '                    Rank = BridgeFleetView.WorstRank,\n'
     '                    Badge = "Down (" + down.Count + ")",',
     '                    Rank = BridgeInspectorTabs.CleanRank,\n'
     '                    Badge = "Down (" + down.Count + ")",'),

    (SYSROW,
     "one account down on a connection stops tainting that connection, so a connection with 5\n"
     "     healthy accounts and 1 disconnected reads fully up",
     '                        byConnection[name] = existing && connected;   // one bad account taints its connection',
     '                        byConnection[name] = existing || connected;'),

    (SYSROW,
     "the copier's severity integer is passed through RAW instead of inverted. Its scale runs the\n"
     "     other way (Ok=0..Critical=3), so a CRITICAL copier sorts as the healthiest thing on the\n"
     "     row -- the same enum trap CopierSnapshotJson exists to stop",
     '            int severityRank = BridgeFleetView.RankOfSystemSeverity(Str(system["severity"]));',
     '            int severityRank = string.Equals(Str(system["severity"]), "ok", StringComparison.OrdinalIgnoreCase) ? 0\n'
     '                : string.Equals(Str(system["severity"]), "info", StringComparison.OrdinalIgnoreCase) ? 1\n'
     '                : string.Equals(Str(system["severity"]), "warn", StringComparison.OrdinalIgnoreCase) ? 2 : 3;'),

    # ⚠️ A MUTANT WAS REPLACED HERE, AND THE REASON MATTERS MORE THAN THE MUTANT DID. The original
    # swapped `WorstOf({severityRank, WorstRank})` for a plain `WorstRank` assignment and SURVIVED --
    # because `WorstRank` is 0 and `WorstOf` returns the MINIMUM, so the two are the same function
    # for every input. The fold was machinery defending a property that cannot be violated, and the
    # acceptance test guarding it asserted `0 <= 0`, which passed under the implementation it existed
    # to reject. THE CODE was simplified to the direct form rather than the test strengthened.
    # [[a-green-that-can-never-be-red]].
    (SYSROW,
     "a config conflict stops mattering at all on the system row, so the one condition the rare\n"
     "     tab and this cell both exist to surface is invisible in the always-visible region",
     '            int rank = conflicts > 0 ? BridgeFleetView.WorstRank : severityRank;',
     '            int rank = severityRank;'),

    (SYSROW,
     "a copier whose config DID NOT LOAD reads clean, so a box where no relationship exists and\n"
     "     nothing would ever be mirrored reports the copier as fine",
     '            if (!loaded)\n'
     '            {\n'
     '                return new SystemCell\n'
     '                {\n'
     '                    Id = CopierCell,\n'
     '                    Label = "Copier",\n'
     '                    Rank = BridgeFleetView.WorstRank,',
     '            if (!loaded)\n'
     '            {\n'
     '                return new SystemCell\n'
     '                {\n'
     '                    Id = CopierCell,\n'
     '                    Label = "Copier",\n'
     '                    Rank = BridgeInspectorTabs.CleanRank,'),

    (SYSROW,
     "an ABSENT copier system object reads clean rather than unknown, so a route that stopped\n"
     "     serving the copier's state renders as a healthy copier",
     '                    Rank = BridgeFleetView.UnknownRank,\n'
     '                    Badge = "Unreadable",\n'
     '                    Detail = "The copier did not return a system state',
     '                    Rank = BridgeInspectorTabs.CleanRank,\n'
     '                    Badge = "Unreadable",\n'
     '                    Detail = "The copier did not return a system state'),

    # ---- group 7: the wiring, source-gated -----------------------------------------------------
    (BRIDGE,
     "⚠️ THE EVENTS PANE IS BUILT AND SERVED TO NOBODY, which is P2-138 verbatim: 199 lines of\n"
     "     view logic with a test file, a battery and a CI job, and no endpoint returning it. The\n"
     "     only surface that defect was detectable on was the operator saying they could not see it",
     '                    var eventRows = BridgeEventsView.Build(',
     '                    var eventRows = new System.Collections.Generic.List<EventRow>(); var unusedRows = BridgeEventsViewUnused(('),

    (BRIDGE,
     "the system row is served to nobody, same shape",
     '                    var systemCells = BridgeSystemRow.Build(',
     '                    var systemCells = new System.Collections.Generic.List<SystemCell>(); var unusedCells = BridgeSystemRowUnused(('),

    (BRIDGE,
     "⚠️ THE TAIL READ STOPS SHARING WRITE ACCESS. The guard's own five-second sweep APPENDS to\n"
     "     interventions.jsonl, so a page poll would either throw here or make the audit flush\n"
     "     fail -- a poll costing the audit record is a strictly worse trade than an empty pane",
     '                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))',
     '                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))'),

    (PAGE,
     "the page stops rendering the events pane, so the payload is served and displayed nowhere --\n"
     "     P2-138 from the other end, and every server-side test still passes",
     'function renderEvents(data) {',
     'function renderEventsNotWired(data) {'),

    (PAGE,
     "the page stops rendering the system row, same shape",
     'function renderSystemRow(data) {',
     'function renderSystemRowNotWired(data) {'),

    (PAGE,
     "the page re-sorts the events itself, undoing the one ordering decision that had to happen\n"
     "     in a particular place -- newest-first is applied AFTER repeats are collapsed",
     "  el.innerHTML = '<table>' + rows.map(function (r) {",
     "  el.innerHTML = '<table>' + rows.sort(function (a, b) { return a.rank - b.rank; }).map(function (r) {"),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    try:
        p = subprocess.run(
            ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
            cwd=REPO, capture_output=True, text=True,
            # The DECODE half of the pin: the Windows default is cp1252, and one non-ASCII character
            # in a test message makes capture_output raise UnicodeDecodeError on a READER THREAD, so
            # res.stdout comes back None and the battery dies before its first mutant.
            encoding='utf-8', errors='replace', timeout=900)
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'
    out = (p.stdout or '') + (p.stderr or '')
    if 'error CS' in out:
        return 'BUILD FAILED'
    m = re.search(r'Passed = \d+, Failed = \d+', out)
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
    # try/finally as well as the encoding pin: the pin closes the failure that has actually
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
