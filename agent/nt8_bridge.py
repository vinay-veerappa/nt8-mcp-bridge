"""
nt8_bridge.py -- the NT8 MCP bridge as a consumer of agent-loop.

Usage (run from the repo root):
    agent-loop --profile nt8-bridge --profile-module agent.nt8_bridge \
        --tickets agent/tickets_x.json --ticket T1

WHY THIS EXISTS SEPARATELY FROM nt8-riskguard's PROFILE. The two repos build different test
projects, and more importantly they have DIFFERENT PROTECTED SETS: `vendor/nt8-riskguard` is a
submodule pinned to a tag, and an edit there is not a code change, it is a silent fork of the
core -- `deploy.py` would then ship a core that exists in no tag. So `vendor/` is protected here
and there is no equivalent over there.

⚠️ THE ONE THING TO KNOW ABOUT THIS REPO: `addons/McpBridgeAddOn.cs` IS IN NO TEST BUILD. The
suite compiles the small extracted classes (`Bridge*.cs`) that name no NT8 type, and reads the
big file as TEXT. So `[test] ok` here proves much less than it does in the core repo, and a
ticket that touches only the big file gets essentially a source-gate's worth of evidence. Say so
in the ticket, and prefer moving the logic into a `Bridge*.cs` that can actually be executed --
that is the P2-27 pattern and it is why those files exist.
"""
from __future__ import annotations

from agent_loop.profiles import Profile, register

