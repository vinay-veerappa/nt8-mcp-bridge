# MCP Tool Gap Analysis

This document compares the current NinjaTrader 8 MCP bridge (56 shipped tools in `mcp/lib/tools.js`) against the general-purpose platform bridge reference list (88 tools).

## Summary
The current MCP is a specialized implementation focused on **risk management, proprietary firm compliance, and automated trading workflows**. It contains many domain-specific tools that the reference list does **not** include (RiskGuard, Trade Copier, Monte Carlo, ATM brackets, synthetic stress data). Conversely, it omits most general-purpose **platform administration, window management, workspace control, and deep UI navigation** tools that assume direct NT8 desktop automation.

---

## 1. Current MCP Tool Inventory (Source of Truth)

The canonical list is exported from `mcp/lib/tools.js` and dispatched in `mcp/nt-mcp-server.js`:

| # | Tool | Category |
|---|------|----------|
| 1 | `nt_health` | Health |
| 2 | `nt_accounts` | Account |
| 3 | `nt_positions` | Account |
| 4 | `nt_orders` | Order |
| 5 | `nt_place_order` | Order |
| 6 | `nt_place_oco_order` | Order |
| 7 | `nt_place_atm_order` | Order |
| 8 | `nt_atm_bracket_status` | Order |
| 9 | `nt_change_order` | Order |
| 10 | `nt_cancel_order` | Order |
| 11 | `nt_cancel_all_orders` | Order |
| 12 | `nt_close_position` | Order |
| 13 | `nt_emergency_flatten` | Safety |
| 14 | `nt_quote` | Market Data |
| 15 | `nt_bars` | Market Data |
| 16 | `nt_search` | Instrument |
| 17 | `nt_export_bars` | Data Export |
| 18 | `nt_get_export` | Data Export |
| 19 | `nt_capture_chart` | Chart |
| 20 | `nt_chart_snapshot` | Chart |
| 21 | `nt_trade_chart` | Chart |
| 22 | `nt_open_chart` | Chart |
| 23 | `nt_get_logs` | Observability |
| 24 | `nt_fill_events` | Observability |
| 25 | `nt_inspect_strategy` | Strategy |
| 26 | `nt_lockout` | RiskGuard |
| 27 | `nt_connection` | Connection |
| 28 | `nt_riskguard_state` | RiskGuard |
| 29 | `nt_riskguard_inventory` | RiskGuard |
| 30 | `nt_copier_snapshot` | Trade Copier |
| 31 | `nt_copier_config` | Trade Copier |
| 32 | `nt_prop_limits` | Prop Firm |
| 33 | `nt_extract_trades` | Trade Journal |
| 34 | `nt_monte_carlo` | Research |
| 35 | `nt_draw_level` | Chart Drawing |
| 36 | `nt_indicator_values` | Indicator |
| 37 | `nt_script_execute` | Scripting (stub — refuses all calls) |
| 38 | `nt_subscribe` | Streaming |
| 39 | `nt_portfolio_backtest` | Research |
| 40 | `nt_synthetic_data` | Research |
| 41 | `nt_signal_backtest` | Research |
| 42 | `nt_schedule` | Automation |
| 43 | `nt_trade_journal` | Trade Journal |
| 44 | `nt_alert` | Alert |
| 45 | `nt_riskguard_config` | RiskGuard |
| 46 | `nt_compliance_report` | Compliance |
| 47 | `nt_multi_account_orchestrator` | Order Routing |
| 48 | `nt_list_strategies` | Strategy File |
| 49 | `nt_strategy_source` | Strategy File |
| 50 | `nt_create_strategy` | Strategy File |
| 51 | `nt_compile` | Strategy Build |
| 52 | `nt_backtest` | Research |
| 53 | `nt_strategy_status` | Running Strategy |
| 54 | `nt_deploy_strategy` | Running Strategy |
| 55 | `nt_stop_strategy` | Running Strategy |
| 56 | `nt_set_strategy_param` | Running Strategy |

---

## 2. Existing Tool Equivalents (Mapped)

These reference-list tools are covered by a current MCP tool, sometimes with a different name or merged into an action-based endpoint.

