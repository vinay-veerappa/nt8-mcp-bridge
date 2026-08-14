# NinjaTrader 8 Model Context Protocol (MCP) — Architecture & Feature Specification

> **Version**: 1.5.0  
> **Status**: Production Shipped Architecture & 52-Tool Specification  
> **Target Audience**: Engineering Team, Quant Researchers, & External Reviewers

---

## 1. Overview & Core Architecture

The **NinjaTrader MCP Bridge** provides a standardized [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) stdio interface and a **Hub-and-Spoke Event Streaming Bus**, enabling AI agents (Claude, Hermes, ChatGPT, Cursor, Cline), Python quantitative scripts, and system spokes to control and monitor **NinjaTrader 8** locally.

```
+---------------------------------------------------------------------------------------------------+
|  NinjaTrader 8 Native AddOn (McpBridgeAddOn.cs)                                                   |
|  HTTP REST Server: http://127.0.0.1:7890 (Bearer Auth)                                            |
|  Server-Sent Events (SSE) Producer: GET http://127.0.0.1:7890/api/events/stream                  |
+---------------------------------------------------------------------------------------------------+
       ^                                                            |
       | REST Tool Calls                                            | Real-Time Event Stream
       |                                                            v
+------------------------+                        +-------------------------------------------------+
|  nt-mcp-server.js      |                        |  NinjaTrader Unified Hub (ninjatrader_hub.py)   |
|  (Node.js MCP Relay)   |                        |  FastAPI + WebSockets: http://127.0.0.1:7891    |
|  52 Tool RPC Registry  |                        |  Local Broadcast Bus: ws://127.0.0.1:7891/ws    |
+------------------------+                        +-------------------------------------------------+
       ^                                                            |
       | Stdio JSON-RPC                                             +---> Discord Notifier Spoke
+------------------------+                                          +---> Trading Second Brain Spoke
|  AI Agent / Client     |                                          +---> Quant Research & Dashboards
+------------------------+                                          +---> Risk & Compliance Monitors
```

### Key Architectural Pillars
1. **Zero External Cloud Dependencies**: Runs 100% locally on the user's Windows workspace.
2. **In-Process Hot-Swap Compiler**: Compiles C# NinjaScript source directly via Roslyn without restarting NT8.
3. **Hub-and-Spoke Streaming Engine (`ninjatrader_hub.py`)**: Connects to NT8's SSE stream and broadcasts real-time execution fills, RiskGuard FSM state transitions, and strategy errors to local WebSockets (`ws://127.0.0.1:7891/ws`).
4. **Production Safety Guardrails**: Incorporates local auth, atomic kill-switches, mandatory idempotency, auditability, and SIM auto-gating.

---

## 2. Production Safety, Security & Guardrails

### A. Authentication, Network Isolation & Versioning
* **Localhost Binding**: The HTTP listener (`McpBridgeAddOn.cs`) binds strictly to `127.0.0.1:7890` by default to prevent DNS rebinding attacks and cross-process network sniffing.
* **Bearer Token Authorization**: Requests include an `Authorization: Bearer <NT8_MCP_TOKEN>` header.
* **Semantic Versioning Header**: All HTTP responses include `X-NT8-MCP-Version: 1.5.0` allowing clients to detect capability levels.

### B. Concurrency, Retries & Mandatory Idempotency
* All mutating endpoints (`nt_place_order`, `nt_place_oco_order`, `nt_place_atm_order`, `nt_emergency_flatten`) REQUIRE a client-supplied `idempotencyKey` UUID.
* The C# AddOn maintains a rolling 1-hour cache of processed `idempotencyKey` UUIDs. Duplicate submissions return the cached order response without re-submitting to the market.

### C. Atomic Emergency Kill-Switch (`nt_emergency_flatten`)
* **`nt_emergency_flatten`** executes atomically inside a single C# dispatcher block:
  1. Cancels all active/working orders across all specified accounts.
  2. Issues market orders to immediately flatten all open positions.
  3. Activates a temporary account lockout in RiskGuard to block any new order placement for $N$ minutes.

### E. Environment Variables Reference

