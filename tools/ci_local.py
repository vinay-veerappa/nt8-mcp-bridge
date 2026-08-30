"""Run the whole CI suite locally, in parallel, on this box -- the bridge port.

WHY. GitHub-hosted `windows-latest` gives 20 ACCOUNT-WIDE job slots shared with the sibling
(nt8-riskguard) repo, so the two repos contend for the same budget and the round-trip is slow. This
box runs the same gates, both harnesses and every mutation battery locally, far faster, with no
network surface. The operator mandates local as the authoritative gate; push after local-green.

⚠️ THIS DOES NOT REPLACE GITHUB ACTIONS, AND DELIBERATELY SO. A local pass proves the tree you have
is good; only CI proves the commit you PUSHED was checked ([[a-worktree-is-not-a-fresh-checkout]] --
a battery scored 9/9 local and 2/9 in CI on line endings). Both, not either -- so still push.

⚠️ SELF-HOSTED GITHUB RUNNERS WERE REJECTED ON PURPOSE (see nt8-riskguard/tools/ci_local.py): both
repos are PUBLIC and the workflow triggers on `pull_request`, so a self-hosted runner would let any
fork's PR execute arbitrary code on the machine running NinjaTrader against a funded account.

HOW THE ISOLATION WORKS, and it is the whole design:

  ⚠️ A MUTATION BATTERY OWNS ITS SOURCE TREE FOR THE LENGTH OF ITS RUN. It writes a mutant into a
  real addon file, runs the harness, and restores. Two batteries in one tree interleave and corrupt
  each other -- one takes the other's mutant as its snapshot and writes it back, leaving a live
  mutant behind a green suite. [[mutation-battery-killed-leaves-a-mutant]]

  So each worker gets its OWN `git worktree`. A worker that dies mid-mutant leaves the damage inside
  a directory this script then deletes. The harness (`tests/BridgeTests.csproj`) does NOT compile
  the vendored core, so a worktree needs no submodule checkout -- measured 2026-08-21: it builds
  clean with `vendor/nt8-riskguard` absent.

  ⚠️ It also lets the gates run in the main tree WHILE batteries run: a gate reading source with a
  mutant applied reports a FALSE RED, and a false red is the one you act on. The main tree is never
  mutated, so gates there are safe. [[a-killed-mutation-battery-leaves-a-mutant]]

WHAT IT TESTS. `HEAD`, not your working tree, because that is what you are about to push. A dirty
tree is named loudly; `--include-uncommitted` copies modified tracked files into each worktree
instead, for the iterate-before-committing case.

TWO HARNESSES. The bridge has a C# harness (`tests/BridgeTests.csproj`) AND the Node MCP wrapper
(`mcp/`, `node --test`) -- they are two halves of one contract ([[a-gate-is-per-repo]]). Both run in
the suite phase, in the main tree, because the batteries only mutate C# addon sources.
"""
import argparse
import ast
import concurrent.futures
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKTREE_ROOT = os.path.join(REPO, '.ci-local')
PRINT_LOCK = threading.Lock()


def say(*parts):
    with PRINT_LOCK:
        print(*parts, flush=True)


def run(cmd, cwd=REPO, timeout=1800, env=None):
    full_env = None
    if env:
        full_env = dict(os.environ)
        full_env.update(env)
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=timeout, env=full_env)
    return p.returncode, (p.stdout or '') + (p.stderr or '')


def mutant_count(path):
    """Number of entries in the battery's MUTANTS list, WITHOUT executing it (importing a battery
    RUNS it, which would start mutating this tree). Used only to order the queue longest-first."""
    try:
        tree = ast.parse(open(path, encoding='utf-8', errors='replace').read())
    except Exception:
        return 1
    for node in ast.walk(tree):
        if isinstance(node, ast.Assign):
            for t in node.targets:
                if isinstance(t, ast.Name) and t.id == 'MUTANTS':
                    if isinstance(node.value, (ast.List, ast.Tuple)):
                        return max(1, len(node.value.elts))
    return 1


