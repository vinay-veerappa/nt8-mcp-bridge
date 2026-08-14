# NinjaTrader MCP Architectural Solutions & Feature Roadmap

This document details technical solutions for existing constraints (**`nt_deploy_strategy`** and **`execute_script`**) as well as a comprehensive roadmap of proposed high-value feature additions for the **NinjaTrader 8 MCP Server**.

---

## 1. Solutions for `nt_deploy_strategy` Constraints

### Core Constraints Identified
1. **Open Chart Dependency**: `DeployStrategy` requires an active WPF `ChartControl` window for the target instrument (`FindChartControl`). If no chart is open, it returns a `best_effort` error requiring manual user action (`Ctrl+Shift+N`).
2. **Strategy Container Warm-Up**: Calling `ChartControl.ApplyStrategy` via reflection requires the target chart's WPF strategy panel and collection to be initialized.
3. **Multi-Thread WPF Dispatcher Affinity**: Each NinjaTrader 8 chart window runs on its own WPF `Dispatcher` thread (e.g., ManagedThreadId 18/19), separate from Application Thread 1.

---

### Proposed Architectural Solutions

#### Option A: Automated Chart Window Creation via Internal WPF Factory (Recommended UI Approach)
* **Concept**: Programmatically spawn a new chart window when `FindChartControl` fails to locate an open chart for the target symbol.
* **Mechanism**:
  1. Marshal execution to `System.Windows.Application.Current.Dispatcher`.
  2. Invoke NinjaTrader's internal window creation pipeline via reflection:
     ```csharp
     var inst = NinjaTrader.Cbi.Instrument.GetInstrument(symbol);
     var chartWindow = NinjaTrader.Gui.Chart.ChartFactory.CreateChart(inst, ChartData.Interval, ...);
     chartWindow.Show();
     ```
  3. Asynchronously wait for the new `ChartControl` and `ChartBars` elements to complete WPF initialization.
  4. Obtain the new `ChartControl` dispatcher thread and invoke `ApplyStrategy` & `StrategyEnable`.
* **Pros**: Completely eliminates manual user setup (`Ctrl+Shift+N`); full visual chart rendering and trade marker support.
* **Cons**: Uses non-public internal NT8 WPF factory methods (`ChartFactory.CreateChart` / `ControlCenter.OpenChart`).

---

#### Option B: Headless Strategy Engine Deployment (Recommended Server Approach)
* **Concept**: Decouple strategy deployment from WPF Chart UI controls entirely. Run strategies programmatically inside NinjaTrader's background **Strategy Runtime Engine**.
* **Mechanism**:
  1. Instantiate the compiled strategy class via `Activator.CreateInstance(stratType)`.
  2. Create a `BarsRequest` / `Data.Bars` historical data series programmatically for the target instrument and period.
  3. Bind the strategy instance to the target `Account` object and data series.
  4. Drive the strategy lifecycle state machine directly:
     ```csharp
     strat.SetState(State.SetDefaults);
     strat.SetState(State.Configure);
     strat.SetState(State.DataLoaded);
     strat.SetState(State.Historical);
     // Process historical bars...
     strat.SetState(State.Realtime);
     ```
  5. Register the strategy instance with `NinjaTrader.Cbi.Account` and `NinjaTrader.NinjaScript.Strategies.Strategy.All`.
* **Pros**:
  - **Zero UI Dependency**: Works whether charts are open or not.
  - **High Scalability**: Can run hundreds of automated strategies without GPU/WPF rendering overhead.
  - **100% Reliable Execution**: Eliminates WPF thread dispatching deadlocks.
* **Cons**: Visual plot drawings will not render on a chart window, though order placement, signals, trailing stops, and RiskGuard protection function identically.

---

