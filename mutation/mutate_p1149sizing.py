# -*- coding: utf-8 -*-
"""Mutation battery for P1-149: the contract cap, applied BEFORE the order exists.

MEASURED 2026-08-18 with `Sizing.MaxContractsPerAccount: 10` live in the config:

    Sim101   sell 1000 MES via /api/order/atm  -> FILLED. -$1,213 slippage on the fill alone.
    FUNDED   sell  501 MES placed BY HAND      -> REJECTED, by the PROP FIRM, not by us:
             "Your maximum order quantity has been met... Limit: 60 Current: 501"

Control surface before this ticket: bridge NONE, guard config 10, prop firm 60.
`MaxContractsPerAccount` had four readers and the component that PLACES THE ORDER was not one.

THE GROUPS:

  1. THE ANTI-TRAP RULE, and it is the one that must never regress. A cap that refuses the order
     CLOSING an over-cap position manufactures the state it bans -- `P1-106` one file over, where a
     lockout refused the exit and trapped the operator inside the exposure the rule existed to
     limit. Every mutant here turns a legal exit into a refusal.
  2. WHAT IS MEASURED. The check is on the RESULTING position, not the order quantity, because the
     guard's reactive `MAX_SIZE_BREACH` asks `pos.Quantity > limit`. Two halves that measure
     different things disagree about the same account, and the visible symptom is the guard
     flattening an order the bridge had just approved.
  3. AGREEING WITH `GuardRules`. A cap of 0 is reported as `Off("no per-account contract cap")`.
     A gate that enforces what the inventory calls OFF is worse than either behaviour alone.
  4. BOUNDARIES AND SIGNS. `Position.Quantity` is ABSOLUTE on NT8 -- `P0-96` is the copier reading
     the SIGN and DOUBLING a follower's short behind 1311 green tests.
  5. ⚠️ EVERY PATH, which is the whole point of the ticket. The defect was measured on
     `PlaceAtmOrder`; fixing only it leaves `/api/order` and `/api/order/oco` as open as before.
     [[a-second-reader-of-the-same-state]].
  6. ONE SOURCE FOR THE NUMBER. The cap is asked of the guard. A literal in the bridge would be a
     fifth reader of a number four things already read, and the drift would be silent.

⚠️ A CRASH IS NOT AUTOMATICALLY A KILL -- see `P2-148`. The harness prints its result line LAST, so
an unhandled exception leaves 'NO RESULT LINE', which scored as a kill unconditionally and hid a
false kill in the sibling repo's P2-136 battery for three sessions. A crash counts here only if the
run printed at least one `[FAIL]` first.

Exits non-zero on any survivor, and exits 2 rather than running against a red baseline.
"""
import os
import re
import subprocess
import sys

