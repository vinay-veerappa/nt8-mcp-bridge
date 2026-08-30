# nt-mcp-server — MCP server for NinjaTrader 8

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Connect AI agents (GitHub Copilot, Claude, Cursor, Cline, etc.) to **NinjaTrader 8** via the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/).

Through a single stdio interface, this MCP server lets an AI agent:

- **Account & Risk** — list accounts/positions/orders, read RiskGuard FSM state, pull compliance reports
- **Live Trading** — place Market / Limit / StopMarket / StopLimit / OCO / ATM orders, cancel/change orders, close positions, emergency flatten
- **Quotes & Data** — stream real-time quotes, historical bars, export date ranges to CSV, search instruments
- **Strategy Development** — author NinjaScript source, compile in-process (Roslyn hot-swap, **no NT8 restart**), backtest, inspect, deploy, stop, and tune parameters
- **Research & Automation** — signal backtests, portfolio backtests, synthetic stress data, Monte Carlo, scheduled tasks, C# snippet execution, chart drawing/capture
- **Observability** — tail NT8 logs, capture chart screenshots, stream fills/FSM events via SSE, export trade journals

## Architecture

```
AI Client (MCP stdio)  →  nt-mcp-server.js  →  HTTP :7890  →  NT8 McpBridgeAddOn
```

Three layers, zero external APIs, everything runs locally on the NT8 machine.

## Tests

```bash
npm test          # 43 tests, node:test, still zero dependencies
```

⚠️ **Not `node --test tests/`** — on Node ≥ 22 the directory is resolved as a module path
and the run dies with `MODULE_NOT_FOUND`, which looks exactly like a legitimate red
baseline. Use `npm test`.

