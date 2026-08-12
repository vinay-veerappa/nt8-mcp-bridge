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
dotnet run --project tests/BridgeTests.csproj    # 9 passed / 0 failed
```

- The three `P2-38` source assertions, migrated out of the core's `TestP2_38` (they were
  asserting on this file from a repo that no longer contains it, which inverted the
  dependency direction).
- That the vendored core is present -- a missing submodule is a deploy hazard, not an
  inconvenience.
- That `addons/` carries no copy of a core source (`P2-28`'s shape).
- A harness self-check that every declared test was invoked.

These are source-text assertions. They prove less than an execution would, and they prove
exactly the thing that regressed in `P2-38`.

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

1. **In the core**, move the NT8 stub block out of `tests/RiskGuardAddOnTests.cs` into
   `tests/TestingStubs.cs`. Mechanical, same compilation unit, so it is semantically
   neutral -- but re-verify 926 tests and both mutation batteries, then cut a new tag and
   re-pin the submodule here. Do this first: it is the structural fix, and duplicating the
   stubs on this side instead would give two definitions that drift, which is the exact
   defect `P2-38` was about.
2. Add the three missing stubs and the namespace shims here.
3. Expect a member-level tail (`CS1061`) once the types resolve. This is the unbounded
   part -- the 330 figure counts type-level errors only, so treat it as a floor, not an
   estimate.
4. Then write behavioural tests for the HTTP handlers, and put
   `addons/McpBridgeAddOn.cs` plus the vendored core into `BridgeTests.csproj`.

Until step 4, **do not describe this bridge as tested.**