| Environment Variable | Components | Default | Description |
| :--- | :--- | :--- | :--- |
| `NT8_MCP_TOKEN` | AddOn & Relay | `""` (disabled) | Bearer authorization token used to authenticate all incoming HTTP requests to port 7890. |
| `NT8_MCP_PREFIX` | `McpBridgeAddOn.cs` | `http://localhost:7890/` | HTTP listener prefix bound by NinjaTrader. Set to `http://+:7890/` for Tailscale/private VPN listener access. |
| `NT8_MCP_DEV` | `McpBridgeAddOn.cs` | `0` (off) | Set to `1` to enable dynamic C# reflection RPC (`/api/dev/reflect`) for inspecting internal NT8 object handles without restarting NT8. |
| `NT8_HOST` | `nt-mcp-server.js` | `127.0.0.1` | Host address of the NinjaTrader 8 McpBridge AddOn HTTP server. |
| `NT8_PORT` | `nt-mcp-server.js` | `7890` | Port number of the NinjaTrader 8 McpBridge AddOn HTTP server. |

---

### D. Auditability & Intervention Logging
* Every mutating endpoint logs the complete JSON request payload, user/agent context, timestamp, and resulting `action_id` to an immutable audit file (`interventions.jsonl`).

### E. Roslyn Hot-Swap Compiler & SIM Gating
* Strategy source created via `nt_create_strategy` and compiled via `nt_compile` is automatically tagged as **Unverified** and restricted to **SIM accounts (`Sim101`)**. Deployment to live accounts requires `confirmLive: true`.

---

## 3. NinjaTrader Unified Hub & Spoke Architecture (`ninjatrader_hub.py`)

