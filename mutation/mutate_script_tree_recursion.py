"""Mutation battery: the script-tree handlers must see the WHOLE tree.

THE DEFECT, measured 2026-09-04. NT8 compiles bin\\Custom\\Strategies and
bin\\Custom\\Indicators RECURSIVELY. Six handlers used
`Directory.GetFiles(dir, "*.cs")`, which defaults to TopDirectoryOnly.
`nt_list_strategies` reported 27 files against 59 on disk; the 32 it omitted live
in six subfolders, and Vinay is where this user's own bots are. That listing was
read as evidence the bots were not deployed, and a deploy was nearly repeated on
a live box because of it. An empty answer and an unsearched folder look identical.

THE SEVERE HALF is creation, not listing. `File.Exists` at the top level only
means creating `Strategies/Foo.cs` while `Strategies/Vinay/Foo.cs` exists writes a
SECOND definition of the same class; NT8 then fails the whole Custom assembly,
which stops EVERY addon loading -- the risk guard included -- and the only symptom
is a deploy that had no effect. The consumer repo hit this exact trap with
indicators, where the same shape would have written 23 duplicate classes.

THE GROUPS:

  1. the two shared helpers losing SearchOption.AllDirectories -- one edit that
     silently reverts all six handlers, which is why they are shared;
  2. an individual handler going back to a bare top-level GetFiles;
  3. ⚠️ the create paths accepting a cross-folder collision instead of refusing.
     `overwrite` cannot authorise this: it means "replace the file I named", and
     a same-named file in another folder is a different file;
  4. the read paths silently taking the FIRST of several matches, which hides the
     collision rather than reporting it -- fail-quiet at exactly the moment the
     tree is already broken.

Killer is the source gate: McpBridgeAddOn.cs is the one bridge source
tests/BridgeTests.csproj cannot compile, so no unit test can reach these handlers.
check_bridge_parses.py runs alongside so a mutant that merely breaks the C# is
scored killed for the right reason.

A crash counts as a kill. Exits non-zero on any survivor, and exits 2 rather than
running against a red baseline.
"""
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ADDON = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

GATES = [
    os.path.join(REPO, 'tools', 'check_script_tree_recursion.py'),
    os.path.join(REPO, 'tools', 'check_bridge_parses.py'),
]

MUTANTS = [
    # ---- group 1: the shared helpers -----------------------------------------
    (ADDON,
     "⚠️ ListScriptsRecursive loses AllDirectories -- ONE edit that silently reverts\n"
     "     both list handlers to the 27-of-59 answer",
     'return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)',
     'return Directory.GetFiles(root, "*.cs")'),

    (ADDON,
     "⚠️ FindScriptFiles loses AllDirectories -- the collision check stops seeing\n"
     "     subfolders, so both create paths go back to writing duplicate classes",
     'return Directory.GetFiles(root, name + ".cs", SearchOption.AllDirectories).ToList();',
     'return Directory.GetFiles(root, name + ".cs").ToList();'),

    # ---- group 2: an individual handler regresses ----------------------------
    (ADDON,
     "ListStrategies goes back to a bare top-level GetFiles",
     '            var list = ListScriptsRecursive(dir);\n'
     '            return new { dir, count = list.Count, recursive = true, strategies = list };',
     '            var list = Directory.GetFiles(dir, "*.cs").Select(f => (object)f).ToList();\n'
     '            return new { dir, count = list.Count, recursive = true, strategies = list };'),

    (ADDON,
     "ListIndicators goes back to a bare top-level GetFiles",
     '            var list = ListScriptsRecursive(dir);\n'
     '            return new { dir, count = list.Count, recursive = true, indicators = list };',
     '            var list = Directory.GetFiles(dir, "*.cs").Select(f => (object)f).ToList();\n'
     '            return new { dir, count = list.Count, recursive = true, indicators = list };'),

    # ---- group 3: the create paths stop refusing -----------------------------
    (ADDON,
     "⚠️ CreateStrategy stops refusing a cross-folder collision: it writes a second\n"
     "     definition of the class, NT8 fails the whole Custom assembly, and every\n"
     "     addon stops loading with a deploy that merely looks ineffective",
     '            if (elsewhere.Count > 0)\n'
     '                return new { error = $"refused: {name}.cs already exists elsewhere under Strategies",',
     '            if (false)\n'
     '                return new { error = $"refused: {name}.cs already exists elsewhere under Strategies",'),

    (ADDON,
     "⚠️ CreateIndicator stops refusing -- the likelier half, with eleven vendor\n"
     "     subfolders under Indicators",
     '            if (elsewhere.Count > 0)\n'
     '                return new { error = $"refused: {name}.cs already exists elsewhere under Indicators",',
     '            if (false)\n'
     '                return new { error = $"refused: {name}.cs already exists elsewhere under Indicators",'),

    # ---- group 4: the read paths hide a duplicate ----------------------------
    (ADDON,
     "GetStrategySource silently takes the FIRST of several matches instead of\n"
     "     reporting AMBIGUOUS -- fail-quiet exactly when the tree is already broken",
     '                if (hits.Count > 1)\n'
     '                    return new { error = $"AMBIGUOUS: {hits.Count} files named {name}.cs exist under Strategies",',
     '                if (false)\n'
     '                    return new { error = $"NOTE: {hits.Count} files named {name}.cs exist under Strategies",'),

    (ADDON,
     "GetIndicatorSource silently takes the FIRST of several matches",
     '                if (hits.Count > 1)\n'
     '                    return new { error = $"AMBIGUOUS: {hits.Count} files named {name}.cs exist under Indicators",',
     '                if (false)\n'
     '                    return new { error = $"NOTE: {hits.Count} files named {name}.cs exist under Indicators",'),
]

ORIGINALS = {p: open(p, encoding='utf-8').read() for p in {m[0] for m in MUTANTS}}


def restore():
    for path, text in ORIGINALS.items():
        # newline='' so the exact bytes go back. Two invisible CRs once made six
        # bins cry "A MUTANT IS LIVE" while every mutant had died.
        open(path, 'w', encoding='utf-8', newline='').write(text)


def run():
    fails = []
    for gate in GATES:
        try:
            p = subprocess.run(
                [sys.executable, gate], cwd=REPO, capture_output=True, text=True,
                # DECODE half of the encoding pin: the Windows default is cp1252 and
                # one non-ASCII character in gate output makes capture_output raise
                # UnicodeDecodeError on a reader thread, killing the battery before
                # its first mutant.
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
sys.exit(1 if survivors else 0)