#### Option C: Chart Strategy Container Warm-Up
* **Concept**: Fix the "strategy-less chart" limitation when deploying onto an existing chart.
* **Mechanism**: If `ChartControl.ApplyStrategy` fails because the chart container is uninitialized:
  1. Programmatically invoke `ChartControl.Strategies.Add(strat)`.
  2. Force a WPF layout pass via `ChartControl.UpdateLayout()`.
  3. Re-query `ChartControl.Strategies` to verify registration before calling `StrategyEnable`.
* **Pros**: Resolves failure edge cases on strategy-less charts.

---

---

## 2. Solutions for `execute_script` (`ScriptExecute`)

### Core Flaws & Risks Identified
1. **Heavy Compilation Overhead & Connection Resets**:
   - Generates a temporary `_ScriptEval_XXXXXX.cs` file in `bin\Custom\Strategies`.
   - Triggers `CompileCore(false)`, causing Roslyn to recompile the entire NinjaScript workspace.
   - Triggers an assembly hot-swap inside NT8, which **resets all open HTTP keep-alive sockets** across the MCP bridge (causing 3–10s latency per call).
2. **Limited Execution Context**:
   - Snippets are wrapped in a static method (`public static object Run()`), lacking implicit access to active `Account`, `Positions`, `MarketData`, or `Logger` instances unless fetched via verbose statics (`Account.All`, `Instrument.GetInstrument`).
3. **Security Risks**:
   - Free-form C# execution could run destructive OS calls (`System.IO.File.Delete`, `Process.Start`).

---

### Proposed Architectural Solutions

#### Option A: In-Memory Roslyn Scripting Engine (`Microsoft.CodeAnalysis.CSharp.Scripting`)
* **Concept**: Replace temporary disk files and full NT8 workspace compilation with in-memory **Roslyn Script Evaluation**.
* **Mechanism**:
  1. Maintain a persistent `CSharpScript` engine inside `McpBridgeAddOn`.
  2. Reference loaded NT8 assemblies (`NinjaTrader.Cbi.dll`, `NinjaTrader.Data.dll`, `NinjaTrader.Core.dll`, `NinjaTrader.Gui.dll`).
  3. Pass a rich `ScriptContext` object into script evaluation:
     ```csharp
     public class ScriptContext
     {
         public Account Account { get; set; }
         public List<Account> Accounts => Account.All.ToList();
         public Action<string> Log { get; set; }
         public Func<string, double> GetLastPrice { get; set; }
     }
     ```
  4. Evaluate snippets dynamically using:
     ```csharp
     var result = await CSharpScript.EvaluateAsync(snippet, options, globals: context);
     ```
* **Pros**:
  - **Sub-50ms Execution**: Evaluates in milliseconds without writing disk files.
  - **Zero Bridge Disruptions**: Does not trigger Roslyn workspace recompilation or HTTP connection resets.
  - **Rich API Access**: Snippets directly reference `Account`, `Positions`, `Logs`, etc.
* **Cons**: Requires ensuring `Microsoft.CodeAnalysis.Scripting.dll` is present in NT8's assembly resolver path.

---

#### Option B: Security & Execution Sandbox (AST Whitelisting)
* **Concept**: Protect the host machine from unsafe or destructive C# code execution.
* **Mechanism**:
  1. Pre-parse snippet using Roslyn syntax trees (`CSharpSyntaxTree.ParseText`).
  2. Inspect syntax nodes against a strict whitelist before compiling:
     - **Disallowed**: `System.IO.File.Delete`, `System.Diagnostics.Process`, `System.Reflection.Emit`, `Unsafe`, `DllImport`.
     - **Allowed**: `NinjaTrader.*`, `System.Linq`, `System.Math`, `System.Collections`.
  3. Reject unauthorized snippets with a clear validation error message prior to compilation.
* **Pros**: Guarantees system safety and prevents malicious or accidental system calls.

---

---

## 3. High-Value Future Feature Roadmap

Below are **10 proposed features** designed to expand NinjaTrader MCP's capabilities across quantitative research, automated trading, and daily profiling.

---