The **NinjaTrader Unified Hub** ([scripts/streaming/ninjatrader_hub.py](file:///c:/Users/vinay/tvDownloadOHLC/scripts/streaming/ninjatrader_hub.py)) mirrors the Schwab Hub ([schwab_hub.py](file:///c:/Users/vinay/tvDownloadOHLC/scripts/streaming/schwab_hub.py)) architecture:

* **Hub Endpoint**: `http://127.0.0.1:7891` (FastAPI + uvicorn).
* **SSE Ingestion**: Listens to `http://127.0.0.1:7890/api/events/stream` via `httpx` stream consumer.
* **WebSocket Broadcast Bus**: `ws://127.0.0.1:7891/ws` broadcasts incoming event payloads (`fill`, `order`, `fsm_change`, `error`) to all connected system spokes.
* **REST Endpoints**:
  * `GET /status`: Hub connection status, connected spoke count, and last event timestamp.
  * `GET /events/history?limit=50`: In-memory historical event log replay.

---

## 4. Complete 52-Tool Reference (Phases 1–8 Shipped)

### Phase 1 — Account Management, Live Trading & Quotes
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_health` | `GET /api/health` | Check NT8 AddOn connection, version, auth, and dev mode status. |
| `nt_accounts` | `GET /api/account` | List accounts, cash balances, buying power, and total equity. |
| `nt_positions` | `GET /api/positions` | Query open market positions with live P&L per account. |
| `nt_orders` | `GET /api/orders` | List active/working orders with pagination (`limit`, `offset`). |
| `nt_place_order` | `POST /api/order` | Place Market, Limit, StopMarket, StopLimit, or MIT orders with mandatory `idempotencyKey`. |
| `nt_change_order` | `POST /api/order/change` | Modify active working order parameters (quantity, limit price, stop price). |
| `nt_cancel_order` | `POST /api/order/cancel` | Cancel an order by order ID or group OCO ID. |
| `nt_cancel_all_orders` | `POST /api/orders/cancel-all` | Panic cancellation of all working orders across accounts. |
| `nt_close_position` | `POST /api/position/close` | Flatten a position and cancel working orders for a symbol. |
| `nt_emergency_flatten`| `POST /api/emergency-flatten`| **Atomic Kill-Switch**: Cancel all orders, flatten positions, and engage RiskGuard lockout. |
| `nt_quote` | `GET /api/quote?symbol=` | Real-time quote stream (bid, ask, last, volume, daily high/low) with auto-subscription. |
| `nt_bars` | `GET /api/bars?...` | Fetch historical OHLCV bars with pagination (`limit`, `offset`) capped at 5,000 rows. |
| `nt_search` | `GET /api/search?query=` | Search available instrument master records by symbol or description. |

### Phase 2 — Strategy Development & Backtesting
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_list_strategies` | `GET /api/strategies` | List NinjaScript strategy source `.cs` files in `bin\Custom\Strategies`. |
| `nt_strategy_source` | `GET /api/strategy/source?name=` | Read raw NinjaScript C# source code. |
| `nt_create_strategy` | `POST /api/strategy/create` | Write NinjaScript C# source file into `bin\Custom\Strategies`. |
| `nt_compile` | `POST /api/compile` | Hot-swap compile NinjaScript in-process via Roslyn (**no NT8 restart required**). |
| `nt_backtest` | `POST /api/backtest` | Run Strategy Analyzer backtests over symbol, UTC date range, timeframe, and parameters. |

### Phase 3 — Historical Data Ingest & Archive
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_export_bars` | `POST /api/bars/export` | Download & export raw historical OHLCV bars to CSV (continuous or single contract). |
| `nt_get_export` | `GET /api/export?name=` | Fetch generated export CSV file content over network. |

### Phase 4 — Strategy Deployment & Real-Time Monitoring
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_deploy_strategy` | `POST /api/strategy/deploy` | Deploy & enable compiled strategy onto an open chart (SIM-first safety). |
| `nt_stop_strategy` | `POST /api/strategy/stop` | Disable & remove running strategy from chart, optionally flattening open positions. |
| `nt_strategy_status` | `GET /api/strategy/running` | Query state (Realtime/Historical), instrument, timeframe, and position of running strategies. |
| `nt_set_strategy_param` | `POST /api/strategy/param` | Modify inputs on a RUNNING strategy live without restarting. |

### Phase 5 — Real-Time Streaming & Observability
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_subscribe` | `GET /api/events/stream` | Subscribe to NinjaTrader Hub (`ninjatrader_hub.py`) or McpBridge SSE stream. |
| `nt_capture_chart` | `GET /api/chart/capture` | Capture active WPF chart window as a base64 PNG screenshot. |
| `nt_chart_snapshot` | `POST /api/chart/snapshot` | High-res PNG chart screenshot with overlay markers, price lines, indicators, and time range. |
| `nt_trade_chart` | `POST /api/chart/trade` | Visual Feedback Loop: Auto-capture execution fill chart with trade marker overlays, returning image ID & base64. |
| `nt_open_chart` | `POST /api/chart/open` | Programmatically open a chart window/tab for a symbol and period. |
| `nt_get_logs` | `GET /api/logs` | Tail Output tab logs, Strategy Analyzer output, or `interventions.jsonl`. |
| `nt_fill_events` | `GET /api/events/fills` | Query execution fill history with pagination (`limit`, `offset`). |
| `nt_inspect_strategy` | `GET /api/strategy/inspect` | Reflect over compiled strategy assemblies to extract input parameters and types. |
| `nt_indicator_values`| `GET /api/indicator/values`| Retrieve calculated historical or live indicator values (SMA, EMA, VWAP, ATR). |


### Phase 6 — Quantitative Research & Risk Controls
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_place_oco_order` | `POST /api/order/oco` | Place atomic paired OCO (One-Cancels-Other) limit/stop orders. |
| `nt_place_atm_order` | `POST /api/order/atm` | Place a server-side ATM bracket (entry + stop + target OCO) via `DynamicAtmManager.cs`. Supports 8 strategies: `FixedTicks`, `AtrAdaptive`, `SwingPoint`, `DrawdownShield`, `ScaledRunner`, `VolatilityScaled`, `SessionAdaptive`, `KellyOptimal`. Auto-selects per-instrument strategy (13 profiles) if `strategyName` omitted. DrawdownShield & ScaledRunner register for 5s breakeven/trailing monitoring. Response is camelCase: `{status, bracketId, ocoId, stopPrice, targetPrice, strategyName, entryOrderId, stopOrderId, targetOrderId}`. Master-instrument symbols rejected (use full futures contract e.g. `NQ 09-26`). |
| `nt_atm_bracket_status` | `GET /api/order/atm/status` | Query an active ATM bracket by `bracketId` (returns `{bracketId, symbol, account, isLong, entryPrice, strategy, currentStop, currentTarget, breakevenTriggered, partialProfitTaken, isComplete, ageSeconds}`) or omit `bracketId` to list all active brackets (`{count, brackets[]}`). |
| `nt_riskguard_state` | `GET /api/riskguard/fsm-state` | Read live RiskGuard FSM state, peak equity drawdown, and daily loss limit snapshots. |
| `nt_copier_config` | `POST /api/copier/config` | Configure `TradeCopierEngine.cs` ratios, Micro/Mini lot scaling, and account quarantine. |
| `nt_prop_limits` | `POST /api/prop/limits` | Configure `PropFirmProtectionSuite.cs` Target Lock, Giveback Cap, and News Shield. |
| `nt_extract_trades` | `GET /api/trades/extract` | Extract execution records enriched with MAE/MFE, macro window tags (`macro_1050`), and latency. |
| `nt_monte_carlo` | `POST /api/trades/monte-carlo` | Run Block Bootstrap Monte Carlo simulations returning Risk of Ruin %, CVaR @ 95%/99%, and MaxDD bands. |
| `nt_draw_level` | `POST /api/chart/draw` | Plot S/R levels, Midnight Open, HOD/LOD, or FVG boxes onto charts via `Draw.*` methods. |
| `nt_script_execute` | `POST /api/script/execute` | Execute sandboxed C# utility snippets or pre-approved helper functions. |

### Phase 7 — Advanced Research & Automation Pipeline
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_portfolio_backtest`| `POST /api/backtest/portfolio`| Simultaneous multi-symbol backtests with correlation matrix calculation. |
| `nt_synthetic_data` | `POST /api/data/synthetic` | Generate stress scenario datasets (2020 COVID shock, 2008 GFC, volatility scaling). |
| `nt_signal_backtest` | `POST /api/backtest/signal` | Lightweight "what-if" testing of entry/exit signal rules without full NinjaScript overhead. |
| `nt_schedule` | `POST /api/schedule/task` | Register time-based or event-based scheduled tasks inside NinjaTrader. |
| `nt_trade_journal` | `POST /api/trades/journal` | Full CRUD operations on local trade journal repository with macro window auto-tagging. |
| `nt_alert` | `POST /api/alert/create` | Create persistent price, indicator, or strategy alerts with local notifications. |

### Phase 8 — Multi-Account & Compliance Suite
| Tool Name | Endpoint | Description |
| :--- | :--- | :--- |
| `nt_riskguard_config` | `POST /api/riskguard/config` | Dynamic configuration of trailing drawdown limits and volatility position caps. |
| `nt_compliance_report`| `GET /api/compliance/report` | One-click generation of prop firm / broker compliance reports. |
| `nt_multi_account_orchestrator`| `POST /api/orchestrator/multi-account`| Coordinated order routing and hedging across multiple accounts. |

---

## 11. Test Harness

The bridge is covered by three test suites. The C# harness and JS tests run in CI on every push; the Python integration tests run live against the NT8 box.

### C# Test Harness — `nt8-mcp-bridge/tests/BridgeTests.csproj`

Source-text and executed tests covering P2-38 (no name-based sim classification), P1-90 (account resolver refuses rather than guessing), P1-97 (order action resolution from position), P0-104 (emergency flatten cancel set), P1-106 (lockout gate admits exits), P3-34 (copier mode), and the vendored-core presence check. Extracted logic (BridgeAccountResolver, BridgeLockoutGate, BridgeFlattenPlan, BridgeOrderAction, CopierEnforcementView) is executed; McpBridgeAddOn.cs is source-asserted (P2-27).

```powershell
dotnet run --project tests/BridgeTests.csproj    # 133 tests, 0 failures
```

### JS Schema Tests — `nt8-mcp-bridge/mcp/tests/`

Pins the MCP wrapper's tool schemas against the addon's actual behaviour. P1-91 (no schema defaults that supply arguments the caller never sent), P1-72 (the copier action enum names only actions the addon accepts — reads `addons/McpBridgeAddOn.cs` directly), P3-34 (copier mode), and the request builder for `nt_copier_config` (P1-73 merge semantics).

```bash
cd mcp && node --test    # 43 tests, 0 dependencies, node:test
```

### Core Test Suite — `nt8-riskguard/tests/RiskGuardTests.csproj`

1436 tests covering RiskGuard FSM/lockout/sizing, TradeCopier hedging/reconciliation, and the DynamicAtmManager bracket engine. See [nt8-riskguard](https://github.com/vinay-veerappa/nt8-riskguard).

### Python Integration Tests — `tests/test_risk_guard_integration.py`

Live HTTP tests against the running bridge (requires NT8 + McpBridge AddOn). Auth: `Authorization: Bearer <NT8_MCP_TOKEN>` (token from env or `.mcp.json`).

```powershell
.\.venv\Scripts\python.exe -m pytest tests\test_risk_guard_integration.py -k atm -v   # ATM subset (10 tests, ~1s)
.\.venv\Scripts\python.exe -m pytest tests\test_risk_guard_integration.py -v          # full suite (live orders)
```

Covers RiskGuard lockout/sizing/cooldown, Firm Mirror persistence, and the **ATM bracket API** (10 tests: auth, FixedTicks long/short response contract, idempotency dedupe, unknown-strategy/master-instrument/missing-field rejections, bracket status listing + unknown-id, order visibility). ATM tests use a lightweight cleanup path (`wait=False`) to avoid the 7.5s settle-wait on a closed market.

---
*Document maintained by Quantitative Trading Architecture Team.*
