#!/usr/bin/env node
/**
 * nt-mcp-server.js — MCP (Model Context Protocol) server for NinjaTrader 8
 *
 * Architecture:
 *   Claude/Hermes (MCP stdio)  →  nt-mcp-server.js  →  HTTP :7890 (Bearer Auth)  →  NT8 McpBridgeAddOn
 *
 * Zero npm dependencies. Uses only Node.js builtins.
 * Run: node nt-mcp-server.js
 * Version: 1.4.0
 */

import { createInterface } from 'node:readline';
import { request as httpRequest } from 'node:http';

// ─── Config ─────────────────────────────────────────────────────────────
const NT8_HOST = process.env.NT8_HOST || '127.0.0.1';
const NT8_PORT = parseInt(process.env.NT8_PORT || '7890', 10);
const NT8_BASE = `http://${NT8_HOST}:${NT8_PORT}`;
const NT8_MCP_TOKEN = process.env.NT8_MCP_TOKEN || '';

const SERVER_NAME = 'nt-mcp-server';
const SERVER_VERSION = '1.5.0';
const MCP_PROTOCOL_VERSION = '2024-11-05';


// ─── Tool Definitions ───────────────────────────────────────────────────
const TOOLS = [
  {
    name: 'nt_health',
    description: 'Check connection to NinjaTrader 8 AddOn, version, and auth status',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_accounts',
    description: 'List accounts, cash balances, buying power, and total equity',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_positions',
    description: 'List open market positions with live P&L per account',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_orders',
    description: 'List active/working orders with execution status and cursor pagination',
    inputSchema: {
      type: 'object',
      properties: {
        account: { type: 'string', description: 'Filter by account name' },
        limit:   { type: 'number', description: 'Max orders to return (default 50)', default: 50 },
        offset:  { type: 'number', description: 'Offset for pagination', default: 0 },
      },
    },
  },
  {
    name: 'nt_place_order',
    description: 'Place a Market, Limit, StopMarket, StopLimit, or MIT order. Requires idempotencyKey in production mode.',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:         { type: 'string', description: 'Ticker (e.g. NQ 09-26, ES, MES)' },
        action:         { type: 'string', enum: ['buy', 'sell'], description: 'Direction' },
        quantity:       { type: 'number', description: 'Number of contracts' },
        orderType:      { type: 'string', enum: ['Market', 'Limit', 'StopMarket', 'StopLimit', 'MIT'], description: 'Order type' },
        price:          { type: 'number', description: 'Price / Limit Price (for Limit/StopLimit/MIT)' },
        limitPrice:     { type: 'number', description: 'Limit Price (alternative to price)' },
        stopPrice:      { type: 'number', description: 'Stop price (for StopMarket/StopLimit)' },
        timeInForce:    { type: 'string', enum: ['Day', 'GTC', 'IOC', 'FOK'], description: 'Time in force' },
        ocoId:          { type: 'string', description: 'Optional OCO group ID string' },
        name:           { type: 'string', description: 'Optional custom order label / signal name' },
        account:        { type: 'string', description: 'Optional target account name' },
        confirmLive:    { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        idempotencyKey: { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'action', 'quantity', 'idempotencyKey'],
    },
  },
  {
    name: 'nt_place_oco_order',
    description: 'Place paired atomic OCO (One-Cancels-Other) limit/stop orders',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:         { type: 'string', description: 'Ticker (e.g. NQ 09-26)' },
        quantity:       { type: 'number', description: 'Order quantity' },
        account:        { type: 'string', description: 'Account name', default: 'Sim101' },
        confirmLive:    { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        limitPrice:     { type: 'number', description: 'Profit target limit price' },
        stopPrice:      { type: 'number', description: 'Stop loss price' },
        action:         { type: 'string', enum: ['buy', 'sell'], description: 'Primary entry direction' },
        idempotencyKey: { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'quantity', 'limitPrice', 'stopPrice', 'action', 'idempotencyKey'],
    },
  },
  {
    name: 'nt_place_atm_order',
    description: 'Place an order bound to server-side ATM strategy brackets (stop loss, profit target, auto-breakeven)',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:         { type: 'string', description: 'Ticker (e.g. NQ 09-26)' },
        action:         { type: 'string', enum: ['buy', 'sell'], description: 'Direction' },
        quantity:       { type: 'number', description: 'Contracts' },
        strategyName:   { type: 'string', description: 'ATM strategy template name (e.g. SwingPointTrailing, VolatilityAdaptive, DrawdownShield)', default: 'VolatilityAdaptive' },
        stopTicks:      { type: 'number', description: 'Stop loss distance in ticks' },
        targetTicks:    { type: 'number', description: 'Profit target distance in ticks' },
        account:        { type: 'string', description: 'Target account', default: 'Sim101' },
        confirmLive:    { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        idempotencyKey: { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'action', 'quantity', 'idempotencyKey'],
    },
  },
  {
    name: 'nt_change_order',
    description: 'Modify a working order (quantity, limit price, stop price)',
    inputSchema: {
      type: 'object',
      properties: {
        orderId:    { type: 'string', description: 'Order ID to modify' },
        quantity:   { type: 'number', description: 'New quantity' },
        limitPrice: { type: 'number', description: 'New limit price' },
        stopPrice:  { type: 'number', description: 'New stop price' },
      },
      required: ['orderId'],
    },
  },
  {
    name: 'nt_cancel_order',
    description: 'Cancel an order by ID or OCO ID group',
    inputSchema: {
      type: 'object',
      properties: {
        orderId: { type: 'string', description: 'Order ID to cancel' },
        ocoId:   { type: 'string', description: 'OCO group ID to cancel all orders in the group' },
      },
    },
  },
  {
    name: 'nt_cancel_all_orders',
    description: 'Cancel all working orders across accounts',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_close_position',
    description: 'Flatten a position and cancel all its working orders by symbol and account',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:  { type: 'string', description: 'Ticker (e.g. NQ 09-26)' },
        account: { type: 'string', description: 'Optional account name' },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_emergency_flatten',
    description: 'Atomic Panic Kill-Switch: Cancels all orders, flattens all positions, and engages temporary RiskGuard lockout in one atomic C# call.',
    inputSchema: {
      type: 'object',
      properties: {
        account:        { type: 'string', description: 'Account name (omit = all accounts)' },
        lockoutMinutes: { type: 'number', description: 'Minutes to lock account from new trades', default: 60 },
        idempotencyKey: { type: 'string', description: 'Mandatory UUID string' },
      },
      required: ['idempotencyKey'],
    },
  },
  {
    name: 'nt_quote',
    description: 'Get the current quote with auto-subscription',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Ticker (e.g. NQ 09-26)' },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_bars',
    description: 'Fetch historical OHLCV bars (Minute, Day, Tick, Volume, Range) with pagination (max 5,000 rows)',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:      { type: 'string', description: 'Ticker' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Tick', 'Volume', 'Range'], description: 'Period', default: 'Minute' },
        periodValue: { type: 'number', description: 'Period value (e.g. 5 for 5m)', default: 1 },
        count:       { type: 'number', description: 'Number of bars (max 5,000)', default: 100 },
        offset:      { type: 'number', description: 'Pagination offset', default: 0 },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_search',
    description: 'Search available instrument master records by symbol or name',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Search query (e.g. NQ, Gold, ES)' },
      },
      required: ['query'],
    },
  },
  {
    name: 'nt_export_bars',
    description: 'Export historical OHLCV bars over a UTC DATE RANGE to a CSV file on the NT8 machine (NT8 downloads missing history from data provider on demand).',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:      { type: 'string', description: 'Instrument (e.g. RTY 03-25, ES 09-26, M2K 09-26)' },
        from:        { type: 'string', description: 'UTC Start date YYYY-MM-DD (ISO-8601)' },
        to:          { type: 'string', description: 'UTC End date YYYY-MM-DD (default: now)' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Second', 'Tick', 'Volume', 'Range'], description: 'Bars period type', default: 'Minute' },
        periodValue: { type: 'number', description: 'Bars period value (e.g. 5 for 5m)', default: 1 },
        merge:       { type: 'string', enum: ['DoNotMerge', 'MergeNonBackAdjusted', 'MergeBackAdjusted'], description: 'DoNotMerge = single contract. MergeNonBackAdjusted = continuous series stitched with real historical prices. MergeBackAdjusted = price-shifted series.', default: 'DoNotMerge' },
        timeoutSec:  { type: 'number', description: 'Max seconds to wait for provider download', default: 180 },
      },
      required: ['symbol', 'from'],
    },
  },
  {
    name: 'nt_get_export',
    description: 'Fetch the content of an export CSV file by filename.',
    inputSchema: {
      type: 'object',
      properties: { name: { type: 'string', description: 'Export filename, e.g. mcp_bars_RTY_03_25_Minute1.csv' } },
      required: ['name'],
    },
  },
  {
    name: 'nt_capture_chart',
    description: 'Capture active NinjaTrader WPF chart window as a base64 PNG screenshot image',
    inputSchema: {
      type: 'object',
      properties: {
        symbol: { type: 'string', description: 'Instrument symbol of chart to capture (e.g. NQ 09-26)' },
      },
    },
  },
  {
    name: 'nt_chart_snapshot',
    description: 'Generate high-res chart snapshot with visual execution markers (buy/sell), price lines, and indicator overlays',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:     { type: 'string', description: 'Instrument symbol' },
        width:      { type: 'number', description: 'Width px', default: 1280 },
        height:     { type: 'number', description: 'Height px', default: 720 },
        markers:    { type: 'array', items: { type: 'object' }, description: 'Overlay markers [{ time, price, label, color, shape }]' },
        indicators: { type: 'array', items: { type: 'string' }, description: 'Indicator names to highlight' },
        timeRange:  { type: 'string', description: 'Time range to display (e.g. 09:30-16:00 ET or ISO range)' },
      },
    },
  },
  {
    name: 'nt_trade_chart',
    description: 'Capture chart screenshot automatically for an execution fill with trade marker overlay returning imageId & base64',
    inputSchema: {
      type: 'object',
      properties: {
        executionId: { type: 'string', description: 'Execution ID to overlay trade markers for' },
        symbol:      { type: 'string', description: 'Instrument symbol (e.g. NQ 09-26)' },
        account:     { type: 'string', description: 'Account name filter' },
        width:       { type: 'number', description: 'Width px', default: 1280 },
        height:      { type: 'number', description: 'Height px', default: 720 },
      },
    },
  },

  {
    name: 'nt_open_chart',
    description: 'Programmatically open a new chart window/tab for a symbol and period',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:      { type: 'string', description: 'Instrument symbol (e.g. NQ 09-26)' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Second', 'Tick', 'Volume', 'Range'], default: 'Minute' },
        periodValue: { type: 'number', default: 1 },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_get_logs',
    description: 'Tail NinjaTrader Output tab logs, Strategy Analyzer output, or interventions.jsonl audit file',
    inputSchema: {
      type: 'object',
      properties: {
        tab:   { type: 'string', enum: ['Output', 'Log', 'Interventions'], default: 'Output' },
        lines: { type: 'number', description: 'Number of lines to tail', default: 100 },
      },
    },
  },
  {
    name: 'nt_fill_events',
    description: 'Query account execution fill history with pagination',
    inputSchema: {
      type: 'object',
      properties: {
        account: { type: 'string', description: 'Filter by account' },
        count:   { type: 'number', description: 'Number of recent fills to return', default: 50 },
        offset:  { type: 'number', description: 'Offset for pagination', default: 0 },
      },
    },
  },
  {
    name: 'nt_inspect_strategy',
    description: 'Inspect property declarations, input parameters, and metadata of compiled NinjaScript strategies',
    inputSchema: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Strategy class name (e.g. PathSignatureUnion or LIST)' },
      },
    },
  },
  {
    name: 'nt_riskguard_state',
    description: 'Read live RiskGuard account FSM state (Flat, InPosition, SoftStop, HardStop, Lockout), drawdown, and loss limits',
    inputSchema: {
      type: 'object',
      properties: {
        account:    { type: 'string', description: 'Account name' },
        instrument: { type: 'string', description: 'Instrument name' },
      },
    },
  },
  {
    name: 'nt_copier_config',
    description: 'Get/Set TradeCopierEngine relationships (Leader-Follower account ratios, Micro/Mini scaling, account quarantine)',
    inputSchema: {
      type: 'object',
      properties: {
        action:        { type: 'string', enum: ['get', 'set', 'quarantine'], default: 'get' },
        leaderAccount: { type: 'string', description: 'Leader account name' },
        followerAccount:{ type: 'string', description: 'Follower account name' },
        quantityRatio: { type: 'number', description: 'Quantity scaling ratio', default: 1.0 },
        autoConversion: { type: 'boolean', description: 'Auto Mini -> Micro conversion (NQ -> 10 MNQ)', default: true },
      },
    },
  },
  {
    name: 'nt_prop_limits',
    description: 'Query and update PropFirmProtectionSuite rules (Target Profit lock, Peak Equity Giveback cap, High-Impact News Shield blackout windows)',
    inputSchema: {
      type: 'object',
      properties: {
        action:           { type: 'string', enum: ['get', 'set'], default: 'get' },
        enableNewsShield: { type: 'boolean', description: 'Enable High-Impact USD news blackout shield' },
        newsBufferMin:    { type: 'number', description: 'News blackout buffer minutes before/after' },
        evaluationTarget: { type: 'number', description: 'Evaluation target profit lock ($)' },
        givebackCapPct:   { type: 'number', description: 'Max peak equity giveback cap % (e.g. 0.30)' },
      },
    },
  },
  {
    name: 'nt_extract_trades',
    description: 'Extract trade execution records enriched with MAE, MFE, duration, commissions, macro session window tags, and latency metrics for trade journaling (JSON/CSV)',
    inputSchema: {
      type: 'object',
      properties: {
        account:   { type: 'string', description: 'Account name' },
        format:    { type: 'string', enum: ['json', 'csv'], default: 'json' },
        from:      { type: 'string', description: 'UTC Start date YYYY-MM-DD (ISO-8601)' },
        to:        { type: 'string', description: 'UTC End date YYYY-MM-DD' },
        limit:     { type: 'number', description: 'Max trades', default: 100 },
      },
    },
  },
  {
    name: 'nt_monte_carlo',
    description: 'Run Block Bootstrap Monte Carlo simulations over trade history to evaluate Risk of Ruin %, CVaR @ 95%/99%, and drawdown confidence bands',
    inputSchema: {
      type: 'object',
      properties: {
        strategy:    { type: 'string', description: 'Strategy name or source dataset' },
        iterations:  { type: 'number', description: 'Number of Monte Carlo runs (1,000 - 10,000)', default: 2000 },
        method:      { type: 'string', enum: ['standard', 'block_bootstrap'], default: 'block_bootstrap' },
        blockSize:   { type: 'number', description: 'Block size for block bootstrap', default: 5 },
        sizingModel: { type: 'string', enum: ['fixed_lot', 'fixed_fractional', 'volatility_scaled'], default: 'fixed_lot' },
      },
    },
  },
  {
    name: 'nt_draw_level',
    description: 'Plot S/R levels, Midnight Open, HOD/LOD, or FVG boxes directly onto NT8 charts via native Draw.* methods',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:    { type: 'string', description: 'Instrument symbol' },
        shapeType: { type: 'string', enum: ['line', 'rectangle', 'text'], default: 'line' },
        tag:       { type: 'string', description: 'Drawing tag ID' },
        price1:    { type: 'number', description: 'Primary price level' },
        price2:    { type: 'number', description: 'Secondary price level (for rectangles)' },
        time1:     { type: 'string', description: 'UTC Start time' },
        time2:     { type: 'string', description: 'UTC End time' },
        label:     { type: 'string', description: 'Text label' },
        color:     { type: 'string', description: 'Hex color string (e.g. #FF0000)', default: '#0000FF' },
      },
      required: ['symbol', 'tag', 'price1'],
    },
  },
  {
    name: 'nt_indicator_values',
    description: 'Retrieve calculated historical or live indicator values (SMA, EMA, VWAP, ATR, Daily NY Levels) for a symbol or running strategy',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:        { type: 'string', description: 'Instrument symbol' },
        indicatorName: { type: 'string', description: 'Indicator class name (e.g. SMA, VWAP, ATR)' },
        period:        { type: 'number', description: 'Indicator period setting', default: 14 },
        barsBack:      { type: 'number', description: 'Number of historical values to return', default: 20 },
      },
      required: ['symbol', 'indicatorName'],
    },
  },
  {
    name: 'nt_script_execute',
    description: 'Execute a sandboxed C# utility snippet or pre-approved helper function inside NinjaTrader',
    inputSchema: {
      type: 'object',
      properties: {
        codeSnippet: { type: 'string', description: 'C# code snippet to execute' },
      },
      required: ['codeSnippet'],
    },
  },

  // ─── Phase 5 SSE Stream & Phase 6-8 Expansion Tools ───────────────────
  {
    name: 'nt_subscribe',
    description: 'Subscribe to NinjaTrader Hub (ninjatrader_hub.py) or McpBridge real-time SSE event stream for fills, RiskGuard FSM state transitions, and strategy errors',
    inputSchema: {
      type: 'object',
      properties: {
        hubUrl: { type: 'string', description: 'NinjaTrader Hub URL or local broadcast bus', default: 'http://127.0.0.1:7891' },
      },
    },
  },
  {
    name: 'nt_portfolio_backtest',
    description: 'Run simultaneous multi-symbol portfolio backtests with correlation matrix calculation and capital allocation metrics',
    inputSchema: {
      type: 'object',
      properties: {
        symbols:  { type: 'array', items: { type: 'string' }, description: 'List of symbols (e.g. ["NQ 09-26", "ES 09-26", "CL 09-26"])' },
        strategy: { type: 'string', description: 'Strategy class name' },
        from:     { type: 'string', description: 'UTC Start date YYYY-MM-DD' },
        to:       { type: 'string', description: 'UTC End date YYYY-MM-DD' },
      },
      required: ['symbols', 'strategy'],
    },
  },
  {
    name: 'nt_synthetic_data',
    description: 'Generate stress scenario datasets (e.g. 2020 COVID shock, 2008 GFC, volatility scaling) to evaluate strategy robustness',
    inputSchema: {
      type: 'object',
      properties: {
        scenario: { type: 'string', enum: ['2020_covid_shock', '2008_gfc_crash', 'high_volatility_regime', 'custom_gap_shock'], default: '2020_covid_shock' },
        symbol:   { type: 'string', description: 'Target instrument symbol', default: 'NQ 09-26' },
      },
    },
  },
  {
    name: 'nt_signal_backtest',
    description: 'Lightweight "what-if" testing of entry/exit signal rules without full NinjaScript strategy overhead',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:     { type: 'string', description: 'Instrument symbol' },
        entryRule:  { type: 'string', description: 'Signal entry rule expression' },
        exitRule:   { type: 'string', description: 'Signal exit rule expression' },
        timeframe:  { type: 'string', default: '5m' },
      },
      required: ['symbol'],
    },
  },
  {
    name: 'nt_schedule',
    description: 'Register time-based or event-based scheduled tasks inside NinjaTrader (e.g., re-optimize weekly, flatten at market close)',
    inputSchema: {
      type: 'object',
      properties: {
        cronExpression: { type: 'string', description: '5-field cron expression', default: '0 18 * * 0' },
        taskAction:     { type: 'string', description: 'Task action name or endpoint', default: 'reoptimize' },
      },
    },
  },
  {
    name: 'nt_trade_journal',
    description: 'Full CRUD operations on local trade journal repository with macro window auto-tagging and export to TraderSync/TradesViz',
    inputSchema: {
      type: 'object',
      properties: {
        action:  { type: 'string', enum: ['list', 'add', 'tag', 'export'], default: 'list' },
        format:  { type: 'string', enum: ['json', 'csv'], default: 'json' },
      },
    },
  },
  {
    name: 'nt_alert',
    description: 'Create persistent price, indicator, or strategy alerts with local email/SMS/webhook notifications',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:    { type: 'string', description: 'Instrument symbol' },
        condition: { type: 'string', description: 'Alert trigger condition' },
        action:    { type: 'string', enum: ['flatten', 'webhook', 'notify'], default: 'webhook' },
      },
      required: ['symbol', 'condition'],
    },
  },
  {
    name: 'nt_riskguard_config',
    description: 'Dynamic configuration of trailing drawdown limits, volatility-based position caps, and time-of-day blackout windows',
    inputSchema: {
      type: 'object',
      properties: {
        trailingDrawdown: { type: 'number', description: 'Max trailing drawdown limit ($)' },
        maxPositionCap:   { type: 'number', description: 'Max contracts position cap' },
      },
    },
  },
  {
    name: 'nt_compliance_report',
    description: 'One-click generation of prop firm / broker compliance reports (daily P&L, trade log, max position exposure)',
    inputSchema: {
      type: 'object',
      properties: {
        account: { type: 'string', description: 'Target account name', default: 'Sim101' },
      },
    },
  },
  {
    name: 'nt_multi_account_orchestrator',
    description: 'Coordinated order routing and hedging across multiple accounts',
    inputSchema: {
      type: 'object',
      properties: {
        action:   { type: 'string', enum: ['sync_hedge', 'rebalance', 'group_flatten'], default: 'sync_hedge' },
        accounts: { type: 'array', items: { type: 'string' }, description: 'List of target account names' },
      },
    },
  },

  // ─── Phase 2: strategy authoring / compile / backtest ─────────────────
  {
    name: 'nt_list_strategies',
    description: 'List NinjaScript strategy source files in bin\\Custom\\Strategies (name, size, last modified)',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_strategy_source',
    description: 'Read the NinjaScript source of one strategy by class/file name',
    inputSchema: {
      type: 'object',
      properties: { name: { type: 'string', description: 'Strategy class/file name (no .cs)' } },
      required: ['name'],
    },
  },
  {
    name: 'nt_create_strategy',
    description: 'Write a NinjaScript strategy (.cs) into bin\\Custom\\Strategies. Pass full NinjaScript C# source. Call nt_compile afterward to build + hot-load it.',
    inputSchema: {
      type: 'object',
      properties: {
        name:      { type: 'string', description: 'Strategy class/file name (no .cs). Must match the class name in source.' },
        source:    { type: 'string', description: 'Full NinjaScript C# source (namespace NinjaTrader.NinjaScript.Strategies, class : Strategy)' },
        overwrite: { type: 'boolean', description: 'Overwrite if it already exists', default: true },
      },
      required: ['name', 'source'],
    },
  },
  {
    name: 'nt_compile',
    description: 'Recompile all NinjaScript in-process (Roslyn, hot-swap, no NT8 restart). Returns success + any compile errors/warnings. Run after nt_create_strategy.',
    inputSchema: {
      type: 'object',
      properties: { debug: { type: 'boolean', description: 'Emit a debug build', default: false } },
    },
  },
  {
    name: 'nt_backtest',
    description: 'Run a backtest of a compiled strategy via the NT8 Strategy Analyzer over a configurable symbol, UTC date range, timeframe, and parameters.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy:    { type: 'string', description: 'Strategy class name (must be compiled first)' },
        symbol:      { type: 'string', description: 'Instrument (e.g. GC 08-26, NQ, ES)' },
        from:        { type: 'string', description: 'UTC Start date YYYY-MM-DD' },
        to:          { type: 'string', description: 'UTC End date YYYY-MM-DD' },
        period:      { type: 'string', enum: ['Minute', 'Day', 'Tick', 'Second', 'Range', 'Volume'], description: 'Bars period type', default: 'Minute' },
        periodValue: { type: 'number', description: 'Bars period value (e.g. 5 for 5m)', default: 1 },
        params:      { type: 'object', description: 'Strategy parameter overrides { paramName: value }' },
        maxTrades:   { type: 'number', description: 'Max trades to include in the response', default: 50 },
        timeoutSec:  { type: 'number', description: 'Server-side wait for the run to finish', default: 180 },
      },
      required: ['strategy', 'symbol'],
    },
  },
  {
    name: 'nt_strategy_status',
    description: 'List strategies NT8 is currently running (enabled on an account): type, state (Realtime/Historical/etc.), account, instrument, timeframe, market position and quantity. Read-only.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_deploy_strategy',
    description: 'Deploy a compiled strategy onto an OPEN chart and enable it (SIM-first). A live account requires confirmLive:true.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy:    { type: 'string', description: 'Compiled strategy class name (e.g. PathSignatureUnion)' },
        instrument:  { type: 'string', description: 'Instrument of an OPEN chart to deploy onto (e.g. NQ 09-26)' },
        account:     { type: 'string', description: 'Account name', default: 'Sim101' },
        params:      { type: 'object', description: 'Strategy parameter overrides { name: value }' },
        enable:      { type: 'boolean', description: 'Enable after adding', default: true },
        confirmLive: { type: 'boolean', description: 'Required to deploy to a non-sim (live) account', default: false },
      },
      required: ['strategy', 'instrument'],
    },
  },
  {
    name: 'nt_stop_strategy',
    description: 'Stop running strategies: disable and remove them from the chart, and flatten open positions.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy: { type: 'string', description: 'Strategy class name to stop (omit = all)' },
        account:  { type: 'string', description: 'Limit to this account (omit = all)' },
        flatten:  { type: 'boolean', description: 'Flatten open position via offsetting market order', default: true },
      },
    },
  },
  {
    name: 'nt_set_strategy_param',
    description: 'Change inputs on a RUNNING strategy live, with no restart.',
    inputSchema: {
      type: 'object',
      properties: {
        strategy: { type: 'string', description: 'Strategy class name (omit = all running)' },
        account:  { type: 'string', description: 'Limit to this account (omit = all)' },
        params:   { type: 'object', description: 'Inputs to set' },
      },
      required: ['params'],
    },
  },
];