def dirty_files():
    _, out = run(['git', 'status', '--porcelain'])
    return [l[3:].strip() for l in out.splitlines() if l.strip()]


def make_worktrees(n, include_uncommitted):
    if os.path.isdir(WORKTREE_ROOT):
        run(['git', 'worktree', 'prune'])
        shutil.rmtree(WORKTREE_ROOT, ignore_errors=True)
    os.makedirs(WORKTREE_ROOT, exist_ok=True)
    paths = []
    modified = [f for f in dirty_files() if os.path.isfile(os.path.join(REPO, f))]
    for i in range(n):
        wt = os.path.join(WORKTREE_ROOT, 'w%d' % i)
        rc, out = run(['git', 'worktree', 'add', '--detach', wt, 'HEAD'])
        if rc != 0:
            say('  worktree %d FAILED: %s' % (i, out.strip()[:300]))
            return paths
        # ⚠️ The submodule must be populated even though the BUILD does not need it. Two harness
        # tests read `vendor/nt8-riskguard` at RUNTIME -- "the vendored core is present" and "the
        # bridge carries no copy of a vendored core source" -- and `git worktree add` does NOT
        # check out submodules, so without this the baseline is 678/2 in every worktree while the
        # main tree is 680/0, and every battery refuses on a red baseline it did not cause.
        # Measured 2026-08-21; ~2.7s per worktree from the already-fetched objects (no network).
        rc2, out2 = run(['git', 'submodule', 'update', '--init', '--recursive',
                         'vendor/nt8-riskguard'], cwd=wt)
        if rc2 != 0:
            say('  worktree %d submodule FAILED: %s' % (i, out2.strip()[:300]))
            return paths
        if include_uncommitted:
            for f in modified:
                src = os.path.join(REPO, f)
                dst = os.path.join(wt, f)
                if os.path.isfile(src):
                    os.makedirs(os.path.dirname(dst), exist_ok=True)
                    shutil.copy2(src, dst)
        paths.append(wt)
    return paths


def drop_worktrees():
    for name in sorted(os.listdir(WORKTREE_ROOT)) if os.path.isdir(WORKTREE_ROOT) else []:
        run(['git', 'worktree', 'remove', '--force', os.path.join(WORKTREE_ROOT, name)])
    run(['git', 'worktree', 'prune'])
    shutil.rmtree(WORKTREE_ROOT, ignore_errors=True)


def phase_gates():
    """Every tools/check_*.py gate. (Unlike the core, check_anchors.py lives in tools/ here, so a
    single glob covers it.) Fast, and safe beside the batteries because they mutate worktrees and
    these read the main tree, which is never mutated."""
    scripts = sorted(
        os.path.join('tools', f) for f in os.listdir(os.path.join(REPO, 'tools'))
        if f.startswith('check_') and f.endswith('.py'))
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as ex:
        futs = {ex.submit(run, [sys.executable, s]): s for s in scripts}
        for fut in concurrent.futures.as_completed(futs):
            s = futs[fut]
            rc, out = fut.result()
            results.append((s, rc, out))
            say('  [%s] %s' % ('ok  ' if rc == 0 else 'FAIL', s))
    return results