| Reference Tool | Current MCP Tool | Coverage Notes |
|----------------|------------------|----------------|
| `health_check` | `nt_health` | Connection, version, auth, feed status. |
| `compile_ninjascript` | `nt_compile` | In-process Roslyn hot-swap compile. |
| `list_connections` / `connection_info` / `list_configured_connections` / `connect_data_feed` / `disconnect_data_feed` | `nt_connection` | **Consolidated** into one tool via `action` parameter (`status`/`connect`/`disconnect`). |
| `list_strategies` | `nt_list_strategies` | Lists `.cs` source files. |
| `read_strategy` | `nt_strategy_source` | Reads raw NinjaScript C# source. |
| `save_strategy` | `nt_create_strategy` | Writes NinjaScript `.cs` file. |
| `market_snapshot` | `nt_quote` | Real-time quote with auto-subscription. |
| `instrument_info` / `search_instruments` | `nt_search` | Instrument master search by symbol/name. |
| `get_bars` | `nt_bars` | Historical OHLCV bars. |
| `list_accounts` / `account_details` | `nt_accounts` / `nt_riskguard_state` | Account balances and extended risk state. |
| `get_positions` / `get_positions_extended` | `nt_positions` | Open positions with live P&L. |
| `get_executions` | `nt_fill_events` | Execution/fill history. |
| `cancel_all_orders` | `nt_cancel_all_orders` | Cancels all working orders. |
| `flatten_all` | `nt_emergency_flatten` / `nt_close_position` | Atomic cancel+flatten or per-symbol flatten. |
| `submit_order` | `nt_place_order` | Market/Limit/StopMarket/StopLimit/MIT. |
| `list_orders` / `get_order_details` | `nt_orders` | Lists active/working orders. |
| `modify_order` | `nt_change_order` | Modify quantity, limit/stop price. |
| `cancel_order` | `nt_cancel_order` | Cancel by order ID or OCO group. |
| `flatten_position` | `nt_close_position` | Flatten a symbol's position. |
| `list_running_strategies` / `chart_strategies` | `nt_strategy_status` | Lists enabled strategies on charts. |
| `strategy_details` | `nt_inspect_strategy` | Reflects strategy inputs/properties. |
| `get_strategy_performance` / `strategy_analysis` | `nt_backtest` / `nt_portfolio_backtest` | Strategy Analyzer backtests. |
| `get_strategy_trades` / `export_trades` | `nt_extract_trades` | Trade extraction with MAE/MFE/latency. |
| `strategy_orders` | `nt_orders` | Orders generated by running strategies. |
| `set_strategy_state` / `close_strategy` | `nt_set_strategy_param` / `nt_stop_strategy` | Live param changes and strategy shutdown. |
| `open_chart` | `nt_open_chart` | Best-effort chart open (NT8 public API limitation). |
| `chart_snapshot` | `nt_chart_snapshot` | High-res chart screenshot. |
| `chart_screenshot` | `nt_capture_chart` | WPF chart window capture. |
| `indicator_values` | `nt_indicator_values` | Historical/live indicator values. |
| `chart_bars` | `nt_bars` | Same historical bars endpoint. |
| `create_drawing` | `nt_draw_level` | **Partial** — only price-level primitives (horizontal line, rectangle, ray, vertical line, line). No freeform drawings. |
| `read_export` | `nt_get_export` | Retrieve generated CSV export. |
| `export_bars` | `nt_export_bars` | Export historical OHLCV to CSV. |

---

## 3. Missing Tool Subset

These reference-list tools have **no equivalent** in the current MCP.

### 3.1 Platform & Window Management
- **Discovery**: `list_api_endpoints`
- **System**: `platform_info`
- **UI Control**: `list_windows`, `close_window`, `write_output`, `write_log`
- **Workspaces**: `list_workspaces`, `open_workspace`, `set_active_workspace`, `save_workspace_as`, `close_workspace`

### 3.2 Indicator & File Management
- **Indicators**: `list_indicators`, `read_indicator`, `save_indicator`, `delete_indicator`
- **Strategies**: `delete_strategy`
- **Catalog**: `list_indicator_catalog`, `list_strategy_catalog`

