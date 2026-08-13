"""
js_ninjatrader_mcp.py — agent-loop profile for the `ninjatrader-mcp` server.

WHY THIS EXISTS. tvDownloadOHLC's `python-tvdownloadohlc` profile cannot take a
ticket against this repo: its `build_cmd` is `py_compile`, which errors on a `.js`
file, and its `test_cmd` is two **Python** suites that pass no matter what a patch
does here. Pointing it at this server would produce a gate that cannot fail — the
exact failure that profile's own comments warn about. So the MCP wrapper had no
loop coverage at all, which is part of why `P1-72`…`P1-75` and `P1-91` were every
one of them found by hand.

Paths are relative to THIS repo root, and the loop must be run from here. See
`agent/__init__.py` for why the profile cannot live in the parent repo.

Usage:
    "C:/Users/vinay/tvDownloadOHLC/.venv/Scripts/python.exe" -m agent_loop \
        --profile js-ninjatrader-mcp --profile-module agent.js_ninjatrader_mcp \
        --tickets agent/tickets_p191.json --ticket T1
"""
from __future__ import annotations

from agent_loop.profiles import Profile, register

JS_NINJATRADER_MCP = Profile(
    name="js-ninjatrader-mcp",
    language="javascript",
    file_suffixes=(".js", ".mjs", ".cjs"),
    line_comment="//",
    block_comment=("/*", "*/"),
    block_kind="brace",
    preprocessor_directives=(),
    # `node --check` is a PARSE check, not a type check -- JS has no compile step.
    # It is still worth having: this server is launched by the MCP client, so a
    # syntax error here does not surface as a build failure. It surfaces as every
    # nt_* tool silently disappearing from the agent's toolset.
    #
    # {files} is substituted with the files the patch actually touched. Do NOT name
    # a fixed file here; that makes the gate pass regardless of what the patch did.
    build_cmd="node --check {files}",
    # The real gate, ~0.1s.
    #
    # ⚠️ THE QUOTED GLOB IS LOAD-BEARING. `node --test <dir>` is not directory
    # discovery in Node 24 -- it resolves the path as a *file* and dies with
    # MODULE_NOT_FOUND, which the loop reads as a red baseline and then refuses
    # every ticket. Node expands the quoted pattern itself, so this does not depend
    # on a shell.
    test_cmd='node --test "tests/*.test.js"',
    # No lock primitive: the server is single-threaded and request-serial.
    lock_name="",
    risk_calls=(),
    # Developer mode may touch the server and its lib, not the tests.
    file_scope_whitelist=("lib/", "nt-mcp-server.js"),
    # fnmatch patterns against the whole relative path, so a bare directory name
    # matches NOTHING -- that bug made "web/" and "data/" inert on the Python
    # profile and had to become "web/*" and "data/*".
    protected=(
        "*.test.js",
        "tests/*",
        "agent/*",
        "nt8-addon/*",
        "nt8_ingest/*",
    ),
    test_sources=("tests/*.test.js",),
    context_token_budget=3000,
    round_input_token_budget=40000,
    implementer_rules="""\
You are a senior Node.js engineer working on an MCP (Model Context Protocol)
server that fronts a live NinjaTrader 8 trading platform. Tool calls issued
through it place real orders. You make surgical, minimal, provably-correct edits.

HARD CONSTRAINTS:
1. ESM, Node 18+. `package.json` sets `"type": "module"`, so use `import` and
   `export`. `require` and `module.exports` are NOT available and fail at runtime
   rather than at parse time.
2. No new dependencies. This server has none beyond Node builtins.
3. Do not rename an existing tool, or remove a property another tool's handler
   reads. An MCP tool name is a published contract.
4. Preserve the existing 2-space indentation and the aligned-property style
   inside `inputSchema.properties`.
5. Fail closed. If a request cannot be resolved to exactly one account, the tool
   must REFUSE, not choose. That is `P1-90`: the NT8 addon used to fall back to the
   account named "Sim101", then to any account at all, and place the order there.
6. A schema `default:` is not documentation. An MCP client may materialise it into
   the request, at which point it is an argument the caller never sent. Never add a
   `default:` to a field naming an account, an order side, a quantity or a price.
7. Do not weaken or delete tests to pass.""",
    reviewer_priorities="""\
Judge this patch as code that can move money. In priority order:

1. Can any change here cause an order to reach an account the caller did not
   name? That is `P1-90`, it shipped, and it was live for months.
2. Does a schema still advertise a `default:` for an account, side, quantity or
   price? A client may inject it, so a default is a silently-supplied argument.
3. Does the schema now disagree with what the NT8 addon does? The addon refuses a
   missing or unresolvable account; a schema saying otherwise sends callers into a
   refusal, or papers over one.
4. Is `required` accurate, and was it APPENDED to rather than rewritten? Dropping
   `idempotencyKey` from an order tool would admit duplicate orders.
5. Are the tests real? A test asserting on source TEXT proves less than one
   asserting on the exported schema object. Say which kind it is.""",
    arbiter_rules="""\
This profile fronts a live trading platform, so weight the panel accordingly.

* A finding that an order could reach an unnamed account outranks everything.
* A finding about a schema `default:` on an account/side/quantity/price field is
  REAL, not style. Do not dismiss it as documentation.
* Prefer refusal over inference in every disputed case.
* A test that only greps source text is acceptable when the value cannot be
  imported, and must be LABELLED as a source assertion. Do not let it be presented
  as behavioural coverage.""",
    settled=(
        "P1-90: an account that cannot be resolved is REFUSED, never substituted. "
        "Do not propose restoring any fallback -- not to Sim101, not to the first "
        "connected account, not to a single-account convenience case. Fixed in "
        "nt8-mcp-bridge/addons/BridgeAccountResolver.cs and live-validated.",
        "A schema `default:` is treated as a possible injected argument, not as "
        "documentation. Do not argue that MCP clients never materialise defaults; "
        "the specification permits it, and whether a given client does is a "
        "property of that client, not of the contract.",
        "`account` stays OPTIONAL on tools whose handler treats its absence as "
        "'all accounts' (nt_orders, nt_fill_events, nt_trade_chart, "
        "nt_riskguard_state, nt_extract_trades, nt_stop_strategy, "
        "nt_set_strategy_param). Only the five tools whose handler REFUSES without "
        "an account may declare it required. A passing test pins this boundary; do "
        "not argue for widening it.",
    ),
)

register(JS_NINJATRADER_MCP)
