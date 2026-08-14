"""Check every mutation battery's anchors WITHOUT running a single mutant.

⚠️ WHY THIS EXISTS. A battery whose find-string no longer matches prints `[SKIP]`
and scores that mutant a SURVIVOR -- but only when the battery is RUN, and a
battery is only run when the suite is green. So an anchor broken by an unrelated
commit stays invisible for exactly as long as it takes to notice.

⚠️ PORTED FROM nt8-riskguard (P3-111, session 42) AFTER IT WAS NEEDED HERE. This
file existed in the core repo for sessions while THIS repo, which also has five
batteries, had nothing. P3-111 moved the parse/clamp arithmetic out of
`BridgeOrderQuery.cs` into `BridgeQueryValue.cs`; six of `mutate_p2109.py`'s
anchors stopped matching, and the only thing that noticed was running the battery
by hand -- 6 [SKIP]s scoring as SURVIVORS, 6/12, minutes of `dotnet build` spent
proving nothing. In nt8-riskguard the same edit would have failed in the commit.

That is the THIRD per-repo gate found missing on this side, after
`check_ci_runs_every_battery.py` (which ran NEITHER of this repo's batteries until
session 38) and `check_expected_survivors.py`. **A gate is per-repo. Writing one
in the repo where you found the defect leaves the other side unguarded, and the
other side is where you will be working tomorrow.** When adding a check to either
NT8 repo, ask what it would cost to put it in both -- here it was `cp`.

Each battery run is minutes of `dotnet build` per mutant. This is a substring
count, so it is instant -- which means it can be run after EVERY commit that
touches `addons/`, including while the suite is red.

It imports nothing: importing a battery module executes it, because they run at
import time by design. It reads the MUTANTS list out of the source instead.

Exits non-zero if any anchor does not match exactly once.
"""
import ast
import glob
import io
import os
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))


def literal(node):
    """Fold a `'a' 'b'` / `'a' + 'b'` string expression down to its value."""
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return node.value
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
        left, right = literal(node.left), literal(node.right)
        if left is not None and right is not None:
            return left + right
    return None


def paths_in(tree, module_path):
    """The `NAME = os.path.join(REPO, 'addons', 'X.cs')` constants at module level."""
    out = {}
    for node in tree.body:
        if not isinstance(node, ast.Assign) or len(node.targets) != 1:
            continue
        target = node.targets[0]
        if not isinstance(target, ast.Name):
            continue
        call = node.value
        if not (isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute)
                and call.func.attr == 'join'):
            continue
        parts = [literal(a) for a in call.args[1:]]
        if any(p is None for p in parts):
            continue
        out[target.id] = os.path.join(REPO, *parts)
    return out


def mutants_in(tree):
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 \
                and isinstance(node.targets[0], ast.Name) \
                and node.targets[0].id == 'MUTANTS':
            return node.value
    return None


def main():
    bad = 0
    checked = 0
    for battery in sorted(glob.glob(os.path.join(REPO, 'mutation', 'mutate_*.py'))):
        name = os.path.basename(battery)
        tree = ast.parse(io.open(battery, encoding='utf-8').read())
        consts = paths_in(tree, battery)
        mutants = mutants_in(tree)
        if mutants is None or not isinstance(mutants, (ast.List, ast.Tuple)):
            print('%-24s SKIPPED -- no literal MUTANTS list to read' % name)
            continue

        # A battery is either 3-tuples (one implicit file) or 4-tuples (path first).
        default_path = None
        for key in ('TARGET', 'SOURCE', 'ENGINE', 'ADDON'):
            if key in consts:
                default_path = consts[key]
                break
        if default_path is None and len(consts) == 1:
            default_path = list(consts.values())[0]

        sources = {}
        problems = []
        for entry in mutants.elts:
            if not isinstance(entry, (ast.Tuple, ast.List)):
                continue
            items = list(entry.elts)
            # 4-tuples name their target file with a module constant. Both orders occur --
            # (PATH, label, old, new) and (label, PATH, old, new) -- so find the Name rather
            # than assuming a position.
            if len(items) == 4:
                named = [i for i, it in enumerate(items) if isinstance(it, ast.Name)]
                if len(named) == 1 and named[0] in (0, 1):
                    path = consts.get(items[named[0]].id)
                    label = literal(items[1 - named[0]])
                    old = literal(items[2])
                else:
                    path = label = old = None
            elif len(items) == 3:
                path = default_path
                old = literal(items[1])
                label = literal(items[0])
            else:
                path = label = old = None

            # ⚠️ An unrecognised entry shape used to `continue`, so a battery whose tuples this
            # parser could not read printed `ok` having checked NOTHING. P2-107's battery landed
            # with its file constant second, all 18 anchors were skipped in silence, and the
            # gate reported clean -- which is *a gate nobody reads is a comment* wearing the
            # gate's own output. Not being able to read an entry is a FAILURE, not a skip: this
            # check's whole product is the count of anchors it verified.
            if path is None or old is None:
                checked += 1
                problems.append('  ? entry %d: could not read it statically (%d element(s)). This '
                                'check cannot skip what it cannot parse.' % (len(problems) + 1, len(items)))
                continue
            if path not in sources:
                sources[path] = io.open(path, encoding='utf-8').read()
            hits = sources[path].count(old)
            checked += 1
            if hits != 1:
                short = (label or old).strip().splitlines()[0][:70]
                problems.append('  x %-16s matched %d time(s): %s'
                                % (os.path.basename(path), hits, short))

        if problems:
            bad += len(problems)
            print('%-24s %d BROKEN ANCHOR(S)' % (name, len(problems)))
            for p in problems:
                print(p)
        else:
            print('%-24s ok' % name)

    print('\n%d anchor(s) checked, %d broken' % (checked, bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