Coverage is `lib/copier-config-request.js` and `lib/tools.js`, and that is deliberate about
where it lives: `nt-mcp-server.js` starts a stdin readline loop at import, so a test of a
function defined inside it hangs. **Any request-building logic worth testing belongs in
`lib/`.** The mapping for `nt_copier_config` was moved there after four defects
(`P1-72`…`P1-75`) were found in it — see
[`nt8-riskguard`'s hardening plan](https://github.com/vinay-veerappa/nt8-riskguard/blob/main/docs/RISKGUARD_COPIER_HARDENING_PLAN.md).

Two rules those defects establish, both enforced by tests here:

1. **A read must not write.** `action: get` uses HTTP `GET` and carries no body.
2. **A write sends only the fields you named.** The engine *merges*, so a schema `default`
   that reaches the body overwrites stored config. There is deliberately **no `default:`**
   on any value field of `nt_copier_config`, and an unknown `action` throws rather than
   falling through to a read.

## Version

**AddOn + MCP server version: 1.5.0** (`X-NT8-MCP-Version: 1.5.0`)

⚠️ **The server must be restarted to pick up a change to this file or `lib/`.** A client
that spawned `nt-mcp-server.js` earlier is still running the old tool schema.

## Quick Start

### 1. Install the NT8 AddOn

1. Open **NinjaTrader 8**
2. `New` → `NinjaScript Editor` (F11)
3. Right-click `AddOns` in the left panel → `New AddOn...`
4. Replace the file contents with `nt8-addon/McpBridgeAddOn.cs` (or the compiled source at `C:\Users\<user>\Documents\NinjaTrader 8\bin\Custom\AddOns\McpBridgeAddOn.cs`)
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
# {"status":"ok","timestamp":"...","version":"1.5.0","dev":true|false,"accounts":N,"feedConnected":true|false}
```

### 2. Start the MCP Server

```bash
node nt-mcp-server.js
```

Expected output:
```
[nt-mcp] Server v1.5.0 started — NT8 at http://127.0.0.1:7890
[nt-mcp] Waiting for MCP messages on stdin...
```

### 3. Configure Your AI Client

**VS Code / GitHub Copilot** (`.vscode/mcp.json`):
```json
{
  "servers": {
    "ninjatrader": {
      "command": "node",
      "args": ["C:/path/to/nt-mcp-server.js"],
      "env": {
        "NT8_MCP_TOKEN": "YOUR_TOKEN_HERE"
      }
    }
  }
}
```

**Claude Desktop** (`claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["C:/path/to/nt-mcp-server.js"],
      "env": { "NT8_MCP_TOKEN": "YOUR_TOKEN_HERE" }
    }
  }
}
```

## Tools Reference

The server exposes the following MCP tools, mapped to the HTTP endpoints listed in the **Endpoint column**.

### Account, Position, Order

| Tool | Endpoint | Description |
|------|----------|-------------|
| `nt_health` | `GET /api/health` | Bridge status, version, dev mode, account count, feed connected |
| `nt_connection` | `GET/POST /api/connection` | List connections (incl. configured-but-not-instantiated Config.xml rows with `configured`/`present` flags) or connect/disconnect one. Name resolution is normalized: en-dash, ASCII-hyphen, and cp1252-mojibake spellings all resolve to the canonical name; a configured connection with no live `Connection` object is refused `NOT_INSTANTIATED`. Connect/Disconnect marshal to the NT8 UI thread; `disconnect` is REFUSED while anything on the connection is live unless `confirmDisruptive` is set. |
| `nt_accounts` | `GET /api/account` | List accounts with balances, PnL, buying power |
| `nt_positions` | `GET /api/positions` | Open positions with market position, quantity, avg price, unrealized PnL |
| `nt_orders` | `GET /api/orders?account=&limit=&offset=` | Working/historical orders with state, price, quantity |
| `nt_place_order` | `POST /api/order` | Place Market / Limit / StopMarket / StopLimit / MIT order |
| `nt_place_oco_order` | `POST /api/order/oco` | Place paired OCO entry + stop + target |
| `nt_place_atm_order` | `POST /api/order/atm` | Place order bound to a server-side ATM strategy |
| `nt_change_order` | `POST /api/order/change` | Modify limit/stop price or quantity of a working order |
| `nt_cancel_order` | `POST /api/order/cancel` | Cancel by `orderId` or entire OCO group by `ocoId` |
| `nt_cancel_all_orders` | `POST /api/orders/cancel-all` | Cancel all working orders across accounts |
| `nt_close_position` | `POST /api/position/close` | Flatten a symbol position for an account |
| `nt_emergency_flatten` | `POST /api/emergency-flatten` | Atomic panic kill-switch: cancel all, flatten all, optional lockout |
| `nt_atm_bracket_status` | `GET /api/order/atm/status` | Query active ATM bracket by `bracketId`, or list all active brackets |

### Quotes, Bars, Instruments

| Tool | Endpoint | Description |
|------|----------|-------------|
| `nt_quote` | `GET /api/quote?symbol=` | Real-time quote: bid, ask, last, volume, high, low |
| `nt_bars` | `GET /api/bars?symbol=&period=&periodValue=&count=&format=` | Historical OHLCV bars (bar-close time in NT8 timezone). `format=columnar` returns six parallel arrays (~40% fewer tokens); default `rows` unchanged |
| `nt_export_bars` | `POST /api/bars/export` | Export date range of OHLCV to CSV on NT8 machine |
| `nt_get_export` | `GET /api/export?name=` | Retrieve exported CSV content |
| `nt_list_exports` | `GET /api/exports` | List `mcp_*.csv` export files (name, size, modified), newest first |
| `nt_delete_export` | `POST /api/exports/delete` | Delete one `mcp_*.csv` export file (name-gated: mcp_ prefix, .csv only) |
| `nt_search` | `GET /api/search?query=` | Search instruments by name or symbol |

### Strategy Authoring, Compile, Backtest

| Tool | Endpoint | Description |
|------|----------|-------------|
| `nt_list_strategies` | `GET /api/strategies` | List NinjaScript source files in `bin\Custom\Strategies` |
| `nt_strategy_source` | `GET /api/strategy/source?name=` | Read source of one strategy |
| `nt_create_strategy` | `POST /api/strategy/create` | Write full NinjaScript source to `bin\Custom\Strategies` |
| `nt_list_indicators` | `GET /api/indicators` | List NinjaScript source files in `bin\Custom\Indicators` |
| `nt_indicator_source` | `GET /api/indicator/source?name=` | Read source of one indicator |
| `nt_create_indicator` | `POST /api/indicator/create` | Write full NinjaScript indicator source to `bin\Custom\Indicators` |
| `nt_compile` | `POST /api/compile` then `GET /api/compile/result` | In-process Roslyn compile + hot-swap; returns errors/warnings |
| `nt_backtest` | `POST /api/backtest` | Run Strategy Analyzer backtest |
| `nt_portfolio_backtest` | `POST /api/backtest/portfolio` | Multi-symbol simultaneous backtests with correlation matrix |
| `nt_signal_backtest` | `POST /api/backtest/signal` | Lightweight what-if signal rule testing |
| `nt_inspect_strategy` | `GET /api/strategy/inspect?name=` | Reflect strategy properties and inputs |
| `nt_strategy_status` | `GET /api/strategy/running` | List running strategies, state, position |
| `nt_deploy_strategy` | `POST /api/strategy/deploy` | Add compiled strategy to an **open chart** (SIM-first). Best-effort: NT8 has no public API to open a chart or attach a strategy from an AddOn, so the chart must already be open. |
| `nt_stop_strategy` | `POST /api/strategy/stop` | Disable + remove running strategies |
| `nt_set_strategy_param` | `POST /api/strategy/param` | Change inputs on a running strategy live |

### Observability & Logs

| Tool | Endpoint | Description |
|------|----------|-------------|
| `nt_get_logs` | `GET /api/logs?tab=&lines=` | Tail Output tab, Strategy Analyzer, or `interventions.jsonl` |
| `nt_fill_events` | `GET /api/events/fills?account=&count=&offset=` | Query account execution fill history, paged backwards from the most recent (count 1-1000, default 50) |
| `nt_events_since` | `GET /api/events/since?since=&count=` | Poll bridge intervention events (guard actions, writes, orders) after a UTC instant, from the same audit tail the UI events pane reads. Stateless poll, not a stream; `truncated=true` flags an incomplete window |
| `nt_chart` | `GET /api/chart/capture?symbol=` · `POST /api/chart/snapshot` · `POST /api/chart/trade` | One chart tool with a `mode` enum: capture = active window as base64 PNG (symbol matching best-effort, falls back to any visible chart); snapshot = high-res with markers/indicators; trade = screenshot centered on a fill. |
| `nt_charts` | `GET /api/chart/list` | List every open chart window: instrument, visibility, size, dispatcher thread. Read-only precondition check for capture/draw. |
| `nt_open_chart` | `POST /api/chart/open` | Validates instrument and focuses Control Center. NT8 has no public AddOn API to open a chart window — use Ctrl+Shift+N |
| `nt_draw_level` | `POST /api/chart/draw` | Draw line/rectangle/text onto a chart (best-effort; requires visible chart for exact instrument) |

### Research, Risk, Automation

| Tool | Endpoint | Description |
|------|----------|-------------|
| `nt_extract_trades` | `GET /api/trades/extract?account=&from=&to=&format=` | Export trade records with MAE/MFE, commissions, tags |
| `nt_monte_carlo` | `POST /api/trades/monte-carlo` | Block-bootstrap Monte Carlo over trade history |
| `nt_synthetic_data` | `POST /api/data/synthetic` | Generate stress-scenario OHLCV datasets |
| `nt_trade_journal` | `POST /api/trades/journal` | CRUD + tag + export trade journal entries |
| `nt_schedule` | `POST /api/schedule/task` | Register cron-based tasks inside NT8 |
| `nt_alert` | `POST /api/alert/create` | Create persistent price/indicator alerts |
| `nt_riskguard_state` | `GET /api/riskguard/fsm-state?account=&instrument=` | Read RiskGuard FSM state, drawdown, limits |
| `nt_riskguard_config` | `POST /api/riskguard/config` | Configure trailing drawdown, vol caps, blackouts |
| `nt_compliance_report` | `GET /api/compliance/report?account=` | Generate prop/broker compliance report |
| `nt_copier_config` | `POST /api/copier/config` | Get/Set TradeCopierEngine leader/follower config |
| `nt_prop_limits` | `POST /api/prop/limits` | Get/Set PropFirmProtectionSuite rules |
| `nt_multi_account_orchestrator` | `POST /api/orchestrator/multi-account` | Coordinated multi-account order routing/hedging |
| `nt_indicator_values` | `GET /api/indicator/values?symbol=&indicatorName=` | Retrieve indicator values. Scans loaded assemblies to resolve the NinjaScript indicator host, fixing the original `NinjaTrader.Custom` AssemblyLoadContext mismatch |

## Instrument Symbols

**Root tickers like `ES`, `NQ`, `MNQ` are rejected as unknown instruments.** Use the full futures format that matches the active front month:

```json
{ "symbol": "ES 09-26" }   // resolves to ES SEP26
{ "symbol": "NQ 09-26" }   // resolves to NQ SEP26
{ "symbol": "MNQ 09-26" }  // resolves to MNQ SEP26
{ "symbol": "GC 08-26" }   // resolves to GC AUG26
```

Use `GET /api/search?query=NQ` to discover available contract months.

## Common Payload Examples

### Place a market order
```jsonc
POST /api/order
{
  "symbol": "MNQ 09-26",
  "action": "buy",
  "quantity": 1,
  "orderType": "Market",
  "idempotencyKey": "uuid-or-unique-string"
}
```

### Place an OCO order
```jsonc
POST /api/order/oco
{
  "symbol": "MNQ 09-26",
  "action": "buy",
  "quantity": 1,
  "stopPrice": 28400.0,
  "targetPrice": 28700.0,
  "idempotencyKey": "oco-uuid"
}
```
Note: use `targetPrice`, not `limitPrice`.

### Place an ATM order
```jsonc
POST /api/order/atm
{
  "symbol": "MNQ 09-26",
  "action": "buy",
  "quantity": 1,
  "strategyName": "Standard_ATM",
  "stopTicks": 20,
  "targetTicks": 40,
  "idempotencyKey": "atm-uuid"
}
```

### Run a backtest
```jsonc
POST /api/backtest
{
  "strategy": "IBBreakoutBot",
  "symbol": "NQ 09-26",
  "from": "2026-07-20",
  "to": "2026-07-25",
  "period": "Minute",
  "periodValue": 5,
  "params": { "ActivePlay": 1 },
  "maxTrades": 50
}
```

### Export bars to CSV
```jsonc
POST /api/bars/export
{
  "symbol": "NQ 09-26",
  "from": "2026-07-20",
  "to": "2026-07-25",
  "period": "Minute",
  "periodValue": 5,
  "merge": "DoNotMerge",
  "timeoutSec": 180
}
```
Fetch the file with `GET /api/export?name=mcp_bars_NQ_09_26_Minute5.csv`.

### Deploy a strategy to a chart
```jsonc
POST /api/strategy/deploy
{
  "strategy": "MyStrategy",
  "instrument": "NQ 09-26",
  "account": "Sim101",
  "params": { "Qty": 1 },
  "enable": true,
  "confirmLive": false
}
```
**Important**: the chart must already host at least one strategy for that instrument via the NT8 Strategies dialog; `nt_deploy_strategy` can then add/manage further strategies on it.

### Emergency flatten
```jsonc
POST /api/emergency-flatten
{
  "account": "Sim101",
  "lockoutMinutes": 5,
  "idempotencyKey": "panic-uuid"
}
```

## Configuration

**MCP server** (`nt-mcp-server.js`, on the AI-client machine):

| Variable | Default | Description |
|----------|---------|-------------|
| `NT8_HOST` | `127.0.0.1` | NT8 AddOn hostname |
| `NT8_PORT` | `7890` | NT8 AddOn HTTP port |
| `NT8_MCP_TOKEN` | — | Bearer token for bridge auth |

**AddOn** (`McpBridgeAddOn.cs`, on the NinjaTrader machine):

| Variable / marker | Default | Description |
|-------------------|---------|-------------|
| `NT8_MCP_PREFIX` | `http://localhost:7890/` | HTTP bind prefix. Set to `http://+:7890/` to also listen on a **private** VPN interface (e.g. Tailscale) for remote access. Never expose publicly without auth + firewall. |
| `NT8_MCP_DEV` env or `mcp_dev.on` marker file (in the NT8 user-data dir) | off | Enables dev-only reflection endpoint (`/api/dev/reflect`) for internal probing. Off by default; leave off in normal use. |