### 3.3 Market Data & Instruments
- **Data**: `market_depth`
- **Instruments**: `list_trading_hours`
- **Accounts**: `reset_sim_account`

### 3.4 Chart & Drawing Control
- **Chart Management**: `list_charts`, `open_chart_template`, `close_chart`, `list_chart_templates`, `save_chart_template`, `reload_chart`
- **Chart Navigation**: `chart_time_info`, `chart_zoom_in`, `chart_zoom_out`, `chart_scroll_to`
- **Indicator Control on Chart**: `chart_indicators`, `add_chart_indicator`, `remove_chart_indicator`
- **Drawings**: `list_drawing_types`, `list_drawing_helpers`, `list_drawings`, `remove_drawing`

### 3.5 CSV Export Management
- **File Ops**: `list_exports`, `delete_export`
- **Specialized Exports**: `export_chart_bars`, `export_chart_indicator`, `export_performance`

---

## 4. Partially Covered Functionality

| Reference Capability | Current Tool | Gap |
|----------------------|--------------|-----|
| `create_drawing` | `nt_draw_level` | Only supports price-level primitives (HorizontalLine, Ray, VerticalLine, Rectangle, Line). No full drawing object suite, no Fibonacci/extensions, no text labels, no trend channels. |
| `open_chart` | `nt_open_chart` | NT8 has no public AddOn API to create chart windows; the endpoint validates the instrument and focuses Control Center, requiring the user to open the chart manually. |
| `chart_snapshot` / `chart_screenshot` | `nt_chart_snapshot` / `nt_capture_chart` | Both work only when a chart window is already visible; no headless chart creation. |
| `get_strategy_performance` | `nt_backtest` | Backtest reports are available, but there is no dedicated strategy-performance summary endpoint for live running strategies. |
| `platform_info` / `list_api_endpoints` | None | No endpoint introspection or capability-discovery endpoint exists beyond the version header. |
| `script_execute` | `nt_script_execute` | Tool exists but is a stub; its own description says it always refuses with `error=NOT_IMPLEMENTED`. |

---

## 5. Tools in Current MCP Not Present in Reference List

These are domain-specific capabilities the reference list does **not** define:

- **RiskGuard**: `nt_riskguard_state`, `nt_riskguard_inventory`, `nt_riskguard_config`
- **Trade Copier**: `nt_copier_config`, `nt_copier_snapshot`
- **Prop Firm / Compliance**: `nt_prop_limits`, `nt_compliance_report`
- **Emergency / Safety**: `nt_emergency_flatten`, `nt_lockout`
- **Advanced Order Types**: `nt_place_atm_order`, `nt_place_oco_order`, `nt_atm_bracket_status`
- **Quant Research**: `nt_monte_carlo`, `nt_synthetic_data`, `nt_signal_backtest`, `nt_portfolio_backtest`
- **Automation**: `nt_schedule`, `nt_alert`, `nt_multi_account_orchestrator`
- **Trade Journaling**: `nt_trade_journal`, `nt_trade_chart`
- **Drawing**: `nt_draw_level` (domain-specific S/R/FVG levels)
- **Streaming**: `nt_subscribe`
- **Scripting**: `nt_script_execute` (stub — refuses all calls)
- **Logs**: `nt_get_logs`

---

## 6. Structural Difference

The reference list uses a **verb-based tool design** (many small tools with one action each). The current MCP uses an **action-based design** for some areas:

- Connections: one tool (`nt_connection`) handles list/connect/disconnect/status.
- RiskGuard: one config tool + one state tool + one inventory tool rather than many discrete risk endpoints.
- Orders: separate tools exist for place/modify/cancel, but `nt_close_position` and `nt_emergency_flatten` absorb "flatten" semantics.

This consolidation reduces tool count but means a client expecting the exact reference-list tool names must use different MCP tool names and parameter shapes.

---

## 7. Brainstorming: Headless Chart-Driven Indicator Test Loop

A useful future workflow would be **Open New Chart → Add Indicator/Strategy → Read Values → Close Chart**, entirely for indicator development and verification. The goal is to investigate what is possible **without WPF/UI automation**.

### Desired Workflow

