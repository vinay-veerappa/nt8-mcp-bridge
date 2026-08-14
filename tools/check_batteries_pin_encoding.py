"""Every mutation battery must pin an explicit encoding on its subprocess captures.

⚠️ WHY THIS EXISTS. On Windows, `subprocess.run(..., capture_output=True, text=True)` decodes the
child's output with the LOCALE codec, which is cp1252 here and on GitHub's windows runners. The
bridge test suite prints test names, and one non-ASCII character in one of them is enough:
`fh.read()` raises `UnicodeDecodeError` on a reader THREAD, the exception is printed but not
propagated, `res.stdout` comes back **None**, and the battery dies with a `TypeError` from `re`
before it has run a single mutant.

That is not a test failure and does not read like one. It is `an alarm that is always on is off`
in its other form -- a check that CANNOT RUN reports nothing, and the batteries are this repo's
whole evidence standard.

⚠️ AND THE REASON IT IS A GATE RATHER THAN A FIXED BUG: all four batteries had it. A bulk patch
fixed three and printed `SKIP mutate_p190.py (matched 0)` for the fourth, because that one's
`run()` builds and runs in two steps and did not match the patch's anchor. **The skip was printed,
read, and not acted on** -- and CI went red on the very next push, on a battery that had nothing
to do with the change. A human reading a tool's honest report is not a gate; this is.

It parses with `ast` rather than grepping for a string, and it prints the number of calls actually
inspected -- see the nt8-riskguard handover on the four gates in these repos that were caught
proving nothing by searching a region nobody had bounded.
"""
import ast
import os
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
MUTATION = os.path.join(REPO, 'mutation')


def captures_output(call):
    """True if this subprocess.run call captures the child's output as TEXT.

    A capture that stays as bytes cannot raise a decode error, so it is not this gate's business.
    """
    kw = {k.arg: k.value for k in call.keywords if k.arg}
    def truthy(name):
        node = kw.get(name)
        return isinstance(node, ast.Constant) and node.value is True
    return truthy('capture_output') and (truthy('text') or truthy('universal_newlines'))


def is_subprocess_run(call):
    f = call.func
    return isinstance(f, ast.Attribute) and f.attr == 'run' \
        and isinstance(f.value, ast.Name) and f.value.id == 'subprocess'


def main():
    if not os.path.isdir(MUTATION):
        print('FAIL: no mutation/ directory -- refusing to pass vacuously')
        return 1

    batteries = sorted(f for f in os.listdir(MUTATION)
                       if f.startswith('mutate_') and f.endswith('.py'))
    if not batteries:
        print('FAIL: no mutate_*.py found -- refusing to pass vacuously')
        return 1

    inspected = 0
    problems = []
    for name in batteries:
        path = os.path.join(MUTATION, name)
        src = open(path, encoding='utf-8').read()
        try:
            tree = ast.parse(src)
        except SyntaxError as exc:
            # Refuse what cannot be parsed rather than skipping it: a battery this gate cannot
            # read is exactly the battery it would otherwise silently exempt.
            problems.append('%s: could not parse (%s)' % (name, exc))
            continue

        found_any = False
        for node in ast.walk(tree):
            if not isinstance(node, ast.Call) or not is_subprocess_run(node):
                continue
            if not captures_output(node):
                continue
            found_any = True
            inspected += 1
            kw = {k.arg for k in node.keywords if k.arg}
            if 'encoding' not in kw:
                problems.append(
                    '%s:%d: subprocess.run captures text output with no explicit encoding= '
                    '(decodes as cp1252 on Windows; one non-ASCII test name makes stdout None)'
                    % (name, node.lineno))

        if not found_any:
            problems.append(
                '%s: no text-capturing subprocess.run found at all. Either it does not run the '
                'suite, or it does so in a shape this gate cannot see -- both need a human.'
                % name)

    print('batteries: %d   text-capturing subprocess.run calls inspected: %d'
          % (len(batteries), inspected))
    for b in batteries:
        print('  ' + b)

    if problems:
        print('\nFAIL:')
        for p in problems:
            print('  * ' + p)
        return 1

    print('\nOK: every battery pins an explicit encoding on every text capture.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