# ⚠️ REQUIRED, gate: tools/check_batteries_pin_encoding.py. Without it one non-ASCII character in a
# description raises UnicodeEncodeError inside print() on a cp1252 console -- AFTER a mutant is
# applied and BEFORE it is restored, leaving a LIVE MUTANT in the tree.
# [[a-battery-must-reach-its-restore-line]].
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
GATE = os.path.join(REPO, 'addons', 'BridgeSizingGate.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

MUTANTS = [
    # ---- group 1: the anti-trap rule -------------------------------------------------------
    (GATE,
     "⚠️ THE ANTI-TRAP RULE GOES AWAY: a reducing order is no longer waved through, so a\n"
     "     Sell 50 against a long 50 is judged on the cap and REFUSED. The operator is locked\n"
     "     inside the exact exposure the cap exists to prevent -- P1-106 verbatim",
     '            if (opposes && orderQuantity <= held)',
     '            if (false)'),

    # ⚠️ REPLACED AFTER THE FIRST RUN, and the replaced mutant is the lesson. It made the
    # reducing test STRICT (`< held` rather than `<= held`) and was described as 'an exact flatten
    # is refused'. It SURVIVED because it is EQUIVALENT: an exact flatten falls through to the cap
    # test, computes `resulting = 0`, and 0 is under every cap -- so both forms agree and no test
    # can distinguish them.
    #
    # ⚠️ The description was wrong about WHICH case the anti-trap branch protects. It is not
    # the full flatten -- the arithmetic already handles that -- it is the PARTIAL reduction that
    # leaves a still-over-cap position: long 50, Sell 30 leaves 20, over a cap of 10, and without
    # the branch that is REFUSED. Mutant 1 (`if (false)`) is what actually pins the branch.
    (GATE,
     "a reducing order reports the position it STARTED with rather than what it leaves, so a\n"
     "     partial exit from long 50 says it leaves 50. The verdict stays right and the number\n"
     "     the caller logs is wrong -- and the number is the half an operator reads",
     '                    ResultingQuantity = held - orderQuantity',
     '                    ResultingQuantity = held'),

    (GATE,
     "the anti-trap rule moves BELOW the cap test, so it can no longer pre-empt it. Ordering is\n"
     "     the whole of this rule: a branch that runs second never runs at all",
     '            if (opposes && orderQuantity <= held)\n'
     '            {\n'
     '                return new BridgeSizingDecision\n'
     '                {\n'
     '                    Allowed = true,\n'
     '                    ResultingQuantity = held - orderQuantity\n'
     '                };\n'
     '            }',
     '            if (opposes && orderQuantity <= held && maxContracts < 0)\n'
     '            {\n'
     '                return new BridgeSizingDecision\n'
     '                {\n'
     '                    Allowed = true,\n'
     '                    ResultingQuantity = held - orderQuantity\n'
     '                };\n'
     '            }'),

    # ---- group 2: what is measured ---------------------------------------------------------
    (GATE,
     "the existing position stops counting, so only the ORDER is measured. Long 8 with a cap of\n"
     "     10 admits a Buy 5 that leaves 13, and MAX_SIZE_BREACH then flattens all 13 -- which\n"
     "     reads to an operator as the guard flattening an order the bridge approved",
     '            int resulting = opposes ? orderQuantity - held : held + orderQuantity;',
     '            int resulting = orderQuantity;'),

    (GATE,
     "a reversal is measured as if it only added, so long 8 + Sell 20 reads as 28 rather than a\n"
     "     short 12. Over-refusing looks safe and is not: it refuses legal exits-plus-entries and\n"
     "     teaches the operator the gate is wrong",
     '            int resulting = opposes ? orderQuantity - held : held + orderQuantity;',
     '            int resulting = held + orderQuantity;'),

    # ---- group 3: agreeing with GuardRules -------------------------------------------------
    (GATE,
     "a cap of ZERO becomes enforcing, so an account GuardRules reports as 'no per-account\n"
     "     contract cap' refuses every order. The inventory screen and the order path then say\n"
     "     opposite things about the same setting",
     '            if (maxContracts <= 0)',
     '            if (maxContracts < 0)'),

    # ---- group 4: boundaries and signs -----------------------------------------------------
    (GATE,
     "the cap boundary becomes exclusive, so a position exactly AT the configured maximum is\n"
     "     refused and the cap silently means one less than it says",
     '            if (resulting <= maxContracts)',
     '            if (resulting < maxContracts)'),

    (GATE,
     "⚠️ the position quantity is used SIGNED. P0-96's shape: -8 held makes Buy 5 read as -3,\n"
     "     under the cap, and the order is ADMITTED. That defect doubled a real follower short\n"
     "     behind 1311 green tests",
     '            int held = positionQuantity < 0 ? -positionQuantity : positionQuantity;',
     '            int held = positionQuantity;'),

    (GATE,
     "the direction test inverts, so an order on the SAME side as the position is treated as\n"
     "     reducing. Adding to a position is then the thing that is never refused",
     '            bool opposes = !flat && (longNow != buying);',
     '            bool opposes = !flat && (longNow == buying);'),

    # ---- group 5: every path, which is the point of the ticket -----------------------------
    (BRIDGE,
     "⚠️ THE PLAIN ORDER PATH stops consulting the gate, while the other two keep doing so.\n"
     "     This is the ticket's whole subject: the defect was measured on the ATM path and fixing\n"
     "     only that path leaves /api/order exactly as open as it was",
     '            var sizing = BridgeSizingGate.Evaluate(',
     '            var sizing = BridgeSizingGateDISABLED.Evaluate('),

    (BRIDGE,
     "the OCO path stops consulting the gate. An OCO's entry adds exposure just as a plain order\n"
     "     does, and its bracket legs then rest against a position that should never have opened",
     '            var ocoSizing = BridgeSizingGate.Evaluate(',
     '            var ocoSizing = BridgeSizingGateDISABLED.Evaluate('),

    (BRIDGE,
     "the ATM path -- the one the defect was MEASURED on, sell 1000 filled -- stops consulting\n"
     "     the gate",
     '            var atmSizing = BridgeSizingGate.Evaluate(',
     '            var atmSizing = BridgeSizingGateDISABLED.Evaluate('),

    (BRIDGE,
     "the ATM path evaluates the gate and IGNORES the verdict. The call is still there for any\n"
     "     source scan to find, and the order is placed regardless -- an alarm wired to an output\n"
     "     nobody reads. [[an-alarm-wired-to-a-dead-output]]",
     '            if (!atmSizing.Allowed)\n'
     '                return new { error = atmSizing.Reason };',
     '            if (false)\n'
     '                return new { error = atmSizing.Reason };'),

    # ---- group 6: one source for the number ------------------------------------------------
    (BRIDGE,
     "the bridge stops asking the GUARD for the cap and invents its own. A fifth reader of a\n"
     "     number four things already read, free to drift from the config the operator edits",
     '            try { return RiskGuardAddOn.Instance.EffectiveMaxContracts(account, instrumentName); }',
     '            try { return 10; }'),

    (BRIDGE,
     "a missing guard fails CLOSED rather than open, so with the guard unloaded every order is\n"
     "     refused. The bridge must keep working without the guard -- and the failure direction\n"
     "     here is a judgement, so it gets a mutant rather than a comment",
     '            if (RiskGuardAddOn.Instance == null) return 0;',
     '            if (RiskGuardAddOn.Instance == null) return 1;'),
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
            encoding='utf-8', errors='replace', timeout=900)
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'
    out = (p.stdout or '') + (p.stderr or '')
    if 'error CS' in out:
        return 'BUILD FAILED'
    m = re.search(r'Passed = \d+, Failed = \d+', out)
    if not m and '[FAIL]' not in out:
        # P2-148. A crash is not a detection.
        return 'NO RESULT LINE + NO ASSERTION FAILED (harness died undetected)'
    return m.group(0) if m else 'NO RESULT LINE'


print('=== baseline ===')
baseline = run()
print('  %s' % baseline)
if 'Failed = 0' not in baseline:
    print('baseline is RED; a battery against a red baseline scores nothing')
    sys.exit(2)

survivors = []
for target, name, old, new in MUTANTS:
    original = ORIGINALS[target]
    if original.count(old) != 1:
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(target, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    try:
        res = run()
        mm = re.search(r'Failed = (\d+)', res)
        undetected_crash = 'NO ASSERTION FAILED' in res
        killed = (not undetected_crash) and (
            ('BUILD FAILED' in res) or ('NO RESULT LINE' in res)
            or (mm is not None and int(mm.group(1)) > 0))
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
