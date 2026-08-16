"""A battery that declares a mutant EXPECTED to survive must not exit 1 for it.

⚠️ **WHAT THIS CATCHES, measured 2026-08-14 (session 36).** Two batteries described a mutant in
prose as *"Expected to SURVIVE"* -- correctly, with the reason no test can reach it -- and then
ended with `sys.exit(1 if survivors else 0)`, which fails for that mutant every single time. Both
were therefore **red by design**, and CI failed on **10 consecutive pushes across three sessions**
before anyone ran `gh run list`. Nobody was reading the result, because the result was always the
same.

That is worse than a doc going stale. A gate that cannot pass stops being a gate: it trains its
readers to skip a signal that is otherwise the only thing checking 24 batteries, 263 anchors and
1328 tests on every push. The repo's own rule -- *a gate nobody reads is a comment* -- had landed
inside the gate.

**The check, and it fails in BOTH directions** so neither half can rot:

  * a battery with an `EXPECTED SURVIVOR:` mutant MUST hand its verdict to `_battery.finish`,
    which knows the difference between a declared survivor and a real one;
  * a battery with NO such mutant MUST keep the plain `sys.exit(1 if survivors else 0)`. Routing
    a battery through the helper "just in case" would let a future expected-survivor marker be
    added with nothing forcing a second look at whether it is honest.

Exits non-zero on any violation. Wired into CI beside `check_ci_runs_every_battery.py`.

⚠️ **PORTED FROM `nt8-riskguard` 2026-08-16 (session 47), and it is the FOURTH gate found
present in one repo and absent in the other.** It was absent here through every one of this
repo's eight batteries, which is the shape the class always takes: nothing is wrong yet, so
nothing asks for the gate.

⚠️ **AND THE PORT WAS NOT ONE `cp`, which is the part worth reading.** All eight batteries
here use the plain exit and none declares, so a verbatim copy PASSES on day one -- while
prescribing a remedy that does not exist in this repo: **there is no `mutation/_battery.py`
here.** A gate whose failure message names a helper you cannot import sends its reader in a
circle at exactly the moment they are already stuck. So the helper's ABSENCE is now checked
too, and the remedy text is chosen from what this repo actually has. Copy a gate, then read
what it tells you to do about the repo you copied it into.
"""
import ast
import os
import re
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
MUTATION = os.path.join(REPO, 'mutation')

MARKER = 'EXPECTED SURVIVOR:'
HELPER_CALL = '_battery.finish('
HELPER_MODULE = os.path.join(MUTATION, '_battery.py')
PLAIN_EXIT = 'sys.exit(1 if survivors else 0)'


def helper_remedy():
    """What to actually DO -- which differs by repo, so it is derived, not hardcoded."""
    if os.path.exists(HELPER_MODULE):
        return 'Hand the verdict to _battery.finish(survivors, MUTANTS) instead.'
    return ('This repo has NO mutation/_battery.py, so there is nothing to hand the verdict\n'
            '    to yet. Port it from nt8-riskguard in the SAME commit as the declaration --\n'
            '    a declared survivor with the plain exit rule is a battery that can never pass,\n'
            '    which is the exact defect this gate was written for.')


def _strings_in(node):
    """Every string literal reachable from a node, folding `'a' 'b'` and `'a' + 'b'`."""
    out = []
    for child in ast.walk(node):
        if isinstance(child, ast.Constant) and isinstance(child.value, str):
            out.append(child.value)
    return out


def declares_expected_survivor(src):
    """True when the MUTANTS list itself contains the marker.

    Raises on a battery whose MUTANTS list cannot be read, rather than answering False: a
    battery this cannot parse is one the gate is not checking, and reporting `all mutants must
    die` about it would be the gate lying in its own output.
    """
    tree = ast.parse(src)
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 \
                and isinstance(node.targets[0], ast.Name) \
                and node.targets[0].id == 'MUTANTS':
            return any(MARKER in s for s in _strings_in(node.value))
    raise ValueError('no module-level MUTANTS list')


