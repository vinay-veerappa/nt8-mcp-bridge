# Why this harness does not execute the bridge yet

The split plan's section 5 says, correctly:

> If `nt8-mcp-bridge` ships with no test project, the split makes that permanent and
> blesses it.

So this repo ships a harness that runs, fails when it should, and exits non-zero. What it
does **not** do is execute a single line of `McpBridgeAddOn.cs`. That is the open half of
`P2-27`, and it predates the split -- the file was `<Compile Remove>`d from the core's
test project long before these repos existed. The split did not create the gap. It also
must not be allowed to bless it, so here is the gap **measured** rather than described.

## What runs today

```bash
dotnet run --project tests/BridgeTests.csproj    # 69 passed / 0 failed
```

⚠️ **Re-measured 2026-08-14 (session 35). It was `9 passed` when this file was written**, and
the shape of what runs has changed as well as the count -- which is the part worth reading.

**EXECUTED** -- real production code, compiled into this project and run:

- **`addons/BridgeAccountResolver.cs`** (`P1-90`). The account resolution that used to fall
  back to `Sim101`, then to any account at all.
- **`addons/CopierEnforcementView.cs`** (`P3-34`). What `GET /api/copier/config` REPORTS
  about a relationship: whether it is enforcing, and if not, why.
- **`addons/BridgeConnectionPlan.cs` + `addons/BridgeConnectionCatalog.cs`** (`F-17`). The
  connection-name resolution that normalizes en-dash/mojibake spellings and the Config.xml
  configured-connection catalog that gives the endpoint its `configured`/`present` rows.
  The catalog names no NT8 type; the plan's helpers (`NormalizeName`, `RepairMojibake`,
  `RequestKeys`, `KeyMatches`) are exercised over ASCII-hyphen, en-dash, and cp1252-mojibake
  spellings, and the refusal texts that distinguish "no such connection" from
  "configured but not instantiated" from "genuinely ambiguous" are asserted as strings.

Both are their own files for one reason: they name **no NinjaTrader type**, so this project
compiles and runs them without a stub. That is the cheap, repeatable half of `P2-27` --
**when logic inside `McpBridgeAddOn.cs` matters, move the part that names no NT8 type out of
it rather than resigning it to a source-text check.**

**SOURCE-TEXT ONLY** -- these prove the wiring is present, not that it behaves:

- The three `P2-38` assertions, migrated out of the core's `TestP2_38` (they were asserting
  on this file from a repo that no longer contains it, which inverted the dependency
  direction).
- That the vendored core is present -- a missing submodule is a deploy hazard, not an
  inconvenience.
- That `addons/` carries no copy of a core source (`P2-28`'s shape).
- `P1-88`'s unknown-action refusal, `UI7`'s refusal-not-a-NullReference, `P1-80`, and
  `P3-34`'s endpoint wiring.
- A harness self-check that every declared test was invoked.

Each source-text test says in its own docstring that it is one. **A gate that proves less
than it appears to is worse than an absent one unless it says so.**

## The measured gap

Compiling `addons/McpBridgeAddOn.cs` against the vendored core plus
`vendor/nt8-riskguard/tests/TestingStubs.cs`, on `net8.0-windows` with `UseWPF`:

```
330 errors:  312x CS0246 (type not found), 16x CS0234 (namespace), 2x CS0103
23 distinct missing types
```

Two findings worth having:

**1. WPF is not the blocker.** The plan assumed it was, and proposed separating the WPF
surface from the HTTP handlers as the remedy. But `net8.0-windows` + `UseWPF` supplies
`System.Windows.Window`, `Application.Current`, `Dispatcher` and `VisualTreeHelper` for
real. None of the 23 missing types is a WPF type. That refactor is not required.

**2. The real blocker is where the NT8 stubs live.** Of 19 named missing types, **16 are
already stubbed** -- inside `RiskGuardAddOnTests.cs`, a 663 KB file in the core, mixed in
with 926 tests and a `Main()`. They cannot be reused from here without dragging in that
`Main()`. Only three are missing everywhere: `ChartBars`, `DrawingTools`, `LogLevel`, plus
namespace shims for `Gui`, `Code`, `Core` and a `Rectangle`.

## The remedy, in order

1. ~~**In the core**, move the NT8 stub block out of `tests/RiskGuardAddOnTests.cs` into
   `tests/TestingStubs.cs`.~~ ✅ **DONE** (core §5.24). `vendor/nt8-riskguard/tests/TestingStubs.cs`
   exists and is consumable from here. Duplicating the stubs on this side was rejected for the
   reason that still holds: two definitions drift, which is the exact defect `P2-38` was about.
2. Add the three missing stubs and the namespace shims here.
3. Expect a member-level tail (`CS1061`) once the types resolve. This is the unbounded
   part -- the 330 figure counts type-level errors only, so treat it as a floor, not an
   estimate.
4. Then write behavioural tests for the HTTP handlers, and put
   `addons/McpBridgeAddOn.cs` plus the vendored core into `BridgeTests.csproj`.

Until step 4, **do not describe this bridge as tested.** Two production sources are executed
and the rest is source text; that is a real improvement on `9 passed` and it is not the same
thing as coverage of the HTTP handlers.

⚠️ **The cost of the gap, measured rather than argued.** Every defect below was reachable only
by driving the deployed box, because nothing here could execute the handler:

| | Found how |
|---|---|
| `P1-88`/`P1-89` | a copier write reported an unwritten write as persisted |
| `P1-90` | an order named an unresolvable account and was placed on an arbitrary one |
| `P3-34`'s `enforcing` | reported a relationship as enforcing while the copier was in `shadow` |

And in the core, the same session: `P0-96` -- a leader covering a **short** sent the follower a
`Sell`, doubling it -- sat behind **1311 green tests** because no test asserted a short EXIT.
**Coverage counts are not coverage of the cases that matter.**
