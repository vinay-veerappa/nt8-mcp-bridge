"""Mutation battery: `Backtest()` must refuse to run a strategy it was not asked for.

THE DEFECT THIS COVERS, measured against the live box 2026-09-04. The Strategy
Analyzer window is REUSED across calls (`_saWindow`) and the strategy was applied
with the lenient `SetP`, so a name that did not resolve failed silently and the
window kept whatever it already had:

    nt_backtest(strategy="@SampleMACrossOver", symbol="NQ 12-26", ...)
      -> summary "_McpTestBot Backtest ... name='_McpTestBot'"
      -> metrics { totalTrades: 0 }

Zero trades, no error. Indistinguishable from the requested strategy simply not
having traded -- which is how a wrong number gets believed. The trigger is NT8's
own convention: the file is `@SampleMACrossOver.cs` while the CLASS is
`SampleMACrossOver`, so the leading `@` resolves to nothing. A second call in the
same session naming `BollingerCrossOver` WAS honoured, which is what establishes
this as an unresolvable-name fallback rather than the argument being ignored.

This is the phase 0.1 config-inheritance defect one level up: not the settings
inherited from a reused window, but the strategy itself. Every NT8 number ever
obtained through this endpoint is only trustworthy if the returned `name=` matched
the request, and nothing checked that.

WHY THE KILLER IS A SOURCE GATE, not the C# suite. `McpBridgeAddOn.cs` is the one
bridge source `tests/BridgeTests.csproj` cannot compile, so no unit test can reach
`Backtest()`. `tools/check_backtest_strategy_echo.py` is the only automated reader
of this contract, and a gate is worth exactly what its mutants prove
([[a-source-gate-must-assert-the-condition]] -- four mutants have beaten a gate of
this shape before). `tools/check_bridge_parses.py` runs alongside it so a mutant
that merely breaks the C# is scored as killed for the right reason.

THE GROUPS:

  1. removing the resolve, the return, the read-back, the comparison, or the
     routing into `paramErrors` -- the five conditions the gate asserts;
  2. the SUBSTRING hazard in `StrategyIdentity`. `MACrossOver` is a substring of
     `SampleMACrossOver`, so an identity test that accepts a substring passes
     exactly the confusion it exists to detect
     ([[a-filter-that-matches-too-much]]);
  3. the two ways the fix can be present and inert: the comparison satisfied by a
     constant, and the diagnosis written somewhere the fail-closed guard never
     reads ([[an-alarm-wired-to-a-dead-output]], [[a-green-that-can-never-be-red]]);
  4. ORDERING. Resolving after the window has been reconfigured is too late -- the
     shared window has already been pointed at something else, so the refusal no
     longer prevents anything ([[rank-refusal-reasons-by-what-binds]]).

A crash counts as a kill. Exits non-zero on any survivor, and exits 2 rather than
running against a red baseline.
"""
import os
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ADDON = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

GATES = [
    os.path.join(REPO, 'tools', 'check_backtest_strategy_echo.py'),
    os.path.join(REPO, 'tools', 'check_bridge_parses.py'),
]