// ─── HTTP Client to NT8 AddOn ──────────────────────────────────────────
function ntFetch(endpoint, method = 'GET', body = null, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint, NT8_BASE);
    const options = {
      method,
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      headers: {
        'Accept': 'application/json',
        'X-NT8-MCP-Version': SERVER_VERSION,
      },
      timeout: timeoutMs,
    };

    if (NT8_MCP_TOKEN) {
      options.headers['Authorization'] = `Bearer ${NT8_MCP_TOKEN}`;
    }

    if (body) {
      const data = JSON.stringify(body);
      options.headers['Content-Type'] = 'application/json';
      options.headers['Content-Length'] = Buffer.byteLength(data);
    }

    const req = httpRequest(options, (res) => {
      let chunks = '';
      res.on('data', (chunk) => { chunks += chunk; });
      res.on('end', () => {
        try {
          const parsed = JSON.parse(chunks);
          resolve({ status: res.statusCode, data: parsed });
        } catch {
          resolve({ status: res.statusCode, data: chunks });
        }
      });
    });

    req.on('error', (err) => reject(new Error(`NT8 connection failed: ${err.message}`)));
    req.on('timeout', () => { req.destroy(); reject(new Error('NT8 timeout')); });

    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

// ─── MCP Protocol ──────────────────────────────────────────────────────
const rl = createInterface({ input: process.stdin });

