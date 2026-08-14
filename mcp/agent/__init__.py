"""agent — registers this repo's agent-loop profile.

Run the loop with THIS repo as the working directory, or
`--profile-module agent.js_ninjatrader_mcp` cannot resolve:

    "C:/Users/vinay/tvDownloadOHLC/.venv/Scripts/python.exe" -m agent_loop \
        --profile js-ninjatrader-mcp --profile-module agent.js_ninjatrader_mcp \
        --tickets agent/tickets_p191.json --ticket T1

This repo has no `.venv` of its own. `agent-loop` is installed in
tvDownloadOHLC's venv, which is the same arrangement nt8-riskguard uses and
documents.

⚠️ WHY THE PROFILE IS HERE AND NOT IN tvDownloadOHLC. This repo is a SUBMODULE of
tvDownloadOHLC, with its own history and remote. The loop patches inside a git
worktree, and a worktree of the parent does not check submodules out — so a
profile in the parent naming `mcp/ninjatrader-mcp/lib/tools.js` would resolve
during `--list` (which reads the live tree) and then find nothing to patch. Paths
here are relative to THIS repo root.

`python -m agent_loop ... --list` is the free check that the loop can still start
after any move like that, and it is what caught the equivalent breakage in
nt8-riskguard, where every invocation had failed at import for two sessions.
"""
from .js_ninjatrader_mcp import JS_NINJATRADER_MCP

__all__ = ["JS_NINJATRADER_MCP"]