## Important Operational Rules

### ALWAYS use the MCP tool for compile
`nt_compile` calls `POST /api/compile` and polls `GET /api/compile/result`. Direct HTTP calls (`curl`, `Invoke-RestMethod`, Python `requests`) crash because the compile hot-swap resets the connection. The MCP server handles this correctly.

### Instrument symbols must include contract month
Using `ES`, `NQ`, `MNQ` alone returns `unknown instrument`. Use `ES 09-26`, `NQ 09-26`, `MNQ 09-26`.

### Use temp JSON files for curl on Windows
PowerShell strips double quotes from inline JSON. Always write the body to a file:
```powershell
$body = @{ symbol='MNQ 09-26'; action='buy'; quantity=1; orderType='Market'; idempotencyKey='test-1' } | ConvertTo-Json -Compress
$body | Set-Content -Path C:\tmp\order.json -NoNewline
curl.exe -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" --data @C:\tmp\order.json http://localhost:7890/api/order
```

### PowerShell expands `$` variables in inline JSON
JSON bodies containing `$ref`, `$result`, `$type`, etc., must be passed via a file. PowerShell expands `$` as variables when used inline with `python -c` or `curl --data '...'`.
```powershell
$body = '{"op":"describe","args":["$result"]}'
$body | Set-Content -Path C:\tmp\reflect.json -NoNewline
curl.exe -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" --data @C:\tmp\reflect.json http://localhost:7890/api/dev/reflect
```

