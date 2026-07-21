# nt-mcp-server — MCP server for NinjaTrader 8

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Connect AI agents (Claude, Hermes, ChatGPT, Cursor, Cline) to **NinjaTrader 8** via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/).

Through a single stdio interface, this MCP lets an AI agent:

**Account Management**
- List accounts with balances and buying power
- Read open positions with live P&L
- List working orders with their status

**Live Trading**
- Place Market / Limit / StopMarket / StopLimit orders
- Cancel an order by ID/name, or cancel all working orders at once
- Stream real-time quotes (bid, ask, last, volume, daily high/low)

**Strategy Development**
- Author full NinjaScript strategy source
- Compile it in-process via NinjaTrader's own Roslyn compiler — hot-swapped, **no NT8 restart**

**Live Deployment & Monitoring**
- Deploy a compiled strategy onto a chart and enable it — **SIM-first** (live needs explicit confirm)
- List running strategies with state, account, instrument, and position; stop (disable + remove)
- Strategies can POST a per-fill **notification webhook** to an external "AI Gate" (TradingView-style)

**Backtesting**
- Run backtests through the Strategy Analyzer over a configurable **symbol, date range, timeframe, and parameters**
- Read back net P&L, drawdown, gross P/L, trade count, and the full trade list

**Historical Market Data**
- Export OHLCV bar ranges (Minute/Day/Tick/Volume/Range) to CSV
- Build a single-vendor, provenance-tagged 1-minute Postgres archive (`nt8_ohlcv_bars`)
- Keep it current with a scheduled daily incremental updater
- Search instruments by name or symbol

## Architecture

```
AI Client (MCP stdio)  →  nt-mcp-server.js  →  HTTP :7890  →  NT8 McpBridgeAddOn
```

Three layers, zero external APIs, everything runs locally on your machine.

## Quick Start

### 1. Install the NT8 AddOn

1. Open **NinjaTrader 8**
2. `New` → `NinjaScript Editor` (F11)
3. Right-click `AddOns` in the left panel → `New AddOn...`
4. Replace the file contents with `nt8-addon/McpBridgeAddOn.cs`
5. Press **F5** to compile
6. Restart NinjaTrader

Alternatively, copy `nt8-addon/McpBridgeAddOn.cs` to:
```
Documents\NinjaTrader 8\bin\Custom\AddOns\
```
and compile via NinjaScript Editor (F5).

Verify the AddOn is running:
```powershell
curl http://localhost:7890/api/health
# {"status":"ok","timestamp":"...","version":"0.2.1","dev":false}
```

### 2. Start the MCP Server

```bash
node nt-mcp-server.js
```

Expected output:
```
[nt-mcp] Server started — NT8 at http://127.0.0.1:7890
[nt-mcp] Waiting for MCP messages on stdin...
```

### 3. Configure Your AI Client

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["C:/path/to/nt-mcp-server.js"]
    }
  }
}
```

**Hermes Agent** (`~/.hermes/config.yaml`):
```yaml
mcpServers:
  ninjatrader:
    command: node
    args: ['C:\path\to\nt-mcp-server.js']
    transport: stdio