MUTANTS = [
    # ---- group 1: the five asserted conditions ------------------------------
    (ADDON,
     "the requested name is never RESOLVED, so an unknown one falls through to\n"
     "     whatever the reused window already had -- the measured defect verbatim",
     'var stratTypeReq = FindStrategyType(strategy);',
     'var stratTypeReq = typeof(object);'),

    (ADDON,
     "resolved but NOT returned on: the diagnosis is computed and the shared window\n"
     "     is reconfigured anyway, so nothing is prevented",
     '            if (stratTypeReq == null)\n            {',
     '            if (false)\n            {'),

    (ADDON,
     "⚠️ NO READ-BACK: resolving the name proves it is a real strategy, NOT that the\n"
     "     property took. Any SA-side rejection of the write goes unnoticed and the run\n"
     "     is attributed to the wrong strategy",
     'effectiveStrategy = StrategyIdentity(GetP(props, "Strategy"));',
     'effectiveStrategy = StrategyIdentity(strategy);'),

    (ADDON,
     "the read-back is taken and never COMPARED -- a value nobody checked",
     '                    if (!string.Equals(effectiveStrategy, stratTypeReq.Name,\n'
     '                                       StringComparison.OrdinalIgnoreCase))',
     '                    if (false)'),

    (ADDON,
     "⚠️ THE ALARM IS WIRED TO A DEAD OUTPUT: the mismatch is detected correctly and\n"
     "     written somewhere the fail-closed guard never reads, so the run proceeds",
     '                        paramErrors.Add(string.Format(\n'
     '                            "Strategy did not take: requested \'{0}\' (class \'{1}\'), window "',
     '                        effectiveGlobals["_strategyMismatch"] = (string.Format(\n'
     '                            "Strategy did not take: requested \'{0}\' (class \'{1}\'), window "'),

    # ---- group 2: the substring hazard --------------------------------------
    (ADDON,
     "⚠️ IDENTITY BY SUBSTRING: 'MACrossOver' is a substring of 'SampleMACrossOver',\n"
     "     so this accepts precisely the pair of names the check exists to separate",
     '            int dot = s.LastIndexOf(\'.\');\n'
     '            return dot >= 0 && dot < s.Length - 1 ? s.Substring(dot + 1) : s;',
     '            return s.Contains(".") ? s : s;'),

    # ---- group 3: present but inert -----------------------------------------
    (ADDON,
     "the comparison is satisfied by comparing the read-back to ITSELF -- always true,\n"
     "     a green with no reachable red",
     'if (!string.Equals(effectiveStrategy, stratTypeReq.Name,',
     'if (!string.Equals(effectiveStrategy, effectiveStrategy,'),

    # ---- group 4: the refusal made unreachable ------------------------------
    #
    # NOTE ON WHAT THIS MUTANT DOES AND DOES NOT PROVE. It was written as an
    # ORDERING mutant and it is not one: it neuters the guard, so the gate's
    # `returns on an unresolved name` check kills it and the ordering condition is
    # never exercised. Kept because coalescing the null away is a realistic
    # regression in its own right, but RELABELLED
    # ([[check-the-exemplar-belongs-to-the-class]] -- the class was real, the
    # exemplar was not).
    #
    # Ordering is genuinely asserted by `_resolve_precedes_write` in
    # `tools/check_backtest_strategy_echo.py`, and its negative direction by the
    # `reordered` control in that gate's `self_test()`. A source mutant cannot
    # express it here: the resolve sits outside the dispatcher lambda and the
    # write sits inside it, ~60 lines apart, so moving one past the other is not
    # a single anchored replacement.
    (ADDON,
     "the null is coalesced away, so the refusal branch becomes unreachable and an\n"
     "     unresolvable name proceeds to reconfigure the shared window",
     '            var stratTypeReq = FindStrategyType(strategy);\n'
     '            if (stratTypeReq == null)',
     '            var stratTypeReq = FindStrategyType(strategy) ?? typeof(object);\n'
     '            if (false)'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        # newline='' so the exact bytes go back. Two invisible CRs once made six bins
        # cry "A MUTANT IS LIVE" while every mutant had died.
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    """All gates must pass for the tree to be considered green."""
    fails = []
    for gate in GATES:
        try:
            p = subprocess.run(
                [sys.executable, gate], cwd=REPO, capture_output=True, text=True,
                # The DECODE half of the encoding pin: the Windows default is cp1252
                # and one non-ASCII character in gate output makes capture_output raise
                # UnicodeDecodeError on a reader thread, killing the battery before its
                # first mutant.
                encoding='utf-8', errors='replace', timeout=300)
        except subprocess.TimeoutExpired:
            fails.append(os.path.basename(gate) + ':TIMEOUT')
            continue
        except Exception as exc:
            fails.append('%s:%s' % (os.path.basename(gate), type(exc).__name__))
            continue
        if p.returncode != 0:
            fails.append('%s:exit%d' % (os.path.basename(gate), p.returncode))
    return 'GREEN' if not fails else 'RED(' + ','.join(fails) + ')'


print('=== baseline ===')
baseline = run()
print(' ', baseline)
if baseline != 'GREEN':
    print('\nREFUSING TO RUN: the baseline is not green, so nothing below scores '
          'anything. A battery scores a missing RESULT as a kill, which is how 7 '
          'mutants across 5 bins were once mis-scored.')
    sys.exit(2)

survivors = []
for path, name, old, new in MUTANTS:
    original = ORIGINALS[path]
    if original.count(old) != 1:
        # A stale anchor prints [SKIP] and would otherwise score as KILLED.
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(path, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    try:
        res = run()
        killed = res != 'GREEN'
        print('  [%s] %s: %s' % ('KILLED' if killed else 'SURVIVED', name, res))
        if not killed:
            survivors.append(name)
    finally:
        # try/finally as well as the encoding pin: the pin closes the failure that
        # has actually happened, the finally closes every other way of leaving this
        # loop with a mutant applied.
        restore()

restore()
print('\nrestored originals;', run())

print('\n%d/%d mutants killed' % (len(MUTANTS) - len(survivors), len(MUTANTS)))
if survivors:
    print('\nSURVIVORS -- each is a condition the gate does not actually assert:')
    for s in survivors:
        print('  *', s)
# The literal form check_expected_survivors.py looks for. A branched `sys.exit(1)`
# / `sys.exit(0)` is behaviourally identical and the gate cannot see it, so it
# scored this battery as "an exit code nothing checks" -- correctly, since the
# point of the single form is that it is detectable.
sys.exit(1 if survivors else 0)