def main():
    names = sorted(f for f in os.listdir(MUTATION)
                   if f.startswith('mutate_') and f.endswith('.py'))
    if not names:
        print('REFUSING: no mutate_*.py found under mutation/. This check would pass vacuously.')
        return 2

    problems = []
    declaring = []

    for name in names:
        path = os.path.join(MUTATION, name)
        src = open(path, encoding='utf-8').read()

        # The marker only counts inside a MUTANT DESCRIPTION -- a module docstring mentioning it,
        # or a comment explaining the convention, is not a declaration.
        #
        # ⚠️ This used to read `src.split('MUTANTS = [', 1)[-1]`, which is not the list: it is
        # EVERYTHING AFTER the list opens, including every line below it. P2-107's battery ends
        # with a comment telling the next reader to "declare the mutant EXPECTED SURVIVOR: with
        # the reason", and that sentence made this gate report a declaration the battery does not
        # make -- then demand the one exit form that would have been wrong. Detection by
        # substring over a region nobody bounded, which is the exact habit this repo has been
        # bitten by three times (a plan entry read as CLOSED because its title contained
        # `positionClosed`, a battery counted as wired because a comment named it). Read the
        # list.
        try:
            declares = declares_expected_survivor(src)
        except (SyntaxError, ValueError) as exc:
            problems.append('%s could not be read: %s. A battery this gate cannot parse is one it '
                            'is not checking.' % (name, exc))
            print('  %-34s UNREADABLE' % name)
            continue

        uses_helper = HELPER_CALL in src
        uses_plain = PLAIN_EXIT in src

        if declares:
            declaring.append(name)

        if declares and not uses_helper:
            problems.append(
                '%s declares an %s mutant but exits with the plain rule, so it can NEVER pass.\n'
                '    %s' % (name, MARKER, helper_remedy()))
        elif not declares and uses_helper:
            problems.append(
                '%s routes through _battery.finish but declares no %s mutant.\n'
                '    Use the plain `%s`: reaching for the helper without a declaration removes the\n'
                '    prompt to justify the next exemption someone adds.'
                % (name, MARKER, PLAIN_EXIT))
        elif not declares and not uses_plain and not uses_helper:
            problems.append(
                '%s has neither exit form. A battery whose exit code nothing checks is a script.'
                % name)

        if uses_helper and uses_plain:
            problems.append(
                '%s has BOTH exit forms. Whichever runs first wins and the other is dead text.'
                % name)

        # ⚠️ Calling a helper this repo does not have is an ImportError at the moment the
        # battery runs -- which, on a mutation battery, means it dies BETWEEN applying a mutant
        # and restoring it and leaves a live mutant in the tree. Cheaper to refuse here.
        if uses_helper and not os.path.exists(HELPER_MODULE):
            problems.append(
                '%s calls %s but this repo has no mutation/_battery.py.\n'
                '    That is an ImportError at run time, and a battery that dies before its\n'
                '    restore line leaves a MUTANT in the working tree.' % (name, HELPER_CALL))

        print('  %-34s %s' % (name, 'declares an expected survivor' if declares else 'all mutants must die'))

    print('')
    if problems:
        print('FAIL: %d battery/batteries have an exit rule that contradicts their mutants:\n'
              % len(problems))
        for p in problems:
            print('  * ' + p + '\n')
        return 1

    # ⚠️ Say what is TRUE OF THIS REPO. The inherited wording described every battery as routing
    # through `_battery.finish` "which fails on an unexpected survivor AND on a declared one that
    # has since been KILLED" -- a helper that is not present here, so the summary would have been
    # advertising a guarantee nothing in this repo provides. A ported gate reports on the repo it
    # was pasted into, not the one it was written for.
    if declaring:
        print('OK: %d batteries checked; %d declare an expected survivor (%s) and route'
              % (len(names), len(declaring), ', '.join(declaring)))
        print('    through _battery.finish, which fails on an unexpected survivor AND on a')
        print('    declared one that has since been KILLED. The rest exit on any survivor.')
    else:
        print('OK: %d batteries checked; NONE declares an expected survivor, and every one of'
              % len(names))
        print('    them exits on any survivor at all -- the strict rule, which is correct while')
        print('    no exemption exists. The other half of this gate is live: the first battery')
        print('    to declare one must route through a helper, and this repo does not have it')
        print('    yet (no mutation/_battery.py), which this check will say at that moment.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