function sendMessage(msg) {
  const str = JSON.stringify(msg);
  process.stdout.write(str + '\n');
}

function sendError(id, code, message) {
  sendMessage({ jsonrpc: '2.0', id, error: { code, message } });
}

function sendResult(id, result) {
  sendMessage({ jsonrpc: '2.0', id, result });
}

// ─── Tool Handlers ──────────────────────────────────────────────────────
async function handleToolCall(name, args) {
  switch (name) {
    case 'nt_health': {
      try {
        const res = await ntFetch('/api/health');
        return {
          status: res.status === 200 ? 'connected' : 'error',
          server_version: SERVER_VERSION,
          nt8_bridge: res.data,
          timestamp_utc: new Date().toISOString()
        };
      } catch (err) {
        return {
          status: 'disconnected',
          server_version: SERVER_VERSION,
          error: err.message,
          timestamp_utc: new Date().toISOString()
        };
      }
    }


    case 'nt_accounts': {
      const res = await ntFetch('/api/account');
      return res.data;
    }

    case 'nt_positions': {
      const res = await ntFetch('/api/positions');
      return Array.isArray(res.data) ? res.data : [];
    }

    case 'nt_orders': {
      const params = new URLSearchParams();
      if (args.account) params.append('account', args.account);
      if (args.limit) params.append('limit', String(args.limit));
      if (args.offset) params.append('offset', String(args.offset));
      const res = await ntFetch(`/api/orders?${params}`);
      return res.data;
    }

    case 'nt_place_order': {
      const res = await ntFetch('/api/order', 'POST', args);
      return res.data;
    }

    case 'nt_place_oco_order': {
      const res = await ntFetch('/api/order/oco', 'POST', args);
      return res.data;
    }

    case 'nt_place_atm_order': {
      const res = await ntFetch('/api/order/atm', 'POST', args);
      return res.data;
    }

    case 'nt_change_order': {
      const res = await ntFetch('/api/order/change', 'POST', args);
      return res.data;
    }

    case 'nt_cancel_order': {
      const res = await ntFetch('/api/order/cancel', 'POST', { orderId: args.orderId, ocoId: args.ocoId });
      return res.data;
    }

    case 'nt_cancel_all_orders': {
      const res = await ntFetch('/api/orders/cancel-all', 'POST');
      return res.data;
    }

    case 'nt_close_position': {
      const res = await ntFetch('/api/position/close', 'POST', args);
      return res.data;
    }

    case 'nt_emergency_flatten': {
      const res = await ntFetch('/api/emergency-flatten', 'POST', args);
      return res.data;
    }

    case 'nt_quote': {
      const res = await ntFetch(`/api/quote?symbol=${encodeURIComponent(args.symbol)}`);
      return res.data;
    }

    case 'nt_bars': {
      const params = new URLSearchParams({
        symbol: args.symbol,
        period: args.period || 'Minute',
        periodValue: String(args.periodValue || 1),
        count: String(args.count || 100),
        offset: String(args.offset || 0),
      });
      const res = await ntFetch(`/api/bars?${params}`);
      return res.data;
    }

    case 'nt_search': {
      const res = await ntFetch(`/api/search?query=${encodeURIComponent(args.query)}`);
      return res.data;
    }

    case 'nt_export_bars': {
      const timeoutMs = ((args.timeoutSec || 180) + 30) * 1000;
      const res = await ntFetch('/api/bars/export', 'POST', {
        symbol: args.symbol, from: args.from, to: args.to,
        period: args.period || 'Minute', periodValue: args.periodValue || 1,
        merge: args.merge || 'DoNotMerge',
        timeoutSec: args.timeoutSec || 180,
      }, timeoutMs);
      return res.data;
    }

    case 'nt_get_export': {
      const res = await ntFetch(`/api/export?name=${encodeURIComponent(args.name)}`, 'GET', null, 60000);
      return res.data;
    }

    case 'nt_capture_chart': {
      const res = await ntFetch(`/api/chart/capture?symbol=${encodeURIComponent(args.symbol || '')}`);
      return res.data;
    }

    case 'nt_chart_snapshot': {
      const res = await ntFetch('/api/chart/snapshot', 'POST', args);
      return res.data;
    }

    case 'nt_trade_chart': {
      const res = await ntFetch('/api/chart/trade', 'POST', args);
      return res.data;
    }


    case 'nt_open_chart': {
      const res = await ntFetch('/api/chart/open', 'POST', args);
      return res.data;
    }

    case 'nt_get_logs': {
      const params = new URLSearchParams({
        tab: args.tab || 'Output',
        lines: String(args.lines || 100),
      });
      const res = await ntFetch(`/api/logs?${params}`);
      return res.data;
    }

    case 'nt_fill_events': {
      const params = new URLSearchParams();
      if (args.account) params.append('account', args.account);
      if (args.count) params.append('count', String(args.count));
      if (args.offset) params.append('offset', String(args.offset));
      const res = await ntFetch(`/api/events/fills?${params}`);
      return res.data;
    }

    case 'nt_inspect_strategy': {
      const res = await ntFetch(`/api/strategy/inspect?name=${encodeURIComponent(args.name || 'LIST')}`);
      return res.data;
    }

    case 'nt_riskguard_state': {
      const params = new URLSearchParams();
      if (args.account) params.append('account', args.account);
      if (args.instrument) params.append('instrument', args.instrument);
      const res = await ntFetch(`/api/riskguard/fsm-state?${params}`);
      return res.data;
    }

    case 'nt_copier_config': {
      const res = await ntFetch('/api/copier/config', 'POST', args);
      return res.data;
    }

    case 'nt_prop_limits': {
      const res = await ntFetch('/api/prop/limits', 'POST', args);
      return res.data;
    }

    case 'nt_extract_trades': {
      const params = new URLSearchParams({
        format: args.format || 'json',
        limit: String(args.limit || 100),
      });
      if (args.account) params.append('account', args.account);
      if (args.from) params.append('from', args.from);
      if (args.to) params.append('to', args.to);
      const res = await ntFetch(`/api/trades/extract?${params}`);
      return res.data;
    }

    case 'nt_monte_carlo': {
      const res = await ntFetch('/api/trades/monte-carlo', 'POST', args);
      return res.data;
    }

    case 'nt_draw_level': {
      const res = await ntFetch('/api/chart/draw', 'POST', args);
      return res.data;
    }

    case 'nt_indicator_values': {
      const params = new URLSearchParams({
        symbol: args.symbol,
        indicatorName: args.indicatorName,
        period: String(args.period || 14),
        barsBack: String(args.barsBack || 20),
      });
      const res = await ntFetch(`/api/indicator/values?${params}`);
      return res.data;
    }

    case 'nt_script_execute': {
      const res = await ntFetch('/api/script/execute', 'POST', args);
      return res.data;
    }

    case 'nt_subscribe': {
      return { status: 'subscribed', hubUrl: args.hubUrl || 'http://127.0.0.1:7891', sseEndpoint: 'http://127.0.0.1:7890/api/events/stream' };
    }

    case 'nt_portfolio_backtest': {
      const res = await ntFetch('/api/backtest/portfolio', 'POST', args, 300000);
      return res.data;
    }

    case 'nt_synthetic_data': {
      const res = await ntFetch('/api/data/synthetic', 'POST', args);
      return res.data;
    }

    case 'nt_signal_backtest': {
      const res = await ntFetch('/api/backtest/signal', 'POST', args);
      return res.data;
    }

    case 'nt_schedule': {
      const res = await ntFetch('/api/schedule/task', 'POST', args);
      return res.data;
    }

    case 'nt_trade_journal': {
      const res = await ntFetch('/api/trades/journal', 'POST', args);
      return res.data;
    }

    case 'nt_alert': {
      const res = await ntFetch('/api/alert/create', 'POST', args);
      return res.data;
    }

    case 'nt_riskguard_config': {
      const res = await ntFetch('/api/riskguard/config', 'POST', args);
      return res.data;
    }

    case 'nt_compliance_report': {
      const params = new URLSearchParams();
      if (args.account) params.append('account', args.account);
      const res = await ntFetch(`/api/compliance/report?${params}`);
      return res.data;
    }

    case 'nt_multi_account_orchestrator': {
      const res = await ntFetch('/api/orchestrator/multi-account', 'POST', args);
      return res.data;
    }

    case 'nt_strategy_status': {
      const res = await ntFetch('/api/strategy/running', 'GET', null, 30000);
      return res.data;
    }

    case 'nt_deploy_strategy': {
      const res = await ntFetch('/api/strategy/deploy', 'POST', args, 40000);
      return res.data;
    }

    case 'nt_stop_strategy': {
      const res = await ntFetch('/api/strategy/stop', 'POST', args, 40000);
      return res.data;
    }

    case 'nt_set_strategy_param': {
      const res = await ntFetch('/api/strategy/param', 'POST', args, 20000);
      return res.data;
    }

    // ─── Phase 2 ────────────────────────────────────────────────────────
    case 'nt_list_strategies': {
      const res = await ntFetch('/api/strategies');
      return res.data;
    }

    case 'nt_strategy_source': {
      const res = await ntFetch(`/api/strategy/source?name=${encodeURIComponent(args.name)}`);
      return res.data;
    }

    case 'nt_create_strategy': {
      const res = await ntFetch('/api/strategy/create', 'POST', {
        name: args.name,
        source: args.source,
        overwrite: args.overwrite !== false,
      });
      return res.data;
    }

    case 'nt_compile': {
      try {
        await ntFetch('/api/compile', 'POST', { debug: !!args.debug }, 30000);
      } catch {
        // expected on success (connection reset by hot-swap)
      }
      for (let i = 0; i < 15; i++) {
        await new Promise((r) => setTimeout(r, 1500));
        try {
          const res = await ntFetch('/api/compile/result', 'GET', null, 5000);
          if (res.status === 200 && res.data && typeof res.data === 'object') return res.data;
        } catch { /* bridge reloading */ }
      }
      return { error: 'compile result unavailable' };
    }

    case 'nt_backtest': {
      const res = await ntFetch('/api/backtest', 'POST', args, 300000);
      return res.data;
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

// ─── Message Dispatch ──────────────────────────────────────────────────
rl.on('line', async (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return;
  }

  const { id, method, params } = msg;

  try {
    switch (method) {
      case 'initialize': {
        sendResult(id, {
          protocolVersion: MCP_PROTOCOL_VERSION,
          capabilities: { tools: {} },
          serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
        });
        break;
      }

      case 'notifications/initialized': {
        break;
      }

      case 'tools/list': {
        sendResult(id, { tools: TOOLS });
        break;
      }

      case 'tools/call': {
        const { name, arguments: args } = params;
        const result = await handleToolCall(name, args || {});
        sendResult(id, {
          content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
        });
        break;
      }

      default: {
        sendError(id, -32601, `Method not found: ${method}`);
      }
    }
  } catch (err) {
    sendError(id, -32603, err.message);
  }
});

// ─── Startup ────────────────────────────────────────────────────────────
console.error(`[nt-mcp] Server v${SERVER_VERSION} started — NT8 at ${NT8_BASE}`);
console.error('[nt-mcp] Waiting for MCP messages on stdin...');
