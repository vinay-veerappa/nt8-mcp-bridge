#!/usr/bin/env python3
"""
deploy.py -- deploy the bridge AND the vendored core into NT8, or deploy nothing.

    addons/*.cs                      ->  .../NinjaTrader 8/bin/Custom/AddOns/
    vendor/nt8-riskguard/addons/*.cs  ->  same folder

Usage:
    python tools/deploy.py --verify   # drift status only, exit 1 on drift
    python tools/deploy.py --dry-run  # what would be copied
    python tools/deploy.py            # deploy

Why this refuses to deploy the bridge alone
-------------------------------------------
NinjaTrader has no package manager. Every AddOn compiles into ONE assembly
(NinjaTrader.Custom.dll) and calls the others' types directly, so McpBridgeAddOn.cs
does not merely prefer the core -- it will not compile without it. And in NT8 a
compile error is not local: the whole Custom assembly fails, so EVERY addon stops
loading, the risk guard included. A half-deploy therefore does not degrade the
bridge, it disarms the thing protecting the account.

So a missing or empty vendor/nt8-riskguard/ is a hard error here, never a warning.

Unlike the core's sync tool, this one owns the UNION of both trees, so it is the
authoritative deploy-parity check (plan section 6): anything in AddOns/ that neither
tree provides is a genuine orphan and is reported as such.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
BRIDGE_SRC = REPO_ROOT / "addons"
# The browser UI. Static files, deployed OUTSIDE bin/Custom on purpose: NT8 compiles
# that folder, so an .html sitting in it is at best ignored and at worst confusing.
UI_SRC = REPO_ROOT / "ui"
VENDOR_SRC = REPO_ROOT / "vendor" / "nt8-riskguard" / "addons"
# P1-149. The core also ships STRATEGIES (RiskManagerBase, RiskGatekeeper) -- .cs that name
# NinjaTrader.* Strategy types, so no test build compiles them, but NT8 does. RiskGatekeeper.CanTradeSize
# -> ContractCapGate (an addon) is the contract-cap enforcement, and RiskManagerBase's entry path calls
# it, so the strategies and the addon MUST deploy together or the one Custom assembly fails to compile
# and EVERY addon -- the guard included -- stops loading. Only the vendored core has strategies; the
# bridge has none.
STRATEGIES_SRC = REPO_ROOT / "vendor" / "nt8-riskguard" / "strategies"

NT8_HOME = Path(os.environ.get("USERPROFILE", "")) / "Documents" / "NinjaTrader 8" / "bin" / "Custom"
# Globals.UserDataDir as the addons see it -- the NinjaTrader 8 folder itself, not bin/Custom.
UI_DST = Path(os.environ.get("USERPROFILE", "")) / "Documents" / "NinjaTrader 8" / "RiskGuard" / "ui"
ADDONS_DST = NT8_HOME / "AddOns"
# ⚠️ Preserve the source's SUBFOLDER (strategies/Vinay/*.cs -> Strategies/Vinay/*.cs). NT8 compiles
# Strategies/ RECURSIVELY, so a copy under a DIFFERENT subfolder than the hand-deployed one would be a
# SECOND class definition beside it, not a replacement -- the exact trap the indicators sync hit.
STRATEGIES_DST = NT8_HOME / "Strategies"

# Nothing is skipped. RiskManagerAddOn.cs is excluded from the core's *test* build
# (RiskGuardTests.csproj) because compiling it alongside RiskGuardAddOn.cs duplicates
# types there -- but NT8 has compiled and run both together for months, so excluding
# it from DEPLOYMENT would silently remove a live addon. A test-build exclusion is not
# a deployment exclusion, and conflating the two is how a deploy tool quietly changes
# what is running.
SKIP = set()

# ─────────────────────────────────────────────────────────────────────────────
# BOT STRATEGY DENYLIST (2026-09-04 ownership cleanup).
# Trading bots live ONLY in tvDownloadOHLC/scripts/ninjatrader/strategies/ and
# deploy via that repo's sync_nt8_strategies.py. The vendored core used to ship
# 15 bot .cs files that were 5+ days stale copies; deploying them silently
# reverted whatever bot build was live (measured: vendor pin 2026-08-29 vs the
# deployed 2026-09-03 ICTFVGCISDBot). The core now ships framework only
# (RiskManagerBase/RiskGatekeeper/IntradayStrategyBase) — this denylist makes
# that an INVARIANT, not a convention: if a bot name ever reappears in the
# vendor tree, deploy refuses rather than clobbering the live bot.
# ─────────────────────────────────────────────────────────────────────────────
BOT_DENYLIST = {
    "bandits8020bot.cs",
    "bbmrreversionbot.cs",
    "emapullbackbot.cs",
    "failedauctionbot.cs",
    "ibbreakoutbot.cs",
    "ibfadebot.cs",
    "ibretestbot.cs",
    "ibstrategybase.cs",
    "ictfvgbos.cs",
    "ictfvgcisdbot.cs",
    "keltnerchannelbot.cs",
    "strat212continuationbot.cs",
    "strat22revstratbot.cs",
    "sttrendbot.cs",
    "vwapreclaimbot.cs",
}


def _assert_no_bots_in_vendor() -> None:
    """Fail closed if the vendored core ships any bot strategy.

    The framework files the core ships are an allowlist by construction (its
    strategies/ tree is reviewed with the guard); a bot appearing there is a
    mistake that would overwrite a NEWER live bot owned by tvDownloadOHLC.
    """
    if not STRATEGIES_SRC.exists():
        return
    offenders = sorted(
        p.name for p in STRATEGIES_SRC.rglob("*.cs")
        if p.name.lower() in BOT_DENYLIST
    )
    if offenders:
        print("[FATAL] the vendored core ships bot strategies this tool must not deploy:")
        for name in offenders:
            print("        {0}".format(name))
        print()
        print("        Bots are owned by tvDownloadOHLC/scripts/ninjatrader/strategies/")
        print("        and deploy via that repo's sync_nt8_strategies.py. Remove the bot")
        print("        .cs from nt8-riskguard/strategies/ and re-vendor the core.")
        raise SystemExit(2)


def file_hash(path: Path) -> str:
    """MD5 of content, normalised to LF with any BOM stripped.

    NT8's own editor and other tools write CRLF while the repo keeps LF. A raw byte
    hash then reports every file as drifted -- which happened on 2026-08-07, showed
    8216 changed lines on a 4108-line file, got written into the runbook as "the
    deployed sources have diverged", and cost real time to disprove. A drift check
    that cries wolf teaches you to ignore it, which is worse than no check.
    """
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.md5(data).hexdigest()


def collect_sources() -> list:
    """Both trees, as (label, Path). Fails hard if the vendored core is absent."""
    if not VENDOR_SRC.exists():
        print("[FATAL] vendor/nt8-riskguard/addons does not exist.")
        print("        Run: git submodule update --init")
        print()
        print("        Refusing to deploy the bridge alone. It cannot compile without the")
        print("        core, and in NT8 one compile error stops EVERY addon loading --")
        print("        including the risk guard. A half-deploy disarms the account.")
        sys.exit(2)

    vendor_files = [p for p in sorted(VENDOR_SRC.glob("*.cs")) if p.name not in SKIP]
    if not vendor_files:
        print("[FATAL] vendor/nt8-riskguard/addons exists but contains no .cs files.")
        print("        An empty submodule checkout is the same hazard as a missing one.")
        sys.exit(2)

    bridge_files = [p for p in sorted(BRIDGE_SRC.glob("*.cs")) if p.name not in SKIP]
    if not bridge_files:
        print("[FATAL] addons/ contains no .cs files -- is this the right repo?")
        sys.exit(2)

    return ([("core", p) for p in vendor_files] + [("bridge", p) for p in bridge_files])


def _git(repo: Path, *args: str):
    """Run git in `repo`; return stripped stdout, or None if it failed or git is absent."""
    try:
        proc = subprocess.run(["git", "-C", str(repo)] + list(args),
                              capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return None
    return proc.stdout.strip() if proc.returncode == 0 else None


def check_vendor_not_stale(deploying: bool) -> None:
    """Refuse to deploy a vendored core that is BEHIND the sibling core checkout.

    This tool deploys the bridge AND its vendored core, so a stale pin does not
    merely fail to bring a fix -- it OVERWRITES a newer core already live in NT8
    and silently reverts it. That is not hypothetical: on 2026-08-12 the pin sat
    at v1.0.1 while v1.0.2 (the P0-63 fix, without which the mirrored stop had
    never trailed) was deployed and running. Nothing would have warned.

    The check is local and needs no network: if the canonical core checkout is
    beside this repo, ask git whether the pinned commit is a strict ancestor of
    its main. Strictly behind is the one unsafe case. Equal, ahead, or unknown
    are all allowed -- unknown only warns, because refusing on "I could not tell"
    would block a legitimate deploy on a machine that has no core checkout, and
    this tool must stay usable there.
    """
    vendor_repo = REPO_ROOT / "vendor" / "nt8-riskguard"
    sibling = REPO_ROOT.parent / "nt8-riskguard"

    pinned = _git(vendor_repo, "rev-parse", "HEAD")
    described = _git(vendor_repo, "describe", "--tags", "--always") or "unknown"

    if pinned is None or not (sibling / ".git").exists():
        print("  [WARN] cannot compare the vendored core against a canonical checkout")
        print("         (pinned: {0}). Deploying it anyway -- but if a NEWER core is".format(described))
        print("         live in NT8, this OVERWRITES it. Check before trusting this run.")
        return

    sibling_main = _git(sibling, "rev-parse", "main")
    if sibling_main is None:
        print("  [WARN] {0} has no `main` to compare against; pinned {1}.".format(sibling, described))
        return

    if pinned == sibling_main:
        print("  vendored core: {0} -- matches {1} main".format(described, sibling.name))
        return

    # Ask the SIBLING, not the vendored clone. The vendor is a submodule checkout
    # that only fetches when someone tells it to, so it does not know commits the
    # canonical repo has made since the last bump -- and `merge-base --is-ancestor`
    # against a revision git cannot resolve EXITS NON-ZERO, which this function used
    # to read as "not behind". That inverted the guard in precisely the case it
    # exists for: a pin left behind while the core moved on. Found 2026-08-13, one
    # commit after the guard shipped, by watching it pass when it should have failed.
    # The sibling authored both commits, so it can always answer.
    behind = subprocess.run(
        ["git", "-C", str(sibling), "merge-base", "--is-ancestor", pinned, sibling_main],
        capture_output=True, text=True)
    if behind.returncode == 1:
        # Definitively NOT an ancestor: ahead, or an unrelated branch. Safe.
        print("  vendored core: {0} -- not behind {1} main".format(described, sibling.name))
        return
    if behind.returncode != 0:
        # Could not evaluate at all (unknown revision, git error). Say so loudly
        # rather than silently allowing it -- an unreadable guard is not a pass.
        print("  [WARN] cannot tell whether the vendored core is behind {0} main".format(sibling.name))
        print("         (pinned {0}; `merge-base --is-ancestor` failed). Treating as".format(described))
        print("         NOT behind, but verify by hand before trusting this run.")
        return

    count = _git(sibling, "rev-list", "--count", "{0}..{1}".format(pinned, sibling_main)) or "?"

    # Behind, but behind in WHAT? This tool deploys the core's addons/ AND strategies/ .cs and nothing
    # else, so a pin trailing only docs, tests, tooling or the agent profile carries no deploy risk at
    # all -- the .cs it would write are byte-identical. Blocking on that would make every documentation
    # commit in the core require a tag-and-bump before the bridge could be deployed, and a guard that
    # fires when nothing is wrong is one people learn to override. Same reasoning as file_hash()
    # normalising line endings: a check that cries wolf is worse than no check.
    #
    # So: narrow the question to the only files that can hurt. strategies/ is included since P1-149 --
    # a pin behind on RiskManagerBase/RiskGatekeeper would deploy a stale entry-path enforcement.
    addon_commits = _git(sibling, "rev-list", "--count",
                         "{0}..{1}".format(pinned, sibling_main), "--", "addons/", "strategies/")
    if addon_commits == "0":
        print("  vendored core: {0} -- {1} commit(s) behind {2} main, but NONE touch".format(
            described, count, sibling.name))
        print("                 addons/ or strategies/, so the deployed .cs are identical. Proceeding.")
        return

    print()
    # Label it for what it IS in this invocation. On --verify/--dry-run nothing is blocked,
    # so printing [FATAL] and exiting 0 was a message overstating its own outcome -- the
    # same class of defect as P1-70's log line, in the tool that reports on it.
    print("[{0}] the vendored core is STALE: {1} is {2} commit(s) behind {3} main,".format(
        "FATAL" if deploying else "WARN", described, count, sibling.name))
    print("        including {0} that touch addons/ or strategies/.".format(addon_commits or "?"))
    print()
    print("        This tool deploys the core as well as the bridge, so deploying now")
    print("        would overwrite whatever core is live in NT8 with an OLDER one and")
    print("        silently revert it. On 2026-08-12 that would have reverted P0-63,")
    print("        the fix without which the mirrored stop never trailed.")
    print()
    print("        Keep the two repos in sync, then retry:")
    print("          cd {0} && git tag -a vX.Y.Z && git push origin main --tags".format(sibling))
    print("          cd {0}/vendor/nt8-riskguard && git fetch --tags && git checkout vX.Y.Z".format(REPO_ROOT))
    print("          cd {0} && git add vendor/nt8-riskguard && git commit".format(REPO_ROOT))
    print()
    print("        --verify and --dry-run are unaffected; only deploying is blocked.")
    if deploying:
        sys.exit(2)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Deploy the bridge and its vendored core into NT8, together or not at all.")
    parser.add_argument("--verify", action="store_true", help="Show drift status without copying.")
    parser.add_argument("--dry-run", action="store_true", help="Show what would be copied.")
    args = parser.parse_args()

    sources = collect_sources()

    print("=" * 70)
    print("NT8 deploy -- bridge + vendored core")
    print("=" * 70)
    print("  bridge:   {0}".format(BRIDGE_SRC))
    print("  core:     {0}".format(VENDOR_SRC))
    print("  NT8 dest: {0}".format(ADDONS_DST))
    print("  mode:     {0}".format("VERIFY" if args.verify else "DRY-RUN" if args.dry_run else "DEPLOY"))
    print()

    # Before touching anything: is the core we are about to deploy actually the
    # current one? Exits 2 on a stale pin when deploying.
    check_vendor_not_stale(deploying=not (args.verify or args.dry_run))
    print()

    # Ownership invariant: the vendored core ships framework only. A bot in the
    # vendor tree would overwrite the live bot owned by tvDownloadOHLC with a
    # stale copy. Checked on every mode, including --verify, so drift is visible
    # before any deploy attempt.
    _assert_no_bots_in_vendor()

    if not NT8_HOME.exists():
        print("[ERROR] NT8 Custom folder not found: {0}".format(NT8_HOME))
        print("        Is NinjaTrader 8 installed on this machine?")
        return 1
    if not ADDONS_DST.exists():
        print("[ERROR] NT8 AddOns dir does not exist: {0}".format(ADDONS_DST))
        return 1

    # A filename provided by both trees would mean two definitions of one type in one
    # assembly. Catch it before writing anything, not after.
    seen = {}
    for label, path in sources:
        if path.name in seen:
            print("[FATAL] {0} is provided by both the {1} and {2} trees.".format(
                path.name, seen[path.name], label))
            print("        Two copies in one assembly is P2-28's shape. Deploying nothing.")
            return 2
        seen[path.name] = label

    identical, drifted, added = [], [], []
    for label, src in sources:
        dst = ADDONS_DST / src.name
        if not dst.exists():
            added.append((label, src.name))
            if not args.verify:
                if not args.dry_run:
                    shutil.copy2(src, dst)
                    print("  [COPIED]  {0:<28} ({1}, new)".format(src.name, label))
                else:
                    print("  [DRY-RUN] {0:<28} ({1}, would copy)".format(src.name, label))
            else:
                print("  [MISSING] {0:<28} ({1}, not deployed)".format(src.name, label))
            continue

        if file_hash(src) == file_hash(dst):
            identical.append((label, src.name))
            if args.verify:
                print("  [OK]      {0:<28} ({1})".format(src.name, label))
        else:
            drifted.append((label, src.name))
            if not args.verify:
                if not args.dry_run:
                    shutil.copy2(src, dst)
                    print("  [SYNCED]  {0:<28} ({1}, differed)".format(src.name, label))
                else:
                    print("  [DRY-RUN] {0:<28} ({1}, would sync)".format(src.name, label))
            else:
                print("  [DRIFT]   {0:<28} ({1})".format(src.name, label))

    # ── the vendored core's strategies ──────────────────────────────────────────────
    # RiskManagerBase.cs + RiskGatekeeper.cs + IntradayStrategyBase.cs go to
    # Custom/Strategies/<subfolder>/, preserving the source's subfolder (Vinay/).
    # FRAMEWORK ONLY — bots are denylisted (_assert_no_bots_in_vendor above) and
    # the generator loops skip them as a second layer. No orphan deletion here:
    # Strategies/Vinay/ holds bots this tool does not own, so it touches ONLY
    # the files the vendored core ships. These deploy in the SAME run as
    # ContractCapGate.cs (an addon, above), because RiskGatekeeper depends on it
    # and NT8 compiles the whole Custom tree as one assembly.
    strat_added, strat_drifted, strat_identical = 0, 0, 0
    if STRATEGIES_SRC.exists():
        print()
        print("[strategies/] {0} -> {1}".format(STRATEGIES_SRC, STRATEGIES_DST))
        for src in sorted(p for p in STRATEGIES_SRC.rglob("*.cs")
                          if p.name.lower() not in BOT_DENYLIST):
            rel = src.relative_to(STRATEGIES_SRC)
            dst = STRATEGIES_DST / rel
            if not dst.exists():
                strat_added += 1
                if not args.verify and not args.dry_run:
                    dst.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(src, dst)
                    print("  [COPIED]  {0}  (core, new)".format(rel))
                elif args.verify:
                    print("  [MISSING] {0}  (core, not deployed)".format(rel))
                else:
                    print("  [DRY-RUN] {0}  (core, would copy)".format(rel))
            elif file_hash(src) == file_hash(dst):
                strat_identical += 1
                if args.verify:
                    print("  [OK]      {0}  (core)".format(rel))
            else:
                strat_drifted += 1
                if not args.verify and not args.dry_run:
                    shutil.copy2(src, dst)
                    print("  [SYNCED]  {0}  (core, differed)".format(rel))
                elif args.verify:
                    print("  [DRIFT]   {0}  (core)".format(rel))
                else:
                    print("  [DRY-RUN] {0}  (core, would sync)".format(rel))

    # ── the browser UI's static files ──────────────────────────────────────────────
    # These are NOT .cs and do not go into bin/Custom -- NT8 compiles that folder and
    # anything else there is noise at best. The bridge serves them from UserDataDir,
    # and until they are here the /ui route returns a 404 that names this script.
    ui_added, ui_drifted, ui_identical = 0, 0, 0
    if UI_SRC.exists():
        print()
        print("[ui/]     {0} -> {1}".format(UI_SRC, UI_DST))
        for src in sorted(p for p in UI_SRC.rglob("*") if p.is_file()):
            rel = src.relative_to(UI_SRC)
            dst = UI_DST / rel
            if not dst.exists():
                ui_added += 1
                if not args.verify and not args.dry_run:
                    dst.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(src, dst)
                    print("  [COPIED]  {0}  (new)".format(rel))
                elif args.verify:
                    print("  [MISSING] {0}  (not deployed)".format(rel))
                else:
                    print("  [DRY-RUN] {0}  (would copy)".format(rel))
            elif file_hash(src) == file_hash(dst):
                ui_identical += 1
                if args.verify:
                    print("  [OK]      {0}".format(rel))
            else:
                ui_drifted += 1
                if not args.verify and not args.dry_run:
                    shutil.copy2(src, dst)
                    print("  [SYNCED]  {0}  (differed)".format(rel))
                elif args.verify:
                    print("  [DRIFT]   {0}".format(rel))
                else:
                    print("  [DRY-RUN] {0}  (would sync)".format(rel))

    owned = set(path.name for _, path in sources)
    orphans = sorted(p.name for p in ADDONS_DST.glob("*.cs") if p.name not in owned)

    print()
    print("=" * 70)
    total_drift = len(drifted) + len(added) + strat_drifted + strat_added
    if args.verify:
        if total_drift == 0:
            print("  ALL IN SYNC ({0} files identical, {1} orphan(s))".format(
                len(identical), len(orphans)))
        else:
            print("  DRIFT DETECTED: {0} differ/missing, {1} identical, {2} orphan(s)".format(
                total_drift, len(identical), len(orphans)))
    elif args.dry_run:
        print("  DRY-RUN: {0} would be written, {1} already identical".format(
            total_drift, len(identical)))
    else:
        print("  DONE: {0} synced, {1} copied (new), {2} already identical".format(
            len(drifted), len(added), len(identical)))
        if STRATEGIES_SRC.exists():
            print("  STRAT: {0} synced, {1} copied (new), {2} already identical".format(
                strat_drifted, strat_added, strat_identical))
        if UI_SRC.exists():
            print("  UI:   {0} synced, {1} copied (new), {2} already identical".format(
                ui_drifted, ui_added, ui_identical))

    if orphans:
        print("  Orphans in AddOns/ (in neither tree -- stale deploys or hand-copies):")
        for name in orphans:
            print("    AddOns/{0}".format(name))

    print("=" * 70)
    print()
    print("Recompile inside NT8 before trusting any of this. Files on disk are not")
    print("loaded code: NT8 compiles Custom/ on demand, and until it does the running")
    print("assembly is still the old one.")

    if args.verify and total_drift > 0:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