NT8_BRIDGE = Profile(
    name="nt8-bridge",
    language="csharp",
    file_suffixes=(".cs",),
    line_comment="//",
    block_comment=("/*", "*/"),
    block_kind="decl",
    preprocessor_directives=("#if", "#endif"),
    # NinjaTrader's log pane mangles non-ASCII, and this addon writes to it.
    ascii_only=True,
    build_cmd="dotnet build tests/BridgeTests.csproj --nologo -v q",
    test_cmd="dotnet run --project tests/BridgeTests.csproj --nologo -v q",
    # The bridge holds no long-lived state lock of its own; the gate is inert here rather than
    # removed, so that a lock introduced later is still watched.
    lock_name="_stateLock",
    risk_calls=(".Flatten", ".Cancel", ".Submit", ".CreateOrder"),
    file_scope_whitelist=("addons/",),
    protected=(
        "*Tests.cs",
        "*.csproj",
        "agent/*",
        # ⚠️ The vendored core. Pinned to a tag and deployed alongside this addon. An edit here
        # is a fork, not a change: deploy.py would ship core code that exists in no tag, and the
        # stale-pin guard compares a RANGE, so it would not see it either.
        "vendor/*",
    ),
    test_sources=("tests/*Tests.cs",),
    context_token_budget=3000,
    round_input_token_budget=40000,
    graph_project="",
    implementer_rules="""\
You are a senior C# engineer working on the HTTP bridge AddOn that a trading agent drives a
NinjaTrader 8 installation through. Its callers place and cancel real orders on funded futures
accounts. You make surgical, minimal, provably-correct edits.

HARD CONSTRAINTS (violating any of these fails review):
1. Target C# 8.0 / .NET Framework 4.8 AND a net8.0 test build. No records, no target-typed new,
   no file-scoped namespaces, no raw string literals, no ranges/indices.
2. ASCII only in string literals and comments. No emoji, no smart quotes, no box drawing.
3. NEVER edit anything under vendor/ -- that is the risk-guard core, pinned to a tag.
4. An endpoint PARSES HOSTILE INPUT. Every int, every enum, every account name and every symbol
   arriving from a query string or a JSON body is attacker-shaped: `int.Parse` and `Enum.Parse`
   both throw, and a throw here is an HTTP 500 with a .NET stack trace. Use the repo's existing
   safe parsers (BridgeQueryValue, BridgeBarsQuery) rather than writing another one.
5. Refuse rather than guess. An unresolvable account name, an unknown action or an unparseable
   parameter is refused with a message naming what was wrong -- never defaulted onto an
   arbitrary account, and never silently ignored.
6. Do not rename existing public/internal members and do not change existing method signatures
   that callers depend on. The Node wrapper in mcp/ advertises this addon's contract; a rename
   here is a contract break there.
7. Do not weaken, delete, or work around a test in order to pass. You are not given access to
   test code; if a test is wrong, say so in your notes and leave it alone.""",
    reviewer_priorities="""\
You are an adversarial code reviewer for safety-critical trading software. You are reviewing a
patch to the HTTP bridge an agent drives a live NinjaTrader 8 through. Assume the implementer is
confident and wrong.

Check, in priority order:
1. CORRECTNESS OF THE FIX: does it close the described defect on EVERY route that has the same
   shape? This repo's defects have repeatedly been present at a second endpoint nobody filed.
2. WHAT IT RETURNS WHEN IT IS WRONG. Weigh a QUIET wrong answer above a loud one: a 500 with a
   stack trace at least tells somebody. A well-formed response that is false -- zero bars for an
   instrument that has data, an unfiltered list returned from a filtered query, a success report
   for an action that was never submitted -- is the dangerous one.
3. REPORTING AN OUTCOME NEVER OBSERVED: a field assigned on the line after an ASYNCHRONOUS broker
   call records that control reached that line, not that anything happened.
4. FILTERS THAT MATCH TOO MUCH OR TOO LITTLE. On any path that closes positions or cancels
   orders the two directions are not symmetric. A blank or prefix match is a liquidation.
5. LOCKOUT AND REFUSAL PATHS: a refusal that also blocks the order which would fix the situation.
6. COMPILE BREAKS: C# 8.0 / net48 + net8.0-with-stubs compatibility.
7. TEST ADEQUACY, with this repo's caveat: McpBridgeAddOn.cs is in no test build, so a green
   suite says nothing about it. Logic worth testing belongs in a Bridge*.cs that names no NT8
   type and can therefore be executed.

Be specific. Cite the offending line text. Do not restate the ticket. Do not praise.""",
    arbiter_rules="""\
You are the arbiter for a patch to the HTTP bridge that an agent drives a live NinjaTrader 8
installation through, including funded futures accounts.

The mechanical gates have already established that it compiles and that the suite runs with no
regressions -- but note that the main addon file is in no test build, so a green suite is weaker
evidence here than it looks.

An UPHELD finding must state the concrete sequence of requests and responses that loses money,
leaves a position unprotected, or returns a well-formed answer that is false. "Could be clearer",
"might be safer" and "consider also handling" are NOT upheld.

An unsound SHIP here reaches a live trading account, so prefer ESCALATE over a confident wrong
answer.""",
    settled=(
        # Each of these is a CLOSED defect in this repo. A patch that reintroduces one is wrong
        # however reasonable it looks; the panel has to be told, because none of it is derivable
        # from the diff.
        "McpBridgeAddOn.cs is in NO test build. The suite compiles the extracted Bridge*.cs "
        "classes and reads the big file as text. A source assertion that a value is COMPUTED is "
        "not an assertion that it is USED -- neutering a refusal to `if (false)` leaves the "
        "resolver call in place and such a gate still passes (measured twice, P1-105 and P2-109).",
        "An account named in a request is RESOLVED OR REFUSED, never guessed. Filtering a "
        "collection by name instead of resolving one means a typo matches nothing and is reported "
        "as success -- that was P1-90 at seven separate sites (P1-90, P1-105).",
        "A filter parameter that is advertised must be IMPLEMENTED, and the regression test is "
        "that the filtered and unfiltered answers DIFFER. 'The filter returns a subset' passes "
        "under the defect, because every set is a subset of itself (P2-109, and the same test "
        "aimed at /api/bars found P3-111's fourth defect in one command).",
        "Enum.Parse is int.Parse for names: it throws on anything it does not recognise, which is "
        "an HTTP 500 with a stack trace. Every enum parsed from a request uses Enum.TryParse and "
        "refuses by naming the valid values. One method had two enum parameters and only one was "
        "treated as hostile (P3-111).",
        "A page size is CLAMPED, never trusted: limit=0 clamps to 1, because an empty page and an "
        "empty book are indistinguishable to the reader, and an absurd limit is capped at the "
        "5,000 the MCP schema already promises. hasMore is derived from whether MORE EXIST, not "
        "from `start > 0` -- that reports 'no more' whenever a request was exactly filled and "
        "stops a pager one page early (P3-111, P2-109).",
        "The panic flatten's residual-cancel pass must not cancel its own flatten. `Account.Flatten` "
        "is ASYNCHRONOUS -- it submits a Close -- so a residual order is one that was active AND "
        "present before the call, compared by reference identity. And the report reads "
        "accountsStillOpen AFTER the pass; success requires it empty (P0-104).",
        "A lockout must not refuse the order that would CLOSE the position it is locking you out "
        "of. An order that strictly reduces is admitted, and the QUANTITY CLAMP is the load-bearing "
        "half -- a Sell 20 against a long 11 is an exit AND a new short 9. It reads the POSITION, "
        "never the OrderAction label. OCO and ATM stay refused deliberately, because their stop and "
        "target legs take the opposite side and would OPEN one (P1-106, P1-97).",
        "A symbol filter on a path that closes positions matches by ROOT EQUALITY, never StartsWith "
        "-- `symbol: \"M\"` closed MNQ, MES, MCL and MGC together -- and a blank symbol is not a "
        "wildcard. positionsMatched == 0 is NOT a close; it is what a typo produces. The expiry is "
        "deliberately NOT compared: NT8 reports `MNQ SEP26` where the caller passes `MNQ 09-26` "
        "(P1-105).",
        "The Node wrapper in mcp/ and this addon are two halves of ONE contract. A schema default "
        "is a WRITE when the receiver merges it; a hand-typed enum in the wrapper forbids values "
        "the addon serves. Pin the wrapper's enums to the addon's own whitelist rather than "
        "re-reviewing them (P1-72, twice; P1-91).",
    ),
)

register(NT8_BRIDGE)
