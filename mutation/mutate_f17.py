"""Mutation battery for F-17: connection visibility and control.

WHY THE FEATURE EXISTS. `P2-115` gave `/api/health` a `feedConnected` that can finally be false --
but a caller reading `false` had no way to ask WHY, and no way to act on it. Worse, the NEGATIVE
half of `P2-115` could not be validated at all, because nothing on the box could change a
connection's state.

⚠️ AND THEN THE BOX SUPPLIED IT ANYWAY, which is the finding worth keeping. While this feature was
being built the broker dropped on its own (market closed), and the sequence is the complete proof
`P2-115` had been missing:

    14:20  dormant Playback, no market   OLD code -> feedConnected: true    (the defect)
    14:54  live broker attached          NEW code -> feedConnected: true    (positive control)
    16:49  broker disconnected           NEW code -> feedConnected: false   (NEGATIVE control)

`accounts: 97` was identical at all three readings. The field now moves with the thing it names.

⚠️ DISCONNECTING IS DESTRUCTIVE ON A TRADING PLATFORM -- it severs the path by which a position is
managed, which is `P1-106`'s family exactly. So `WouldStrand` refuses by default and names what it
would abandon, and the mutants below attack that refusal from both sides. Mutant 5 is the one to
know: an ALWAYS-refuse, which passes every test about the dangerous direction and makes the tool
useless.

⚠️ TWO DEFECTS IN THIS FEATURE WERE FOUND ONLY BY DRIVING THE LIVE BOX, and neither is
representable as a mutant here because both live in the untestable route:
  * `Connection.Connections` returns ZERO rows from the AddOn's HTTP thread -- the endpoint
    answered `count: 0, marketDataConnected: false` on a box with a live broker.
  * Grouping connections by `Options.Name` merged a live broker into a dormant connection and
    reported the dormant one's status for both, contradicting `/api/health` in the same breath.
    Reference identity is the only key that cannot do that.

A crash counts as a kill. Exits non-zero on any survivor, and exits 2 on a red baseline.
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

PLAN = os.path.join(REPO, 'addons', 'BridgeConnectionPlan.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

MUTANTS = [
    # ---- the resolver: P1-90's rule ------------------------------------------------------
    (PLAN,
     "a BLANK connection name becomes a wildcard again. On a disconnect path that means 'sever\n"
     "     every connection on the platform' -- P1-105's `symbol: \"M\"` at a worse site, where the\n"
     "     failure directions are not symmetric",
     '            if (string.IsNullOrWhiteSpace(requested))\n'
     '            {',
     '            if (false)\n'
     '            {'),

    (PLAN,
     "the resolver stops REFUSING an unknown name and reports success with a null connection,\n"
     "     so the caller believes it acted on something. P1-90 verbatim: for a write that means\n"
     "     acting on the wrong target, for a read answering confidently about someone else's",
     '            refusal = "no connection named \'" + requested + "\' exists on this platform. "\n'
     '                    + Available(available);\n'
     '            return false;',
     '            refusal = null;\n'
     '            return true;'),

    (PLAN,
     "the refusal stops NAMING the available connections, so the caller is told 'no' with no way\n"
     "     to correct it. A refusal that does not say what would have worked is a dead end",
     '            refusal = "no connection named \'" + requested + "\' exists on this platform. "\n'
     '                    + Available(available);',
     '            refusal = "no such connection.";'),

    (PLAN,
     "name matching becomes case-SENSITIVE, so the operator's `provider31` stops resolving. Not\n"
     "     dangerous, but it is the difference between a tool that works and one that is\n"
     "     abandoned -- and the canonical-spelling contract goes with it",
     '                    if (string.Equals(available[i], requested.Trim(),\n'
     '                                      StringComparison.OrdinalIgnoreCase))',
     '                    if (string.Equals(available[i], requested.Trim(),\n'
     '                                      StringComparison.Ordinal))'),

    # ---- the safety decision, both directions --------------------------------------------
    (PLAN,
     "THE ONE TO KNOW: WouldStrand always refuses. Every requirement here is about refusing the\n"
     "     DANGEROUS case, so an unconditional refusal satisfies all of them and ships a control\n"
     "     that can never be used. Same shape as P2-115's constant `false`, one layer up -- the\n"
     "     negative test is the only thing that bans it",
     '            if (parts.Count == 0)\n'
     '                return false;',
     '            if (parts.Count == 0)\n'
     '            {\n'
     '                reason = "refused";\n'
     '                return true;\n'
     '            }'),

    (PLAN,
     "an OPEN POSITION stops counting as disruptive, so a disconnect abandons a position the\n"
     "     operator can then neither close nor even see. This is the defect the whole refusal\n"
     "     exists to prevent",
     '            if (openPositions > 0)',
     '            if (false)'),

    (PLAN,
     "a WORKING ORDER stops counting -- the half that gets forgotten. A resting stop is\n"
     "     protection that stays live at the broker after the connection drops, and can be\n"
     "     neither moved nor cancelled from here. P0-9 and P3-110 are both this family",
     '            if (workingOrders > 0)',
     '            if (false)'),

    (PLAN,
     "the two reasons collapse into one wording, so the operator cannot tell whether a position\n"
     "     or an order is in the way. The refusal is only useful if it says WHAT it is protecting",
     '                parts.Add(workingOrders + " working order(s), which stay live at the broker and "\n'
     '                        + "can be neither moved nor cancelled from here");',
     '                parts.Add(workingOrders + " open position(s) to protect");'),

    # ---- the route wiring, via SOURCE gates (labelled -- they prove less) -----------------
    (BRIDGE,
     "SOURCE GATE: the disconnect stops consulting WouldStrand at all, so nothing stands between\n"
     "     a disconnect and an open position. Aimed at a source assertion because\n"
     "     McpBridgeAddOn.cs is in NO test build (P2-27)",
     '            if (action == "disconnect" && strands && !req.Bool("confirmDisruptive"))',
     '            if (false)'),

    (BRIDGE,
     "SOURCE GATE, the sharper one: TryResolve is still CALLED but its refusal is not returned,\n"
     "     so an unresolvable name falls through. A value that is COMPUTED is not a value that is\n"
     "     USED -- P1-105, P2-109 and P2-115 each shipped a gate that missed exactly this",
     '            if (!BridgeConnectionPlan.TryResolve(req.Str("name"), names, out resolved, out refusal))\n'
     '                return new { success = false, action, refused = true,\n'
     '                             error = "UNRESOLVED_CONNECTION", message = refusal };',
     '            BridgeConnectionPlan.TryResolve(req.Str("name"), names, out resolved, out refusal);'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    res = subprocess.run(
        ['dotnet', 'run', '--project', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'],
        cwd=REPO, capture_output=True, text=True,
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