```text
1. open_chart(symbol, period, periodValue, barsBack?)
2. add_chart_indicator(symbol, indicatorName, inputs)
3. chart_indicators(symbol)            -- verify loaded
4. indicator_values(symbol, indicatorName) -- read output
5. chart_bars(symbol)                    -- verify underlying data
6. close_chart(symbol or chartId)
```

### NT8 API Constraints (Known)

NinjaTrader 8 does **not** expose a public AddOn API to:

- Create chart windows programmatically.
- Attach indicators to charts programmatically.
- Attach strategies to charts programmatically (deploy requires an existing open chart).

The existing `/api/chart/open` endpoint validates the instrument and focuses the Control Center, but cannot truly create a chart headlessly.

### Investigation Questions

To determine what can be done **without UI automation**, the following should be researched in the NT8 AddOn API / NinjaScript documentation:

1. **Chart Enumeration**
   - Can `NinjaTrader.Gui.Chart.ChartControl` instances be enumerated from an AddOn without WPF tree walking?
   - Is there a first-class NT8 service that tracks open charts?

2. **Indicator Instantiation**
   - Can an indicator be instantiated directly from an AddOn via `NinjaTrader.NinjaScript.IndicatorBase` or `AddChartIndicator` equivalent?
   - Can it be hosted inside a custom `Bars` request rather than a chart?

3. **Data Series Hosting**
   - Can a `BarsRequest` be created with a specified period, period value, and bars-back, and fed to an indicator for output calculation?
   - This would bypass the chart entirely.

4. **Strategy as a Test Harness**
   - Can a throwaway strategy host the indicator internally and expose its plots via custom outputs?
   - This is the current fallback path and is fully automatable today.

5. **Chart Cleanup**
   - If a chart window exists, can it be closed via its `Window.Close()` method from the chart's own dispatcher?
   - This may still be UI-thread work, not truly headless.

### Proposed Non-UI Research Outcomes

| Outcome | Description |
|---------|-------------|
| **Path A: Headless BarsRequest Indicator Host** | Build a dedicated endpoint that creates a `BarsRequest`, instantiates an indicator against it, and returns the indicator's output series. No chart required. |
| **Path B: Strategy-Based Test Wrapper** | Improve existing `nt_backtest` / `nt_signal_backtest` to accept an indicator name and return its plotted values alongside trade signals. |
| **Path C: Minimal Chart + Dispatcher** | If chart creation is unavoidable, marshal the minimum WPF calls to the chart dispatcher and accept that this is lightweight UI automation, not fully headless. |
| **Path D: Decline UI Navigation Tools** | Document that `chart_zoom_in`, `chart_zoom_out`, `chart_scroll_to`, and `chart_time_info` are out of scope because they only matter for human viewing. |

### Recommended Next Step

Before implementing any chart indicator tools, produce a small NT8 AddOn proof-of-concept that answers:

> *"Can an indicator be instantiated and its values read using only `BarsRequest` and NinjaScript objects, with no `ChartControl`?"*

If the answer is **yes**, the MCP can expose a clean headless `indicator_test` or `compute_indicator` endpoint.

If the answer is **no**, the recommended path is **Path B**: enhance the existing strategy/backtest tooling to host indicators as a test harness, which is already automatable and avoids the chart UI limitation entirely.

### Research AddOn

A dedicated research AddOn has been created at:

```text
nt8-mcp-bridge/addons/IndicatorTestAddOn.cs
```

It listens on `http://localhost:7892/` (separate from the main MCP bridge on `:7890`) and exposes:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/health` | Confirm the research AddOn is loaded. |
| `GET /api/bars?symbol=...` | Fetch bars via `BarsRequest`. |
| `GET /api/indicator/reflect?name=...` | Find the indicator type in loaded assemblies and inspect its methods/properties. |
| `GET /api/indicator/try-host?symbol=...&name=...` | Attempt to instantiate and drive a NinjaScript indicator headlessly. |
| `GET /api/indicator/builtin?symbol=...&indicatorName=SMA|EMA|RSI|ATR` | Compute built-in indicator values directly from bars. |

Use this AddOn to answer the core research question before deciding whether to add chart-indicator tools to the main MCP.

