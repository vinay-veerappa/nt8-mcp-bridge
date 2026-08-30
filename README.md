# nt8-mcp-bridge

An HTTP bridge inside NinjaTrader 8. It exposes accounts, orders, positions, bars, charts
and the risk guard's own state to external tooling (an MCP server, scripts, agents), and
it drives the dynamic ATM manager.

Extracted from `tvDownloadOHLC` on 2026-08-12 with full history (see
[NT8_REPO_SPLIT_PLAN.md](https://github.com/vinay-veerappa/nt8-riskguard/blob/main/docs/NT8_REPO_SPLIT_PLAN.md)).
That link deliberately points at the core's `main`, not at the copy under `vendor/`: the
submodule is pinned to a tag, so its docs are frozen at whatever that tag said.

## Layout

```
addons/     McpBridgeAddOn.cs -- the bridge
tests/      BridgeTests.csproj + the harness (read tests/README.md first)
tools/      deploy.py -- deploys this repo AND the vendored core, together or not at all
vendor/     nt8-riskguard/ -- git submodule, pinned to a tag
```

## The vendored core is not optional

NinjaTrader has no package manager. Every AddOn compiles into one assembly
(`NinjaTrader.Custom.dll`) and calls the others' types directly, so this repo needs a
**compile-time source dependency** on [nt8-riskguard](https://github.com/vinay-veerappa/nt8-riskguard),
not a package reference. It reaches that code through two singleton facades,
`RiskGuardAddOn.Instance` and `TradeCopierEngine.Instance` -- about 26 members.

```bash
git clone --recurse-submodules https://github.com/vinay-veerappa/nt8-mcp-bridge.git
# or, in an existing clone:
git submodule update --init
```

The submodule is pinned to a commit, not a branch, so this repo always states which core
it was built against. **Currently `4c179e2`** (post-v1.65.0, adopts the full live
strategy suite). Do not trust that sentence -- it is
hand-maintained and has been stale before. `git submodule status` prints the truth:

```bash
git submodule status          # -> <sha> vendor/nt8-riskguard
```

To move to a newer core:

```bash
# in nt8-riskguard: tag and PUSH first -- a submodule cannot resolve a
# tag that exists only locally
cd vendor/nt8-riskguard && git fetch --tags && git checkout <new-tag>
cd ../.. && dotnet run --project tests/BridgeTests.csproj      # against the NEW core
git add vendor/nt8-riskguard && git commit -m "chore: bump core to <new-tag>"
```

### ⚠️ Never leave the pin behind the core

`deploy.py` deploys the vendored core **as well as** the bridge, so a stale pin does not
merely fail to bring a fix across -- it **overwrites a newer core already live in NT8 and
silently reverts it**. On 2026-08-12 the pin sat at `v1.0.1` while `v1.0.2` was deployed and
running; `v1.0.2` carries `P0-63`, the fix without which the mirrored follower stop had
never trailed. Nothing would have warned.

✅ **It has since fired for real, and it was right.** On 2026-08-13 the core moved to `v1.1.0`
carrying three live fixes while this pin sat at `v1.0.3`; `deploy.py` refused, and deploying
anyway would have reverted them in NT8. The working order is **tag core → push → bump pin →
push → deploy → recompile**.

So `deploy.py` now **refuses (exit 2)** when the pinned commit is a strict ancestor of a
sibling `nt8-riskguard` checkout's `main`. Strictly-behind is the only unsafe case: equal,
ahead, and unknown are all allowed, and a missing sibling checkout only warns -- refusing on
"I could not tell" would block a legitimate deploy on a machine that has only this repo.
`--verify` and `--dry-run` are never blocked.

**The dependency is one-way.** This repo depends on the core; the core must never depend on
this one. `nt8-riskguard/tools/check_direction.py` enforces that from the other side.

## Build and test

```bash
dotnet run --project tests/BridgeTests.csproj    # 9 passed / 0 failed
```

⚠️ **This harness does not execute `McpBridgeAddOn.cs`.** It asserts against its source
text. That is the open half of defect `P2-27` and it predates the split.
[tests/README.md](tests/README.md) measures the gap exactly -- 330 compile errors, 23
missing types, 16 of which are already stubbed in the core's test file -- and gives the
ordered remedy. Until that is done, **this bridge is not tested.**

**CI** runs the harness on every push and pull request, on `windows-latest`, from
[.github/workflows/ci.yml](.github/workflows/ci.yml) -- active since 2026-08-13. It also
hides the vendored core and asserts `deploy.py --dry-run` exits **2**, so the refusal to
half-deploy is a check rather than a comment claiming there is one. Deploy parity is
deliberately absent: it compares against an NT8 install that exists only on the trading
machine, so on a runner it would pass vacuously.

### Install the pre-commit hook -- once per clone

```bash
git config core.hooksPath .githooks
```

`.githooks/pre-commit` refuses build output, binaries and anything over 50 MB -- the
hazard `.gitignore` alone does not cover, since `git add -f` walks past it.
`core.hooksPath` is **local config, not tracked**, so a fresh clone has no hook until you
run that line, and nothing will tell you. Override: `ALLOW_BIG_FILES=1 git commit ...`.

## Deploy

```bash
python tools/deploy.py --verify    # what has drifted?
python tools/deploy.py            # deploy both trees
```

Then recompile inside NT8. Files on disk are not loaded code.

`deploy.py` **refuses to deploy this repo alone**: the bridge cannot compile without the
core, and in NT8 one compile error fails the whole Custom assembly, so every addon stops
loading -- the risk guard included. A half-deploy does not degrade the bridge, it disarms
the thing protecting the account.

Because this tool owns the union of both trees, it is the authoritative deploy-parity
check. The core's own `tools/sync_nt8.py` deliberately reports files it does not own as
"unmanaged" rather than orphaned, since naming them would make the core aware of the
bridge.

## Security

The HTTP listener is unauthenticated unless a token is configured, via the `NT8_MCP_TOKEN`
environment variable or `<NT8 UserDataDir>/mcp_token.txt`. With no token set, any local
process can flatten positions and place orders.