```

## Tools

### Phase 1 — account, trading, data

| Tool | Description |
|------|-------------|
| `nt_health` | Check connection to NinjaTrader 8 |
| `nt_accounts` | List accounts, balances, buying power |
| `nt_positions` | List open positions with PnL |
| `nt_orders` | List working orders with status |
| `nt_place_order` | Place Market / Limit / StopMarket / StopLimit orders |
| `nt_cancel_order` | Cancel an order by ID or name |
| `nt_cancel_all_orders` | Cancel all working orders across all accounts |
| `nt_quote` | Real-time quote (bid, ask, last, volume, daily high/low) |
| `nt_bars` | Historical OHLCV bars (Minute, Day, Tick, Volume, Range) |
| `nt_search` | Search instruments by name or symbol |

### Phase 2 — strategy authoring, compile, backtest

| Tool | Description |
|------|-------------|
| `nt_list_strategies` | List NinjaScript strategy files in `bin\Custom\Strategies` |
| `nt_strategy_source` | Read one strategy's NinjaScript source |
| `nt_create_strategy` | Write full NinjaScript source into `bin\Custom\Strategies` |
| `nt_compile` | Recompile NinjaScript in-process (Roslyn, hot-swap, **no NT8 restart**); returns compile errors |
| `nt_backtest` | Run a backtest via the Strategy Analyzer over a configurable **symbol, date range (`from`/`to`), timeframe (`period`/`periodValue`), and `params`**; returns net P&L, drawdown, gross P/L, trade count + trade list |

**Typical Phase 2 flow:** `nt_create_strategy` (agent writes the NinjaScript) → `nt_compile` (build + hot-load, reports any errors) → `nt_backtest` (run it, read metrics) → iterate.

Example `nt_backtest` — the same strategy over a specific symbol, date range, timeframe, and parameters:
```jsonc
{ "strategy": "MyStrategy", "symbol": "GC 08-26",
  "from": "2026-03-01", "to": "2026-04-30",
  "period": "Minute", "periodValue": 5,
  "params": { "Fast": 5, "Slow": 50 }, "maxTrades": 50 }
```

### Phase 3 — historical data extraction

| Tool | Description |
|------|-------------|
| `nt_export_bars` | Export a **date range** of OHLCV bars to a CSV on the NT8 machine (NT8 downloads missing history on demand). Configurable `symbol`, `from`/`to`, `period`/`periodValue`, and `merge` policy. Returns a summary (rows, actual range, filename). |
| `nt_get_export` | Return the content of an export CSV by filename (for pulling it over the private network). |

**Two extraction modes:**

1. **Return CSV** — `nt_export_bars` writes `mcp_bars_<symbol>_<period>.csv`; fetch it with
   `nt_get_export` (or read the file directly if you're on the NT8 machine).
   ```jsonc
   { "symbol": "GC 08-26", "from": "2020-01-01", "to": "2026-07-10",
     "period": "Minute", "periodValue": 1, "merge": "DoNotMerge" }
   ```
   - `merge`: **`DoNotMerge`** = the single resolved contract; **`MergeNonBackAdjusted`** = a continuous
     series stitched across front months with **no price adjustment** (anchor on a real contract, e.g.
     `GC 08-26`). **Never `MergeBackAdjusted`** for spread/ratio work — it shifts historical prices by
     cumulative roll gaps and corrupts the signal.
   - Depth (Tradovate feed): ES/GC/CL/SI/NQ ~2006–2008, **RTY ~2017**, **M2K ~2019** (launch-limited).
   - Timestamps are NT8-local **bar-close**; convert to your target convention on load (see below).

2. **Load to a Postgres table** — [`nt8_ingest/`](nt8_ingest/) builds a **single-vendor, provenance-tagged**
   1-minute archive (`nt8_ohlcv_bars`) from these exports: per-contract, non-back-adjusted, roll-overlap
   bars kept, UTC bar-open timestamps (converted from NT8's Central bar-close), idempotent + resumable,
   with QA (density/rolls/spot-check) and a `nt8_data_gaps` registry that **records feed holes instead of
   cross-vendor patching them**. See [nt8_ingest/README.md](nt8_ingest/README.md).

### Phase 4 — live deployment, monitoring, alerts

| Tool | Description |
|------|-------------|
| `nt_deploy_strategy` | Add a compiled strategy to an **open chart** and enable it (**SIM-first**). Sets account (default `Sim101`) + `params`; a live account requires `confirmLive: true`. |
| `nt_stop_strategy` | Disable + remove running strategies (filter by class name / account). Does **not** auto-flatten an open position. |
| `nt_strategy_status` | List strategies NT8 is running on an account: state (Realtime/…), account, instrument, timeframe, position, quantity. |

### Phase 5 — Unbound v1.1.0 Endpoints & v1.2 Expansion Pipeline

| Tool | Description |
|------|-------------|
| `nt_capture_chart` | Capture WPF chart window into base64 PNG images for visual inspection of setups and trade executions. |
| `nt_open_chart` | Programmatically open chart windows/tabs for any symbol/timeframe (enables zero-manual-step deploy). |
| `nt_get_logs` | Tail NT8 Output tab logs, Strategy Analyzer output, or `interventions.jsonl` audit files for error diagnosis. |
| `nt_fill_events` | Query account execution history (`account.Executions`) for trade reconciliation and fill audit. |
| `nt_inspect_strategy` | Inspect property declarations, inputs, and parameters of compiled strategies via reflection. |
| `nt_riskguard_state` | Read live RiskGuard FSM state, peak equity drawdown, and daily loss limit snapshots. |
| `nt_copier_config` | *(Upcoming)* Dynamic runtime configuration of `TradeCopierEngine.cs` (Leader/Follower account ratios, Micro/Mini lot scaling). |
| `nt_prop_limits` | *(Upcoming)* Dynamic configuration of `PropFirmProtectionSuite.cs` (Target Profit lock, News Shield buffers). |
| `nt_extract_trades` / `nt_journal_export` | *(Upcoming)* Export trade executions with MAE/MFE, duration, P&L, commissions, and ICT context to TraderSync, TradesViz, or Markdown journals. |
| `nt_trade_chart` | *(Upcoming)* Automated chart screenshots centered on trade entry/exit with buy/sell markers and stop/target price lines. |
| `nt_monte_carlo` | *(Upcoming)* Run $1,000$–$10,000$ iteration Monte Carlo simulations on trade histories to evaluate ruin probability, drawdown confidence bands, and slippage sensitivity. |
| `nt_optimize` | *(Upcoming)* Parameter optimization via Strategy Analyzer (Grid/Genetic search space) and Walk-Forward Analysis (`nt_walk_forward`). |
| `nt_place_atm_order` | *(Upcoming)* Order entry with server-side ATM strategy brackets (stop loss, profit targets, auto-breakeven managed by `DynamicAtmManager.cs`). |
| `nt_draw_level` / `nt_draw_shape` | *(Upcoming)* Plot support/resistance levels, HOD/LOD, Midnight Open, and FVG boxes directly onto NT8 charts via native `Draw.*` methods. |

**Typical Phase 4 flow:** open a chart for the instrument → `nt_deploy_strategy` (add + enable on
Sim101) → `nt_strategy_status` (watch state + position) → `nt_stop_strategy` (disable + remove).

```jsonc
{ "strategy": "PathSignatureUnion", "instrument": "NQ 09-26",
  "account": "Sim101", "params": { "Qty": 1 }, "enable": true }