def phase_suite():
    """Both harnesses: the C# BridgeTests and the Node MCP wrapper."""
    # ⚠️ Build first and check it separately: `dotnet run --no-build` after a FAILED build runs the
    # PREVIOUS assembly and prints a green RESULTS line (tests/README.md and BridgeTests.csproj:42
    # both record this). Same reason CI's own run builds implicitly and this splits it.
    rc, out = run(['dotnet', 'build', 'tests/BridgeTests.csproj', '--nologo', '-v', 'q'])
    if 'error CS' in out or rc != 0:
        say('  [FAIL] build (BridgeTests)')
        return False, out
    rc, out = run(['dotnet', 'run', '--project', 'tests/BridgeTests.csproj',
                   '--no-build', '--nologo', '-v', 'q'])
    m = re.search(r'Passed = (\d+), Failed = (\d+)', out)
    if not m:
        say('  [FAIL] C# harness produced NO RESULT LINE')
        return False, out
    csharp_ok = int(m.group(2)) == 0
    say('  [%s] C# harness %s' % ('ok  ' if csharp_ok else 'FAIL', m.group(0)))

    # The Node wrapper -- the other half of the contract. `node --test` exits non-zero on any
    # failing test. ⚠️ It ALSO exits 0 when it discovers NO test files (e.g. run from the wrong
    # directory), which reads exactly like a pass -- so the count is parsed and a zero-test run is
    # a FAILURE, not a pass ([[a-green-that-can-never-be-red]]). Node 24's reporter prints
    # `ℹ tests N` / `ℹ pass N` / `ℹ fail N` (no `#`).
    # ⚠️ TAP reporter, not the default spec: node 24's spec reporter colourises even when piped
    # (wrapping the summary COUNTS in ANSI, so `\s+(\d+)` reads the escape, not the number -- every
    # run parsed as zero-tests, measured 2026-08-21). TAP is plain ASCII designed for machines:
    # `# tests 73` / `# pass 73` / `# fail 0`. Exit code is unchanged by the reporter.
    rc2, out2 = run(['node', '--test', '--test-reporter=tap'],
                    cwd=os.path.join(REPO, 'mcp'), env={'NO_COLOR': '1'})
    mt = re.search(r'(?m)^#\s*tests\s+(\d+)', out2)
    mf = re.search(r'(?m)^#\s*fail\s+(\d+)', out2)
    ntests = int(mt.group(1)) if mt else 0
    nfail = int(mf.group(1)) if mf else None
    node_ok = rc2 == 0 and ntests > 0 and nfail == 0
    if ntests == 0:
        summary = 'discovered NO tests -- run from mcp/? (a zero-test pass is a false pass)'
    else:
        summary = 'tests %d / fail %s' % (ntests, nfail if nfail is not None else '?')
    say('  [%s] Node wrapper (node --test) %s' % ('ok  ' if node_ok else 'FAIL', summary))

    # The client caches (VS Code allowlist, Antigravity schema files) must agree with
    # lib/tools.js. --check writes nothing and exits 1 on drift; the generator itself is
    # NOT run here, because a CI pass that silently rewrites files outside the repo is a
    # pass that hides the drift it exists to report. Run the generator by hand when this
    # gate is red: `node tools/sync_client_caches.mjs`.
    rc3, out3 = run(['node', os.path.join('tools', 'sync_client_caches.mjs'), '--check'])
    caches_ok = rc3 == 0
    say('  [%s] Client caches (tools/sync_client_caches.mjs --check) %s'
        % ('ok  ' if caches_ok else 'FAIL', 'in sync' if caches_ok else 'DRIFT -- run: node tools/sync_client_caches.mjs'))

    return (csharp_ok and node_ok and caches_ok), out + '\n=== node --test ===\n' + out2 + '\n=== client caches ===\n' + out3