### Feature 1: Batch Daily Profiler & Levels Sync (`nt_sync_levels`)
* **Purpose**: Batch update and plot key levels (PDH, PDL, Midnight Open, 8:30 AM Open, RTH Open, Initial Balance IBH/IBL, FVG zones) directly onto NT8 chart windows in a single API call.
* **Input Payload**:
  ```json
  {
    "symbol": "NQ 09-26",
    "clearExisting": true,
    "levels": [
      { "name": "Midnight Open", "price": 28450.25, "color": "#FF9900", "style": "Dash" },
      { "name": "PDH", "price": 28620.00, "color": "#00FF00", "style": "Solid" },
      { "name": "PDL", "price": 28210.50, "color": "#FF0000", "style": "Solid" }
    ]
  }
  ```
* **Implementation**: Iterates levels array, clears previously drawn MCP tags, and calls `Draw.Line` / `Draw.Text` on the target `ChartControl`.

---

### Feature 2: Strategy Optimization & Parameter Tuning (`nt_optimize_strategy`)
* **Purpose**: Expose NinjaTrader 8's internal `StrategyOptimizer` engine (Grid Search and Genetic Algorithms) to AI agents for automated parameter tuning.
* **Input Payload**:
  ```json
  {
    "strategy": "IBBreakoutBot",
    "symbol": "NQ 09-26",
    "from": "2026-07-01",
    "to": "2026-08-01",
    "metric": "ProfitFactor",
    "algorithm": "GridSearch",
    "paramRanges": {
      "StopTicks": { "min": 10, "max": 50, "step": 5 },
      "TargetTicks": { "min": 20, "max": 100, "step": 10 }
    }
  }
  ```
* **Implementation**: Binds parameters to `NinjaTrader.NinjaScript.Optimization.GeneticOptimizer` or `DefaultOptimizer`, executes backtest iterations, and returns ranked performance results.

---

### Feature 3: Market Replay Automation (`nt_replay_control`)
* **Purpose**: Provide full programmatic control over NT8's Market Replay engine (`PlaybackConnection`).
* **Input Payload**:
  ```json
  {
    "action": "play", // "play", "pause", "step", "set_speed", "jump_to_date"
    "speed": 5,
    "stepBars": 1,
    "targetDate": "2026-07-25T09:30:00Z"
  }
  ```
* **Implementation**: Connects to `NinjaTrader.Cbi.Connection.PlaybackConnection`, controlling replay clock step-by-step to verify signals against Python ground-truth models.

---

### Feature 4: Custom Data Import Bridge (`nt_import_bars`)
* **Purpose**: Programmatically import custom CSV/Parquet bar datasets into NT8's historical database (`NinjaTrader.Data.Db`).
* **Input Payload**:
  ```json
  {
    "symbol": "NQ_Continuous",
    "period": "Minute",
    "periodValue": 1,
    "filePath": "C:/data/processed/NQ_1m_continuous.csv"
  }
  ```
* **Implementation**: Invokes `NinjaTrader.Data.Db.Import` programmatically, allowing Python continuous futures pipeline datasets to populate NT8 backtest charts.

---

### Feature 5: Prop Firm Preset Protection (`nt_apply_prop_preset`)
* **Purpose**: Instant configuration of RiskGuard rules matching exact evaluation firm specifications (Apex, Topstep, MyFundedFutures).
* **Input Payload**:
  ```json
  {
    "account": "Sim101",
    "preset": "Topstep_100k_Eval" // Configures $3,000 trailing drawdown, $2,000 daily stop, 10-contract cap, news shield
  }
  ```
* **Implementation**: Maps preset definitions to `RiskGuardConfig` and `PropLimits` statics in `McpBridgeAddOn`.

---

### Feature 6: Volume Profile & Order Flow Metrics (`nt_volume_profile`)
* **Purpose**: Retrieve session Order Flow metrics (VAH, VAL, POC, High Volume Nodes, Cumulative Delta).
* **Input Payload**:
  ```json
  {
    "symbol": "NQ 09-26",
    "sessionDate": "2026-08-07"
  }
  ```