```

**Strategy alerts (AI-Gate webhook).** A strategy can also POST a **notification** to an external
"AI Gate" on every fill (the TradingView-webhook pattern) — a lean, notify-only payload (`source=nt8`,
`event`, `side`, `qty`, `price`, …) so a downstream relay does **not** cross-execute (NT8 already
filled the trade). This lives inside the NinjaScript strategy (an `AlertUrl` input + fire-and-forget
POST), independent of the MCP tools above.

## Configuration

**MCP server** (`nt-mcp-server.js`, on the AI-client machine):

| Variable | Default | Description |
|----------|---------|-------------|
| `NT8_HOST` | `127.0.0.1` | NT8 AddOn hostname |
| `NT8_PORT` | `7890` | NT8 AddOn HTTP port |

**AddOn** (`McpBridgeAddOn.cs`, on the NinjaTrader machine):

| Variable / marker | Default | Description |
|-------------------|---------|-------------|
| `NT8_MCP_PREFIX` | `http://localhost:7890/` | HTTP bind prefix. Set to `http://+:7890/` to also listen on a **private** VPN interface (e.g. Tailscale) for remote access. Never expose publicly without auth + firewall. |
| `NT8_MCP_DEV` env or `mcp_dev.on` marker file (in the NT8 user-data dir) | off | Enables the dev-only reflection endpoint (`/api/dev/reflect`) for internal probing. Off by default; leave off in normal use. |

## How Phase 2 works

