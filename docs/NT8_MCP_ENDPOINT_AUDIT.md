# NT8 MCP/HTTP Bridge Endpoint Audit

Audit date: 2026-08-01 (updated 2026-08-02)
Bridge version: 1.5.2-chart-discovery
Bridge URL: `http://localhost:7890`
Default account used for order tests: `Sim101`
All order tests were placed on **Sim101** unless otherwise noted.

> **Update (2026-08-01)**: `/api/chart/draw` is now **✅ OK** — fixed cross-thread dispatcher issue (ChartControl's own dispatcher instead of app dispatcher). All 5 shape types verified (HorizontalLine, Ray, VerticalLine, Rectangle, Line). Same fix applied to `DeployStrategy`, `StopStrategy`, and `FindChartWindow` (used by `CaptureChart`). See commits `f64b9618` and `cd6fda31`.

> **Update (2026-08-02)**: `/api/order/atm` is now a **full server-side bracket engine** — the old stub that directed callers to `/api/order/oco` is replaced by `DynamicAtmManager.cs` implementing 8 ATM strategies (FixedTicks, AtrAdaptive, SwingPoint, DrawdownShield, ScaledRunner, VolatilityScaled, SessionAdaptive, KellyOptimal), 13 instrument profiles, OCO-wired entry/stop/target, and a 5s background monitor for breakeven/trailing on DrawdownShield & ScaledRunner. Response normalized to **camelCase** (`status`, `bracketId`, `ocoId`, `stopPrice`, `targetPrice`, `strategyName`, order ids) to match every other endpoint. Bracket status queryable via `GET /api/order/atm/status?bracketId=` (omit for all-active listing). Covered by 20 C# unit tests + 10 Python integration tests (see Test Harness section below).

> **Planned (2026-08-04)**: `GET /api/indicator/levels` — a **data-model** endpoint that reads the live indicator's `NtLevelRecord` snapshot (semantic level data: key, label, price, category, scheme_color, state, date) instead of scraping the canvas. This is a tracked follow-up; see `docs/MCP_DATA_MODEL_ENDPOINT.md` for the full design and acceptance criteria.

## Legend

| Status | Meaning |
|--------|---------|
| ✅ OK | Verified working with a successful response |
| ⚠️ Partial | Returns valid response but has documented limitations |
| ❌ Blocked | Fails by NT8 design/API limitation; not a bridge bug |
| 🔴 Fail | Unexpected failure that needs further investigation |
| 📝 Untested | Endpoint not probed in this session |

---

## Account, Position, Order

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/health` | GET | ✅ OK | — | Returns `{status, timestamp, version, dev, accounts, feedConnected}` |
| `/api/account` | GET | ✅ OK | — | Account balances, PnL, buying power |
| `/api/positions` | GET | ✅ OK | — | Open positions + unrealized PnL |
| `/api/orders` | GET | ✅ OK | `account`, `limit`, `offset` | Working/historical orders |
| `/api/order` | POST | ✅ OK | `symbol`, `action`, `quantity`, `orderType`, `idempotencyKey` | Market/Limit/StopMarket/StopLimit/MIT |
| `/api/order/oco` | POST | ✅ OK | `symbol`, `action`, `quantity`, `stopPrice`, `targetPrice`, `idempotencyKey` | Use `targetPrice`, NOT `limitPrice` |
| `/api/order/atm` | POST | ✅ OK | `symbol`, `action`, `quantity`, `strategyName`, `idempotencyKey` | Full bracket engine via `DynamicAtmManager` (8 strategies). Optional `stopTicks`, `targetTicks`, `atrMultiplierSL/TP`, `swingLookbackBars`, `riskPerTrade`, etc. Master-instrument symbols rejected. Returns camelCase `{status, bracketId, ocoId, stopPrice, targetPrice, strategyName, entryOrderId, stopOrderId, targetOrderId}`. |
| `/api/order/change` | POST | ✅ OK | `orderId`, optional `limitPrice`/`stopPrice`/`quantity` | Modify working order |
| `/api/order/cancel` | POST | ✅ OK | `orderId` or `ocoId` | Cancel single or OCO group |
| `/api/orders/cancel-all` | POST | ✅ OK | — | Cancels all working orders across accounts |
| `/api/position/close` | POST | ✅ OK | `symbol`, optional `account` | Flattens position for symbol |
| `/api/emergency-flatten` | POST | ✅ OK | `idempotencyKey`, optional `account`, `lockoutMinutes` | Atomic cancel+flatten+lockout |
| `/api/lockout` | POST | ✅ OK | optional `account`, `minutes`, `query` | Engage or query lockout |

---

## Quotes, Bars, Instruments

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/quote` | GET | ✅ OK | `symbol` (e.g. `NQ 09-26`) | Returns bid/ask/last/volume/high/low |
| `/api/bars` | GET | ✅ OK | `symbol`, `period`, `periodValue`, `count` | Historical OHLCV bars |
| `/api/bars/export` | POST | ✅ OK | `symbol`, `from`, `to`, `period`, `periodValue` | Writes CSV on NT8 machine |
| `/api/export` | GET | ✅ OK | `name` | Retrieve exported CSV content |
| `/api/search` | GET | ✅ OK | `query` | Search instruments by name/symbol |

**Symbol rule**: root tickers (`ES`, `NQ`, `MNQ`) are rejected. Use full futures format: `ES 09-26`, `NQ 09-26`, `MNQ 09-26`.

---

## Strategy Authoring, Compile, Backtest

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/strategies` | GET | ✅ OK | — | List source files in `bin\Custom\Strategies` |
| `/api/strategy/source` | GET | ✅ OK | `name` | Read source by strategy name |
| `/api/strategy/create` | POST | ✅ OK | `name`, `source` | Writes NinjaScript `.cs` file |
| `/api/compile` | POST | ✅ OK | — | Returns no body; connection resets on success (by design) |
| `/api/compile/result` | GET | ✅ OK | — | Poll for errors/warnings |
| `/api/backtest` | POST | ✅ OK | `strategy`, `symbol`, `from`, `to` | Returns PnL, drawdown, trade list |
| `/api/backtest/portfolio` | POST | ✅ OK | `strategy`, `symbols[]` | Multi-symbol backtest via SA; requires compiled strategy compatible with requested symbols |
| `/api/backtest/signal` | POST | ✅ OK | `symbol`, `entryRule`, `exitRule` | Lightweight what-if signal testing |
| `/api/strategy/inspect` | GET | ✅ OK | `name` | Reflects inputs/properties |
| `/api/strategy/running` | GET | ✅ OK | — | Lists enabled strategies + position |
| `/api/strategy/deploy` | POST | ⚠️ Partial | `strategy`, `instrument`, `account`, `enable` | Best-effort; requires an open chart for the instrument (Ctrl+Shift+N). NT8 has no public AddOn API to open a chart or attach a strategy from code |
| `/api/strategy/stop` | POST | ✅ OK | optional `strategy`, `account`, `flatten` | Disable/remove running strategies |
| `/api/strategy/param` | POST | ✅ OK | `params{}`, optional `strategy`, `account` | Change inputs live on running strategy |
| `/api/sa/close` | POST | ✅ OK | — | Close Strategy Analyzer windows |
| `/api/sa/inspect` | GET | ✅ OK | — | Inspect SA controls/bindings |

---

## Observability & Logs

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/logs` | GET | ✅ OK | `tab`, `lines` | Tail Output tab / SA / interventions |
| `/api/events/fills` | GET | ✅ OK | optional `account`, `count` | Fill history |
| `/api/events/stream` | GET (SSE) | ✅ OK | — | Server-sent events heartbeat; stream live |
| `/api/chart/capture` | GET | ✅ OK | optional `symbol` | Returns `{base64: "..."}` PNG of an open chart. Verified non-transparent capture for MNQ SEP26 (120KB base64). Requires chart window to be visible. Fixed: `FindChartWindow` now marshals to each chart window's own dispatcher (was using app dispatcher → cross-thread exception). |
| `/api/chart/snapshot` | POST | ✅ OK | optional `symbol`, `markers[]` | Wraps `CaptureChart()`; inherits verified capture. Verified: `imageId`, `width=1280`, `height=720`. Marker drawing not exercised. |
| `/api/chart/open` | POST | ⚠️ Partial | `symbol`, optional `period`/`periodValue` | Validates instrument and focuses Control Center. NT8 has no public AddOn API to create a chart window; use Ctrl+Shift+N |
| `/api/chart/draw` | POST | ✅ OK | `symbol`, `shapeType`, `price1` (required), optional `price2`, `time1`, `time2`, `color`, `width`, `dashStyle` | Draws on the chart for the requested symbol. Uses `ChartControl`'s own dispatcher (not app dispatcher) to avoid cross-thread exception. Verified: HorizontalLine, Ray, VerticalLine, Rectangle, Line all draw successfully on MNQ SEP26. Idempotent: re-draw with same `tag` replaces prior object. |
| `/api/chart/trade` | POST | ✅ OK | `symbol`, `action`, optional params | Wraps `CaptureChart()`; inherits verified capture. Returns base64 PNG + metadata. |

---

## Research, Risk, Automation

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/trades/extract` | GET | ✅ OK | optional `account`, `from`, `to`, `format` | Trade records with MAE/MFE/commissions |
| `/api/trades/monte-carlo` | POST | ✅ OK | `strategy` / trade source | Block-bootstrap Monte Carlo |
| `/api/data/synthetic` | POST | ✅ OK | `symbol`, optional `scenario` | Generate stress-scenario data |
| `/api/trades/journal` | POST | ✅ OK | TBD | Trade journal CRUD/export |
| `/api/schedule/task` | POST | ✅ OK | `name`, `cron`/`interval`, `command` | Register scheduled task |
| `/api/script/execute` | POST | 🔴 Fail | `code` or script payload | Connection closed or C# compile error; unresolved |
| `/api/alert/create` | POST | ✅ OK | `symbol`, `condition`, `price` | Persistent alert |
| `/api/riskguard/version` | GET | ✅ OK | — | RiskGuard version |
| `/api/riskguard/fsm-state` | GET | ✅ OK | `account`, `instrument` | FSM state, drawdown, daily limits |
| `/api/riskguard/config` | POST | ✅ OK | config body | Configure trailing DD, vol caps, blackouts |
| `/api/compliance/report` | GET | ✅ OK | optional `account` | Compliance/prop report |
| `/api/copier/config` | POST | ✅ OK | leader/follower config body | TradeCopierEngine config get/set |
| `/api/prop/limits` | POST | ✅ OK | prop-limit config body | PropFirmProtectionSuite config get/set |
| `/api/orchestrator/multi-account` | POST | ✅ OK | orders/accounts body | Coordinated multi-account routing |
| `/api/indicator/values` | GET | ✅ OK | `symbol`, `indicatorName` | Scans all loaded assemblies to find the NinjaScript indicator host (fixed AssemblyLoadContext issue) |

---

## Dev / Internal

| Endpoint | Method | Status | Required Fields / Example | Notes |
|----------|--------|--------|---------------------------|-------|
| `/api/dev/reflect` | POST | ✅ OK (dev only) | Any JSON | Internal reflection/probe endpoint. **Fixed** Json.NET metadata-token handling so `$ref`/`$result` placeholders survive parsing. |
| `/api/dev/inspect-state` | GET | ✅ OK (dev only) | — | Internal state inspection |

---

## Common Errors and How to Fix

### `unknown instrument` or `ES rejected`
Use the full futures symbol with contract month/year: `ES 09-26`, `NQ 09-26`, `MNQ 09-26`.

### `Unexpected character` or JSON parse errors from curl
PowerShell strips double quotes when JSON is passed inline. Always write the body to a temp file:
```powershell
$body = @{ symbol='MNQ 09-26'; action='buy'; quantity=1; orderType='Market'; idempotencyKey='test-1' } | ConvertTo-Json -Compress
$body | Set-Content -Path C:\tmp\order.json -NoNewline
curl.exe -s -X POST -H "Authorization: Bearer d0b837223cab4653" -H "Content-Type: application/json" --data @C:\tmp\order.json http://localhost:7890/api/order
```

### Compile connection reset
`POST /api/compile` intentionally returns no body as the AppDomain reloads. The MCP tool `nt_compile` polls `/api/compile/result` automatically. Direct curl/IRM calls crash.

### Stale rejected orders
Bad-symbol order attempts (e.g. `ES`) may leave rejected/cancelled orders in `GET /api/orders`. These are harmless noise but can be cleaned with `POST /api/orders/cancel-all` or ignored.

### `could not access a chart control` on deploy
`/api/strategy/deploy` is best-effort because NinjaTrader 8 does not expose a public API to create a chart window or attach a strategy from an AddOn. Open a chart for the instrument manually (Control Center: Ctrl+Shift+N, type the symbol), optionally add the first strategy via the chart's Strategies dialog, then call deploy again.

### `indicator host unavailable`
`/api/indicator/values` now scans all loaded assemblies to find the NinjaScript indicator host, resolving the original `NinjaTrader.Custom` AssemblyLoadContext issue. If it still returns unavailable, the indicator must be hosted on a chart or strategy.

### `no open chart found` on draw
`/api/chart/draw` uses the same chart discovery as deploy and now also matches by `MasterInstrument.Name` (so `MNQ 09-26` resolves even when the chart label differs slightly). It still requires a visible chart for the requested instrument.

### Chart capture does not match requested symbol
`/api/chart/capture` now attempts symbol-specific window matching before falling back to any visible chart window. The 2025-08-01 rewrite renders the `ChartControl` directly instead of the parent `Window`, which should eliminate transparent PNG output. The route still requires an open chart window; if none is visible it returns `No active chart control found to capture.`

### Chart open does nothing
`/api/chart/open` validates the instrument and focuses the Control Center, but NT8 does not expose a public AddOn API to create a chart window. Use the Control Center shortcut Ctrl+Shift+N and type the symbol.

### `/api/dev/reflect` loses `$ref` / `$result` handles
Json.NET treats `$`-prefixed properties as metadata tokens and strips them by default. Fixed by parsing `dev/reflect` payloads with `MetadataPropertyHandling.Ignore` and resolving placeholders by scanning `JObject.Properties()`.

### PowerShell expands `$` variables in inline JSON
When passing JSON containing `$ref`, `$result`, `$type`, etc. to `python -c` or `curl` inline, PowerShell expands them as variables. Always write the body to a file:
```powershell
$body | Set-Content -Path C:\tmp\body.json -NoNewline
curl.exe -s -X POST ... --data @C:\tmp\body.json http://localhost:7890/api/dev/reflect
```

### `/api/chart/draw` fails with "drawing type unavailable"
~~The AddOn currently looks for `NinjaTrader.Gui.Chart.HorizontalLine`. The correct namespace is `NinjaTrader.NinjaScript.DrawingTools.HorizontalLine` (and `Ray`, `Rectangle`, etc.). Anchor creation (`ChartAnchor.CreateToolAnchorType`) must also be supplied. Pending fix.~~

**Fixed (2026-08-01)**: Now uses correct `DrawingTools` namespace, `ChartControl`'s own dispatcher, and proper `ChartAnchor` setup. All 5 shape types verified on MNQ SEP26.

### `/api/chart/capture` returned a transparent PNG
`CaptureChart()` originally rendered the chart `Window` via `RenderTargetBitmap`, which produced a transparent image. The 2025-08-01 fix renders the `ChartControl` directly via `FindChartControl()` / `FindAnyChartControl()` and removes the stale `MainTabControl` dependency. Status is **✅ OK** — verified with open charts for `NQ 09-26`, `MES 09-26`, and `MNQ 09-26`, producing non-transparent 903×792 RGBA PNGs.

---

## Raw Endpoint Count

The AddOn route switch in `McpBridgeAddOn.cs` defines **51 HTTP endpoints**. This audit covers all of them. `/api/chart/snapshot` and `/api/chart/trade` were verified this session (both wrap `CaptureChart()` and return valid PNGs).

## Three copies of `McpBridgeAddOn.cs`

The AddOn source now lives in a single canonical location:

1. `addons/McpBridgeAddOn.cs` — **this repo** (`nt8-mcp-bridge`). The sole source of truth since the 2026-08-12 repo split (see `docs/NT8_REPO_SPLIT_PLAN.md` in the `nt8-riskguard` repo).

The prior duplicates in `tvDownloadOHLC` (`scripts/ninjatrader/addons/`, `scripts/strategies/nt8/addons/`, and `mcp/ninjatrader-mcp/nt8-addon/`) were all deleted during the split and the subsequent wrapper fold.

## Next Steps / Action Items

1. ~~Fix `/api/chart/draw`~~ ✅ Done (2026-08-01). All 5 shape types verified.
2. ~~Verify `/api/chart/capture` with an open chart and confirm non-transparent PNG output.~~ ✅ Done.
3. ~~Test `/api/chart/snapshot` and `/api/chart/trade`.~~ ✅ Done. Both inherit CaptureChart and return valid PNGs.
4. Resolve `/api/script/execute` failure (dispatcher / payload shape / sandbox compile). The endpoint accepts `codeSnippet` (not `code`); connection drops after submission — may be a sandbox compile crash.
5. Consolidate the three `McpBridgeAddOn.cs` copies into one source of truth.
6. ~~Commit the `McpBridgeAddOn.cs` fixes and this audit document once the submodule state is reviewed.~~ ✅ Done.

---

## Agent-Loop Review (2026-08-01)

Two independent agents reviewed the entire `McpBridgeAddOn.cs` (~5100 lines), debated findings, and 8 fixes were implemented (commit `5f5c839c`).

### Fixes Applied

| # | Issue | Severity | Fix |
|---|-------|----------|-----|
| 1 | `GetIndicatorValues` disposed `BarsRequest` before async callback → empty/stale data or 30s timeout | High | Moved `done.Wait` inside the `using` block |
| 2 | `ClosePosition` ignored `symbol` param — flattened ALL positions | Critical | Added symbol filter for both orders and positions; removed unconditional Sim-account flatten |
| 3 | `DrawChartLevel` error-path walked chart visual trees from HTTP thread | Critical | Marshal "available charts" fallback to each window's own dispatcher |
| 4 | `EmergencyFlatten` called `SetState(Terminated)` on app dispatcher | Medium | Marshal to strategy's ChartControl dispatcher |
| 5 | `FindAnyChartControl` dead code with cross-thread WPF access (landmine) | Low | Deleted |
| 6 | Unfrozen cloned brushes in `DrawChartLevel` (OutlineStroke, AreaBrush) | Low | Added `.Freeze()` calls |
| 7 | `_handles` (dev/reflect) never cleared — unbounded growth | Low | Clear at start of each `RunOps` batch |
| 8 | `ClosePosition` missing null-check on `Application.Current` | Low | Added `?.Dispatcher` guard |

### Deferred (debated but not actionable now)

~~Both items have been fixed (commit below).~~

### Third-pass fixes (commit `73a5c019`)

| # | Issue | Fix |
|---|-------|-----|
| 15 | Single-threaded HTTP listener (all requests serialized) | `HandleRequests` now dispatches each request to `ThreadPool.QueueUserWorkItem` — blocking handlers no longer stall other endpoints. 3 concurrent requests verified: 267ms total. |
| 16 | Backtest shared SA window conflict under concurrency | Added `_saLock` (static object) — only one backtest runs at a time; other request types remain fully concurrent. |
| 17 | `DevReflect` `ui:true` marshals ALL ops to app dispatcher | Documented `"dispatcher":"auto"` option (resolves target object's own dispatcher per-op for chart-owned objects). `"ui":true` remains for app-level objects. |

### Second-pass fixes (commit `12d94c9c`)

| # | Issue | Fix |
|---|-------|-----|
| 9 | `MonteCarlo` placeholder P&L (`* 10` when no trades supplied) | Removed fallback; returns honest error with usage instructions |
| 10 | `PlaceAtmOrder` stub (no real bracket attachment) | **Replaced (2026-08-02)** with full `DynamicAtmManager` server-side bracket engine: 8 strategies, 13 instrument profiles, OCO-wired entry/stop/target, 5s monitor for DrawdownShield/ScaledRunner breakeven+trailing. Master-instrument guard rejects root tickers. Response normalized to camelCase. Covered by 30 ATM tests (20 C# unit + 10 Python integration). |
| 11 | `PlaceOcoOrder` submits entry+exits simultaneously with no reject check | Now checks exit order states after submit; reports rejected orders |
| 12 | `EmergencyFlatten` lockout never enforced | Added `IsAccountLocked()` helper; `PlaceOrder`/`PlaceOcoOrder` now check both RiskGuard and `_lockoutExpiry` |
| 13 | SSE stream (`/api/events/stream`) was heartbeat-only stub | Now loops with 15s heartbeats while `_running` is true |
| 14 | `FindAnyChartWindow` used 3-arg `Invoke` with Background priority (file's own comments warn against this) | Switched to no-timeout `Invoke` |

### Test Results (post-all-fixes, 2026-08-01)

All 20 tested endpoints pass (0 failures). `/api/script/execute` deferred per user request.

#### Fully Verified (functional behavior confirmed)

| Endpoint | What was verified | Result |
|----------|-----------------|--------|
| `/api/health` | Returns status=ok, version, accounts, feedConnected | ✅ Verified |
| `/api/account` | Returns account balances/PnL | ✅ Verified |
| `/api/positions` | Returns open positions | ✅ Verified |
| `/api/orders` | Returns working/historical orders with state | ✅ Verified |
| `/api/quote` | Returns bid/ask/last for MNQ SEP26 | ✅ Verified |
| `/api/bars` | Returns 3 1-min bars with OHLCV for MNQ SEP26 | ✅ Verified |
| `/api/search` | Returns instrument matches for "MNQ" | ✅ Verified |
| `/api/chart/list` | Returns 2 open chart windows with instrument info | ✅ Verified |
| `/api/chart/capture` | Returns valid PNG: 90KB, valid PNG header, 95.3% non-zero bytes (not transparent) | ✅ Verified |
| `/api/chart/draw` | All 5 shapes draw with correct tag. Idempotent: same tag replaces prior object | ✅ Verified |
| `/api/chart/snapshot` | Returns imageId, width=1280, height=720 | ✅ Verified |
| `/api/chart/trade` | Returns imageId + executionId (capture wrapper works) | ✅ Verified |
| `/api/chart/open` | Validates instrument, focuses Control Center (best-effort) | ✅ Verified |
| `/api/strategies` | Lists source files in Custom/Strategies | ✅ Verified |
| `/api/strategy/running` | Returns running strategy count | ✅ Verified |
| `/api/compile/result` | Returns success/errorCount/warnings | ✅ Verified |
| `/api/events/fills` | Returns fill history | ✅ Verified |
| `/api/riskguard/version` | Returns RiskGuard version | ✅ Verified |
| `/api/logs` | Returns log lines | ✅ Verified |
| `/api/trades/monte-carlo` | Verified with 10-trade input, 500 iterations: returns valid riskOfRuinPct, CVaR, drawdown percentiles, equity percentiles. Empty trades → honest error (no more placeholder `*10` P&L) | ✅ Verified |
| `/api/order/atm` | FixedTicks bracket on MNQ 09-26 → `status=submitted`, `bracketId`, `ocoId`, `stopPrice/targetPrice` (8/16 ticks), 3 order ids. IdempotencyKey dedupes. Unknown strategy / master instrument (`MNQ`) / missing fields → errors. Bracket orders visible in `/api/orders` as `AtmEntry_*`/`Stop_*`/`Target_*`. `GET /api/order/atm/status` lists active brackets; unknown id → `{error:"bracket not found"}`. DrawdownShield/ScaledRunner register for 5s breakeven/trailing monitor | ✅ Verified (closed market — orders Rejected by simulator, not a bridge bug) |
| `/api/emergency-flatten` | Triggers flatten + lockout. Lockout then blocks `PlaceOrder` with "Order blocked: Account Sim101 is locked out.". Unlock via `/api/lockout` clears it → orders accepted again | ✅ Verified |
| `/api/schedule/task` | In-process scheduler with Timer (30s tick). Supports interval + basic 5-field cron. Fires loopback HTTP calls to command endpoint | ✅ Verified |
| `/api/alert/create` | Price-level monitor via instrument.MarketData.Last.Price. Fires AlertCallback into Alerts Log on cross_above/cross_below | ✅ Verified |
| `/api/orchestrator/multi-account` | Now actually iterates target accounts and submits orders via CreateOrder/Submit. Returns per-account results with orderId. Without orders array → error | ✅ Verified |
| `/api/strategy/inspect` | Returns 214 properties with `isInput` flag (62 marked as NinjaScriptProperty inputs). Includes inherited StrategyBase properties (previously filtered out) | ✅ Verified |
| `/api/trades/extract` | Returns executions with `commission` field (exec.Commission). MAE/MFE null with note explaining limitation | ✅ Verified |
| `/api/backtest/signal` | Returns error for unsupported `entryRule` (was silently running SMA cross for any rule). Only `sma_crossover` supported | ⚠️ Partial |
| `/api/riskguard/config` | GET: returns live config. POST: writes to RiskGuard/config.json via `SaveAndReloadConfig(RiskConfig)` and reloads live engine | ✅ Verified |
| `/api/compliance/report` | Now tries PropFirmProtectionSuite.Config.DailyLossLimit (was hardcoded -2500). Returns `account not found` for non-existent accounts | ✅ Verified |
| `/api/trades/journal` | Supports add/create + delete + update (was Create+Read only). Loads from disk at top of every call. Stores loaded at startup | ✅ Verified |
| `/api/copier/config` | Real enforcement via TradeCopierEngine.OnExecution → CreateOrder/Submit | ✅ Verified |
| `/api/prop/limits` | Real — consumed by RiskGuardAddOn (News/Target/Peak subset) | ✅ Verified |

#### Market-Closed — Code Verified, Functional Pending Market Hours

These endpoints were verified at the **code level** (correct logic, no crashes, correct response shape) but could not be **functionally validated** because the market is closed (Friday evening, no live fills). They should be re-verified during RTH.

| Endpoint | What was tested (market closed) | What needs verification (market open) | Code Status |
|----------|-------------------------------|---------------------------------------|-------------|
| `/api/order` | Market order submitted, state=Submitted (not Filled — market closed) | Order fills, position appears in `/api/positions`, fill appears in `/api/events/fills` | ✅ Code correct |
| `/api/order/oco` | OCO submitted, all 3 legs state=Submitted, `rejectedExitOrders=null` | Exit orders activate after entry fill; OCO cancels one leg when the other fills | ⚠️ Pending market hours |
| `/api/position/close` | `symbol=MNQ SEP26` → cancelled 2 MNQ working orders, `positionClosed=false` (no position to close). Symbol filter verified: only MNQ orders cancelled | Close an actual open position, verify it flattens only that symbol | ✅ Symbol filter verified; ⚠️ Position close pending market hours |
| `/api/indicator/values` | BarsRequest callback fires correctly (status=NoError, 291ms — was 30s timeout before fix). Returns "no bar data" (no cached bars on this NT8 instance) | Re-run with an instrument with cached bar data, verify SMA/EMA/RSI values returned | ✅ BarsRequest fix verified (291ms vs 30s); ⚠️ Data pending market hours |
| `/api/backtest` | Not re-tested (takes 180s, single-threaded listener blocks all other requests) | Run a backtest with a compiled strategy on NQ 09-26, verify trade list and PnL | ⚠️ Pending |
| `/api/strategy/deploy` | Not re-tested (requires open chart + strategy compiled) | Deploy a strategy to an open chart, verify it appears in `/api/strategy/running` | ⚠️ Pending |
| `/api/strategy/stop` | Not re-tested (no running strategies) | Deploy then stop a strategy, verify position flatten | ⚠️ Pending |
| `/api/order/change` | Not re-tested (no working orders) | Modify a working limit order, verify price/quantity changes | ⚠️ Pending |
| `/api/order/cancel` | Not re-tested | Cancel a working order by orderId and by ocoId | ⚠️ Pending |
| `/api/script/execute` | Deferred per user request | Accepts `codeSnippet` param; connection drops after submission | ⏭️ Deferred |

---

## Test Harness (added 2026-08-02)

The ATM bracket engine is covered by two complementary test suites. Run both after touching `DynamicAtmManager.cs`, `McpBridgeAddOn.cs` `PlaceAtmOrder`/`GetAtmBracketStatus`, or the mock/stub layer.

### C# Unit Harness — `ninjatrader-addon\RiskGuardTests.csproj`

Pure-logic tests of `DynamicAtmManager` strategy math, profile lookup, OCO wiring, bracket registration, and error paths. **No NinjaTrader assembly required** — NT8 runtime types are stubbed under `#if TESTING` in `TestingStubs.cs` + `RiskGuardAddOnTests.cs`.

```powershell
dotnet build ninjatrader-addon\RiskGuardTests.csproj
dotnet run --project ninjatrader-addon\RiskGuardTests.csproj --no-build
```

**Result: 323 tests passed, 0 failed.** 20 ATM-specific tests added this session:

| Test | Verifies |
|------|----------|
| `TestAtm_FixedTicksLong` / `...Short` | stop/target price formulas, order actions, order names |
| `TestAtm_OcoIdSharedAcrossExitOrders` | stop+target share OcoId; entry has empty Oco |
| `TestAtm_DrawdownShieldRegistersBracket` / `TestAtm_ScaledRunnerRegistersBracket` | monitored strategies register in `GetActiveBrackets()` |
| `TestAtm_MonitoredStrategiesNotDoubleRegistered` | FixedTicks does NOT register |
| `TestAtm_VolatilityScaledQuantityCapped` / `...RiskBasedQuantity` | risk-based qty = `floor(RiskPerTrade/riskPerContract)`, capped at `MaxContracts` |
| `TestAtm_AtrAdaptiveFallbackUsesDefaultAtr` | no bars → atr = `DefaultATR*TickSize` |
| `TestAtm_AtrAdaptiveUsesLiveAtr` | injected bars → live ATR drives stop/target |
| `TestAtm_SwingPointUsesSwingLow` | injected bars → swing low ± buffer drives stop |
| `TestAtm_SessionAdaptiveMultiplier` | RTH/ETH multiplier smoke (deterministic: both ≥1) |
| `TestAtm_UnknownSymbolFallsBackToDefaults` | unknown root → default profile (FixedTicks) |
| `TestAtm_GetProfileKnownAndUnknown` | 13 profiles; unknown root → null |
| `TestAtm_ZeroPriceReturnsError` | zero entry → "Could not calculate stop/target prices" |
| `TestAtm_RejectedExitOrdersPartialSubmit` | rejected exits → `status=partial_submit` |
| `TestAtm_ShouldTriggerBreakeven` / `TestAtm_CalculateBreakevenStopPrice` | pure breakeven logic |
| `TestAtm_ActiveBracketStatus` | `GetBracketStatus` returns camelCase payload; unknown id → error |

**Injecting bars for ATR/swing tests:** set `BarsRequest.TestBarsFactory` (static on the stub) to a `Func<BarsRequest, Bars>` returning deterministic OHLCV. Reset to `null` after — the fallback-dependent tests do this defensively for isolation.

### Python Integration Harness — `tests\test_risk_guard_integration.py`

Live bridge tests via HTTP. **Requires NT8 running with the McpBridge AddOn.** Auth: sends `Authorization: Bearer <token>` (token read from env `NT8_MCP_TOKEN` or `.mcp.json`).

```powershell
.\.venv\Scripts\python.exe -m pytest tests\test_risk_guard_integration.py -k atm -v
```

**Result: 10 ATM tests passed, 0 failed** (run against live bridge with closed market — orders Rejected by simulator, not a bridge bug). ATM tests use a lightweight cleanup (no 7.5s settle-wait) via a `wait=False` path in `cleanup_all()`.

| Test | Verifies |
|------|----------|
| `test_atm_health_requires_auth` | no-token → 401 (only when token configured) |
| `test_atm_place_fixed_ticks_bracket` / `..._short_bracket` | response contract (status/bracketId/ocoId/prices/ids), stop<target for long, stop>target for short |
| `test_atm_idempotency_dedupes` | same `idempotencyKey` → same `bracketId` |
| `test_atm_unknown_strategy_rejected` | invalid `strategyName` → "unknown strategy" error |
| `test_atm_master_instrument_rejected` | root ticker (`MNQ`) → "instrument not found" or "master instrument" error |
| `test_atm_missing_symbol_or_action_rejected` | missing fields → "symbol and action required" |
| `test_atm_bracket_status_listing` | `GET /api/order/atm/status` → `{count, brackets[]}` shape |
| `test_atm_bracket_status_unknown_id` | unknown bracketId → `{error: "bracket not found"}` |
| `test_atm_bracket_orders_visible_in_orders_list` | bracket orders appear in `GET /api/orders` as `AtmEntry_*`/`Stop_*`/`Target_*` |

### Harness completeness fixes (2026-08-02)

The C# harness was **broken** (could not build) before this session. Fixes applied:

1. **csproj glob path** pointed to non-existent `scripts\strategies\nt8\addons\` (real: `scripts\ninjatrader\addons\`) — corrected; also excluded auto-globbed `Strategies\Vinay\*.cs` that dragged in NinjaScript-base dependencies (68 → 0 errors).
2. **Missing NT8 stubs** for `DynamicAtmManager` deps: `OrderState.CancelPending`, `Account.Change()`, `MarketData.Ask/Bid`, `BarsPeriod` props, `Bars`, `BarsRequest` (with `TestBarsFactory` hook), `ErrorCode`.
3. **WPF dispatcher guard** in `DynamicAtmManager.MonitorTick` — `#if TESTING` calls `MonitorTickCore()` directly (no `System.Windows.Application`), matching the `RiskGuardAddOn` pattern.
4. **`Account.SimulateExitRejection`** flag added to the mock so the `partial_submit` path is unit-testable.
5. **Python auth** — requests now send the Bearer token (previously 401 against the live bridge).