### Indicator values resolve via assembly scan
`/api/indicator/values` now scans all loaded assemblies to find the NinjaScript indicator host, which fixes the original `NinjaTrader.Custom` AssemblyLoadContext mismatch. If the indicator type is still unavailable, it must be hosted on an active chart or strategy so the assembly is loaded into the AppDomain.

## Known Issues & Fixes

### Compile endpoint crash (FIXED)
`POST /api/compile` was crashing because compilation ran on the HTTP listener thread instead of the WPF UI Dispatcher. Wrapped in `Dispatcher.Invoke`. The MCP tool's `/api/compile/result` polling handles the brief connection reset.

### Indicator values endpoint (FIXED)
`Type.GetType("..., NinjaTrader.Custom")` failed because `NinjaTrader.Custom` lives in a separate `AssemblyLoadContext`. Replaced with an `AppDomain.CurrentDomain.GetAssemblies()` scan, using the same pattern as strategy type resolution.

### Dev/reflect metadata tokens (FIXED)
Json.NET strips `$ref`/`$result` as metadata tokens by default. Fixed by parsing `/api/dev/reflect` payloads with `MetadataPropertyHandling.Ignore` and resolving placeholders by scanning `JObject.Properties()`.

### Chart discovery (FIXED)
`FindChartControl()` previously missed charts in secondary tabs or windows because it used `Application.Current.Windows` and a stale `MainTabControl` field. Rewritten to scan `Globals.AllWindows`, read the private `tabControl` on each `Chart` window, and fall back to a visual-tree walk to locate `ChartControl` instances.