The AddOn calls NinjaTrader's own internal Roslyn compiler (`NinjaTrader.Code.Compiler`) via
reflection, then lets NT8 hot-swap the NinjaScript AppDomain — the same thing pressing **F5** does,
but triggered over HTTP with **no restart**. Backtests are run by driving a bridge-managed
**Strategy Analyzer** window and reading its `SystemPerformance` (the same engine and numbers you get
from the GUI). A successful compile briefly drops the HTTP connection as the AppDomain reloads; the
result is written to a durable file and `nt_compile` reads it back automatically.

## Roadmap

**Shipped:**
- **Phase 1** — account management, live trading, quotes, historical bars, instrument search
- **Phase 2** — strategy authoring, in-process hot-swap compile (no NT8 restart), Strategy Analyzer
  backtesting with configurable symbol / date range / timeframe / parameters
- **Phase 3** — historical data extraction (CSV) **and** a single-vendor, provenance-tagged 1-minute
  Postgres archive (`nt8_ohlcv_bars`) covering all six instruments (CL/GC/SI/ES/NQ/RTY),
  2020→present (~19.6M rows), kept current by a scheduled daily incremental updater
- **Phase 4** — live deployment (`nt_deploy_strategy`, SIM-first), monitoring (`nt_strategy_status`),
  teardown (`nt_stop_strategy`), and per-fill AI-Gate alert webhooks inside the strategies
- **v1.1.0 AddOn Updates** — chart screenshot capture (`nt_capture_chart`), programmatic chart opening (`nt_open_chart`), diagnostic log tailing (`nt_get_logs`), fill history streaming (`nt_fill_events`), and strategy metadata inspection (`nt_inspect_strategy`).

**Upcoming Expansion (v1.4.0 Specification):**
- **Production Safety & Architecture**: Local Bearer token auth, mandatory `idempotencyKey` order deduplication, atomic `nt_emergency_flatten` kill-switch, `interventions.jsonl` action audit logs, `X-NT8-MCP-Version: 1.4.0` headers, and SIM auto-gating for fresh Roslyn builds.
- **Data & Error Standards**: Explicit UTC input/output date contract, ET (`America/New_York`) macro window boundaries, cursor pagination for bars/orders/fills, and standardized JSON error objects.
- **Phase 5 (Observability & Debugging)**: `nt_chart_snapshot` (enhanced screenshot + visual markers, price lines, indicators), `nt_indicator_values` (deep series + running strategy collection), `nt_strategy_debug` (trace logs & variable state dumps).
- **Phase 6 (Advanced Research & Quant Optimization)**: `nt_optimize` + `nt_walk_forward` (Bayesian/Gaussian, Pareto fronts, `run_id` provenance), `nt_portfolio_backtest`, `nt_synthetic_data` (stress scenarios: COVID crash, 2008 shock), `nt_signal_backtest`.
- **Phase 7 (Automation & Workflow Execution)**: `nt_script_execute` (sandboxed C# snippet execution), `nt_schedule` / `nt_task`, `nt_trade_journal` (CRUD + tags), `nt_alert` / `nt_webhook`.
- **Phase 8 (Risk, Compliance & Prop Firm Suite)**: `nt_riskguard_config` (trailing DD, vol limits, time restrictions), `nt_compliance_report`, `nt_multi_account_orchestrator`, `nt_subscribe` (SSE real-time event stream channel for fills, FSM transitions, errors).




## Requirements

- **Node.js 18+** (uses only built-in modules — zero npm dependencies)
- **NinjaTrader 8** (any license: free, trial, or lifetime)
- **Windows** (NinjaTrader only runs on Windows)

## License

MIT — do what you want, no strings attached. See [LICENSE](LICENSE).

## Credits

- **Phase 1** (accounts, trading, quotes, bars, instrument search) — original work by
  [Igor](https://github.com/Wendigooor) and his AI agent Hermes.
- **Phase 2** (strategy authoring, in-process compile with hot-swap, and Strategy Analyzer
  backtesting with configurable symbol / date range / timeframe / parameters) — by
  [**Quant Trading Pro**](https://www.quanttradingpro.com/).