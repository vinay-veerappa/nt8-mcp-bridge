#!/usr/bin/env python3
"""Every mutation battery on disk must be EXECUTED by this repo's CI workflow.

⚠️ **WHY IT EXISTS HERE, and it is not a precaution.** Session 38 discovered that
`nt8-mcp-bridge`'s CI **ran neither of its two batteries** -- `mutate_p190.py` and
`mutate_p0104.py` had been on disk and unwired since the day each was written. The core repo
has had this gate since session 20, and it is **per-repo**: it globs the checkout it lives in,
so it could never have seen this side. Nothing watched here at all.

That was fixed by adding the two steps by hand, which fixes the instance and not the class --
a third battery could arrive tomorrow and be unwired for exactly as long. Session 39 added
`mutate_p1106.py` and ported the gate in the same commit, so the gap closes for good.

⚠️ **Comments are stripped before matching**, for the reason the core's version records: a
name appearing in a prose comment above a step is not a name being run. A gate that accepts a
comment as evidence is *a gate nobody reads is a comment* inverted, and this repo's workflow
comments name every battery they describe.

The name must appear in a form that actually runs something:

  * a `run:` line naming it, or
  * `battery: mutate_x.py` (a matrix entry, if this workflow ever grows one).

It also fails on a DUPLICATE entry: two steps for one battery re-prove the same thing and
report as if two things were proven.

⚠️ **IT ALSO COVERS THE GATE SCRIPTS, and that is not scope creep (2026-08-18).** The
docstring above says fixing the two unwired batteries fixed the instance and not the class.
It fixed the class *for batteries*. One directory over, `tools/check_bridge_parses.py` had
been on disk and unwired in CI since the day it was written -- the gate covering
`McpBridgeAddOn.cs`, the one bridge source `BridgeTests.csproj` cannot compile, so a stray
brace there was findable only by deploying. A syntax error in ANY addon `.cs` stops **every**
addon loading, RiskGuard included. Both meta-gates globbed `mutation/` only, so no gate
script in either repo was ever required to run anywhere. Same shape, one directory over,
and neither repo could see it.

Gates are matched on their REPO-RELATIVE PATH, not their bare name, so invoking one from the
wrong directory does not count as wiring it.

The filename still says `battery` because the plan and the handover reference it by that name
in a dozen places; renaming it would make those references stale to fix a cosmetic problem.

Exit 0 = every battery AND every gate is wired exactly once. Exit 1 = at least one is not.
Exit 2 = there is nothing to check, which is not a pass.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
MUTATION = REPO / 'mutation'
TOOLS = REPO / 'tools'
WORKFLOW = REPO / '.github' / 'workflows' / 'ci.yml'


def strip_comments(text: str) -> str:
    """Drop YAML comments. A `#` inside a quoted string is not a comment, but no battery
    name is ever quoted in this workflow, so the simple rule is the honest one here."""
    out = []
    for line in text.splitlines():
        idx = line.find('#')
        out.append(line if idx < 0 else line[:idx])
    return '\n'.join(out)


def main() -> int:
    if not WORKFLOW.exists():
        print('REFUSING: %s not found. A check that inspects nothing reports nothing.' % WORKFLOW)
        return 2

    batteries = sorted(p.name for p in MUTATION.glob('mutate_*.py'))
    if not batteries:
        print('REFUSING: no mutate_*.py under mutation/. This check would pass vacuously.')
        return 2

    body = strip_comments(WORKFLOW.read_text(encoding='utf-8'))

    problems = []
    for name in batteries:
        escaped = re.escape(name)
        runs = len(re.findall(r'run:[^\n]*' + escaped, body))
        matrix = len(re.findall(r'battery:\s*' + escaped, body))
        total = runs + matrix

        if total == 0:
            problems.append(
                '%s is on disk and CI never runs it. A battery nothing executes proves\n'
                '    nothing, and it looks identical to one that passes.' % name)
        elif total > 1:
            problems.append(
                '%s is wired %d times. The duplicate re-proves the same mutants and\n'
                '    reports as though two things were checked.' % (name, total))

        print('  %-24s %s' % (name, 'wired' if total == 1 else 'PROBLEM (%d)' % total))

    # ---- the gate scripts ------------------------------------------------------------
    # Matched on the repo-relative path: `python tools/check_x.py` is wiring, and the same
    # basename invoked from another directory is not.
    gates = sorted(
        set(g.relative_to(REPO).as_posix()
            for d in (TOOLS, MUTATION) if d.is_dir()
            for g in d.glob('check_*.py')))
    if not gates:
        print('REFUSING: no check_*.py under tools/ or mutation/. This half would pass '
              'vacuously.')
        return 2

    print('')
    for rel in gates:
        total = len(re.findall(r'run:[^\n]*' + re.escape(rel), body))
        if total == 0:
            problems.append(
                '%s is on disk and CI never runs it. A gate nobody executes is a gate that\n'
                '    cannot fail, and it looks exactly like one that passes.' % rel)
        elif total > 1:
            problems.append(
                '%s is wired %d times. The duplicate re-proves the same thing and reports\n'
                '    as though two things were checked.' % (rel, total))

        print('  %-34s %s' % (rel, 'wired' if total == 1 else 'PROBLEM (%d)' % total))

    print('')
    if problems:
        print('FAIL: %d batter(y/ies) and/or gate(s) are not wired exactly once:\n'
              % len(problems))
        for p in problems:
            print('  * ' + p + '\n')
        return 1

    print('OK: all %d batteries and all %d gates are executed by ci.yml, exactly once each.'
          % (len(batteries), len(gates)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