### Chart open / strategy deploy are best-effort
NinjaTrader 8 does not expose a public API to create a chart window or attach a strategy from an AddOn. `/api/chart/open` validates the instrument and focuses the Control Center; `/api/strategy/deploy` attaches to an already-open chart. Use the Control Center shortcut `Ctrl+Shift+N` to open charts.

### Chart draw endpoint (PENDING)
`/api/chart/draw` fails because it searches for `NinjaTrader.Gui.Chart.HorizontalLine`. The correct namespace is `NinjaTrader.NinjaScript.DrawingTools.HorizontalLine` (also `Ray`, `Rectangle`, etc.). Fix also needs `ChartAnchor` construction for each drawing tool.

### Chart capture endpoint (PENDING)
`/api/chart/capture` returns a transparent PNG because the AddOn renders the chart `Window` instead of the `ChartControl` directly. It also still references the removed `MainTabControl` field. Fix in progress.

### Stale Roslyn cache
If `nt_compile` reports errors referencing line numbers beyond the file length or already-fixed code, restart NT8 to clear the Roslyn cache.

## Requirements

- **Node.js 18+** (uses only built-in modules — zero npm dependencies)
- **NinjaTrader 8** (any license: free, trial, or lifetime)
- **Windows** (NinjaTrader only runs on Windows)

## Repository

This wrapper (`mcp/`) lives in the [`nt8-mcp-bridge`](https://github.com/vinay-veerappa/nt8-mcp-bridge)
repo alongside the C# addon it talks to (`addons/McpBridgeAddOn.cs`). The wrapper and
the addon are two halves of one contract: the wrapper advertises MCP tool schemas, the
addon decides what it accepts. Keeping them in one repo makes the contract pinnable —
see `mcp/tests/tool-schema.test.js`'s P1-72 test, which reads the addon source directly
to verify the wrapper's enum matches the addon's `knownActions` whitelist.

The JS tests (`node --test` from `mcp/`) run in CI alongside the C# harness
(`dotnet run --project tests/BridgeTests.csproj`) and the mutation batteries.

## License

MIT — do what you want, no strings attached. See [LICENSE](LICENSE).

## Credits

- **Phase 1** (accounts, trading, quotes, bars, instrument search) — original work by [Igor](https://github.com/Wendigooor) and his AI agent Hermes.
- **Phase 2+** (strategy authoring, in-process compile, Strategy Analyzer backtesting, live deployment, risk/copier/orchestration, and v1.5.0 expansion) — extended by the tvDownloadOHLC project.
- The wrapper was folded into `nt8-mcp-bridge` on 2026-08-14 from its fork of `hoquet98/ninjatrader-mcp`, preserving the full history including the P1-91 and P1-72 contract-drift fixes.