def worker(wt, work, results, log_dir):
    while True:
        try:
            battery = work.get_nowait()
        except queue.Empty:
            return
        started = time.time()
        rc, out = run([sys.executable, os.path.join('mutation', battery)], cwd=wt)
        # A red baseline under load is a flake, not a finding; retry once. Rare here (the bridge
        # harness is ~600 tests, far lighter than the core's ~3400), kept as cheap insurance.
        if rc != 0 and 'baseline' in out.lower() and 'red' in out.lower():
            say('  [retry] %-28s red baseline, likely a load flake' % battery)
            rc, out = run([sys.executable, os.path.join('mutation', battery)], cwd=wt)
        secs = time.time() - started
        with open(os.path.join(log_dir, battery + '.log'), 'w',
                  encoding='utf-8', errors='replace') as f:
            f.write(out)
        results.append((battery, rc, secs, out))
        say('  [%s] %-28s %5.0fs' % ('ok  ' if rc == 0 else 'FAIL', battery, secs))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--jobs', type=int, default=0,
                    help='parallel worktrees; default is cores//2 capped at 12')
    ap.add_argument('--include-uncommitted', action='store_true',
                    help='copy modified tracked files into each worktree instead of testing HEAD')
    ap.add_argument('--keep', action='store_true', help='leave the worktrees for inspection')
    ap.add_argument('--only', choices=['gates', 'suite', 'batteries'], action='append',
                    help='run only these phases (repeatable)')
    args = ap.parse_args()

    phases = set(args.only or ['gates', 'suite', 'batteries'])
    jobs = args.jobs or min(12, max(1, (os.cpu_count() or 4) // 2))
    t0 = time.time()

    dirty = dirty_files()
    print('=' * 72)
    print('LOCAL CI (bridge)  --  %d worker(s), testing %s'
          % (jobs, 'YOUR WORKING TREE' if args.include_uncommitted else 'HEAD'))
    if dirty:
        print('⚠️  %d uncommitted change(s); %s'
              % (len(dirty),
                 'they ARE included' if args.include_uncommitted
                 else 'they are NOT tested (pass --include-uncommitted)'))
        for f in dirty[:10]:
            print('      %s' % f)
    print('=' * 72)

    failures = []

    if 'gates' in phases:
        print('\n-- gates --')
        for s, rc, out in phase_gates():
            if rc != 0:
                failures.append(('gate ' + s, out))

    if 'suite' in phases:
        print('\n-- build + suite (C# harness + Node wrapper) --')
        ok, out = phase_suite()
        if not ok:
            failures.append(('suite', out))

    if 'batteries' in phases:
        batteries = sorted(
            f for f in os.listdir(os.path.join(REPO, 'mutation'))
            if f.startswith('mutate_') and f.endswith('.py'))
        batteries.sort(key=lambda b: -mutant_count(os.path.join(REPO, 'mutation', b)))
        print('\n-- %d batteries across %d worktree(s) --' % (len(batteries), jobs))

        log_dir = os.path.join(REPO, '.ci-local-logs')
        shutil.rmtree(log_dir, ignore_errors=True)
        os.makedirs(log_dir, exist_ok=True)

        print('   creating worktrees...')
        wts = make_worktrees(jobs, args.include_uncommitted)
        if not wts:
            print('   could not create any worktree; aborting the battery phase')
            failures.append(('worktrees', 'none created'))
        else:
            work = queue.Queue()
            for b in batteries:
                work.put(b)
            results = []
            threads = [threading.Thread(target=worker, args=(wt, work, results, log_dir))
                       for wt in wts]
            for t in threads:
                t.start()
            for t in threads:
                t.join()
            for b, rc, secs, out in results:
                if rc != 0:
                    failures.append(('battery ' + b, out))
            if not args.keep:
                drop_worktrees()
            else:
                print('   worktrees kept under %s' % WORKTREE_ROOT)
            print('   logs under %s' % log_dir)

    elapsed = time.time() - t0
    print('\n' + '=' * 72)
    if failures:
        print('FAIL in %.1f min -- %d failing item(s):' % (elapsed / 60.0, len(failures)))
        for name, out in failures:
            print('\n  * %s' % name)
            tail = [l for l in out.strip().splitlines() if l.strip()][-6:]
            for l in tail:
                print('      %s' % l[:160])
        return 1
    print('OK in %.1f min -- gates, both harnesses and every battery green.' % (elapsed / 60.0))
    print('⚠️  This proves the tree you RAN it on. Only CI proves the commit you PUSHED.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