* **Implementation**: Queries loaded volumetric bars or `OrderFlowVolumeProfile` indicator values to return auction market statistics.

---

### Feature 7: Execution Slippage & Latency Analytics (`nt_execution_stats`)
* **Purpose**: Measure order fill latency and slippage across live vs simulation execution.
* **Input Payload**:
  ```json
  {
    "account": "Sim101",
    "limit": 50
  }
  ```
* **Implementation**: Analyzes order creation timestamp vs execution timestamp, comparing requested limit/stop price against actual fill price to return latency (ms) and slippage averages.

---

### Feature 8: Dynamic Portfolio Allocator & Risk Scaling (`nt_rebalance_portfolio`)
* **Purpose**: Multi-strategy position sizing based on live volatility (ATR) or account equity curve drawdown.
* **Input Payload**:
  ```json
  {
    "account": "Sim101",
    "maxPortfolioRiskPct": 0.02,
    "strategies": ["IBBreakoutBot", "FVGFillBot"]
  }
  ```
* **Implementation**: Dynamically adjusts strategy contract quantities (`SetStrategyParam`) based on current account equity and market ATR.

---

### Feature 9: Chart Layout & Workspace Manager (`nt_workspace_manager`)
* **Purpose**: Programmatically save, load, or switch NinjaTrader workspaces and chart layouts.
* **Input Payload**:
  ```json
  {
    "action": "open", // "list", "open", "save"
    "workspaceName": "NQ_Wargaming_Layout"
  }
  ```
* **Implementation**: Calls `NinjaTrader.Gui.Tools.Workspace.Open(...)` on the main WPF application thread.

---

### Feature 10: Inbound Signal Webhook Router (`nt_signal_webhook`)
* **Purpose**: Direct bridge accepting external signals (e.g. from TradingView or Python engines) to trigger instant order execution or ATM brackets inside NT8.
* **Input Payload**:
  ```json
  {
    "signal": "BUY_BREAKOUT",
    "symbol": "NQ 09-26",
    "quantity": 2,
    "stopLossTicks": 20,
    "targetTicks": 40
  }
  ```
* **Implementation**: Translates inbound JSON webhook payloads directly into `PlaceAtmOrder` / `PlaceOcoOrder` calls within `McpBridgeAddOn`.

---

## Summary Matrix

| Feature | Category | Primary Benefit | Implementation Complexity |
| :--- | :--- | :--- | :--- |
| **`nt_deploy_strategy` Fix** | Strategy Lifecycle | Headless strategy deployment without open charts | Medium |
| **`execute_script` Fix** | Development | In-memory Roslyn evaluation in < 50ms | Medium |
| **`nt_sync_levels`** | Daily Profiler | Batch drawing of P12 levels & FVG zones | Low |
| **`nt_optimize_strategy`** | Quantitative Research | Automated parameter tuning / genetic optimization | High |
| **`nt_replay_control`** | Verification | Programmatic Market Replay bar-by-bar control | Medium |
| **`nt_import_bars`** | Data Pipeline | Python Parquet/CSV data ingestion to NT8 Db | Medium |
| **`nt_apply_prop_preset`** | Risk Management | Instant prop-firm rule compliance setups | Low |
| **`nt_volume_profile`** | Market Structure | Session VAH, VAL, POC, and Delta analytics | Medium |
| **`nt_execution_stats`** | Execution Quality | Slippage and latency performance audit | Low |
| **`nt_rebalance_portfolio`** | Portfolio Control | Volatility-scaled multi-strategy position sizing | Medium |
| **`nt_workspace_manager`** | UI Automation | Programmatic workspace and layout loading | Low |
| **`nt_signal_webhook`** | System Integration | Direct TradingView/Python signal execution router | Low |
