/**
 * lib/tools.js — the MCP tool schemas, as data.
 *
 * WHY THIS IS ITS OWN MODULE, and not part of nt-mcp-server.js:
 * importing the server starts its stdin readline loop, so a test that reached in
 * for a value defined there would hang. `lib/copier-config-request.js` was split
 * out for exactly that reason and says so in its own header. This is the same move,
 * applied to the tool schemas — and the same move that made nt8-mcp-bridge's
 * account resolver testable (`P1-90`): put the thing you need to assert on
 * somewhere with no side effects, so a test can assert on the REAL object instead
 * of grepping source text.
 *
 * What that buys, concretely: `tests/tool-schema.test.js` can check that no tool
 * advertises a `default:` for an account. That mattered — `P1-73` shipped a schema
 * default that the receiver merged into stored config, and `P1-91` was four
 * account defaults still advertising a fallback the addon had stopped honouring.
 */
export const TOOLS = [
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
      required: ['symbol', 'action', 'quantity', 'idempotencyKey', 'account'],
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
        account:        { type: 'string', description: 'Account name' },
        confirmLive:    { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        limitPrice:     { type: 'number', description: 'Profit target limit price' },
        stopPrice:      { type: 'number', description: 'Stop loss price' },
        action:         { type: 'string', enum: ['buy', 'sell'], description: 'Primary entry direction' },
        idempotencyKey: { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'quantity', 'limitPrice', 'stopPrice', 'action', 'idempotencyKey', 'account'],
    },
  },
  {
    name: 'nt_place_atm_order',
    description: 'Place a bracket order with server-side ATM strategy (stop loss, profit target, auto-breakeven, trailing, partials). Supports 8 strategies: FixedTicks, AtrAdaptive, SwingPoint, DrawdownShield, ScaledRunner, VolatilityScaled, SessionAdaptive, KellyOptimal. Auto-selects strategy per instrument if omitted.',
    inputSchema: {
      type: 'object',
      properties: {
        symbol:              { type: 'string', description: 'Ticker (e.g. NQ 09-26)' },
        action:              { type: 'string', enum: ['buy', 'sell'], description: 'Direction' },
        quantity:            { type: 'number', description: 'Contracts (overridden by VolatilityScaled/KellyOptimal if riskPerTrade set)' },
        strategyName:        { type: 'string', description: 'ATM strategy: FixedTicks, AtrAdaptive, SwingPoint, DrawdownShield, ScaledRunner, VolatilityScaled, SessionAdaptive, KellyOptimal. Omit for auto-select per instrument.' },
        stopTicks:           { type: 'number', description: 'Stop loss distance in ticks (FixedTicks, DrawdownShield, ScaledRunner, SessionAdaptive)' },
        targetTicks:         { type: 'number', description: 'Profit target distance in ticks (FixedTicks, DrawdownShield, ScaledRunner, SessionAdaptive)' },
        atrMultiplierSL:     { type: 'number', description: 'ATR multiplier for stop loss (AtrAdaptive, VolatilityScaled, KellyOptimal)', default: 1.5 },
        atrMultiplierTP:     { type: 'number', description: 'ATR multiplier for target (AtrAdaptive, VolatilityScaled, KellyOptimal)', default: 2.5 },
        atrPeriod:           { type: 'number', description: 'ATR calculation period', default: 14 },
        swingLookbackBars:   { type: 'number', description: 'Bars to look back for swing point (SwingPoint)', default: 5 },
        swingBufferTicks:    { type: 'number', description: 'Buffer ticks past swing point (SwingPoint)', default: 4 },
        breakevenTriggerTicks: { type: 'number', description: 'Ticks profit before moving stop to breakeven (DrawdownShield, ScaledRunner)', default: 12 },
        breakevenOffsetTicks:  { type: 'number', description: 'Ticks past entry for breakeven stop (DrawdownShield)', default: 2 },
        partialProfitPct:    { type: 'number', description: 'Fraction to take partial profit (DrawdownShield)', default: 0.50 },
        trailMultiplier:     { type: 'number', description: 'Trailing stop multiplier on stopTicks (ScaledRunner)', default: 2.0 },
        riskPerTrade:        { type: 'number', description: 'Max $ risk per trade (VolatilityScaled, KellyOptimal)', default: 200 },
        kellyFraction:       { type: 'number', description: 'Fractional Kelly multiplier (KellyOptimal)', default: 0.25 },
        winRate:             { type: 'number', description: 'Assumed win rate for Kelly (KellyOptimal)', default: 0.55 },
        avgRR:               { type: 'number', description: 'Assumed avg risk-reward for Kelly (KellyOptimal)', default: 2.0 },
        account:             { type: 'string', description: 'Target account' },
        confirmLive:         { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        idempotencyKey:      { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'action', 'quantity', 'idempotencyKey', 'account'],
    },
  },
  {
    name: 'nt_atm_bracket_status',
    description: 'Query active ATM bracket status by bracketId, or list all active brackets',
    inputSchema: {
      type: 'object',
      properties: {
        bracketId: { type: 'string', description: 'Bracket ID from nt_place_atm_order response. Omit to list all active brackets.' },
      },
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
    description:
      'Read or modify TradeCopierEngine relationships and groups: leader/follower ratios, ' +
      'the per-ticker ratio matrix, symbol mappings, slippage quarantine, and group membership. ' +
      'action=get is a pure read (HTTP GET). Writes send ONLY the fields you name -- the engine ' +
      'merges, so an omitted field keeps its stored value and is never reset to a default.',
    inputSchema: {
      type: 'object',
      properties: {
        // NOTE: no `default:` on any value field. The engine merges, so a default
        // materialised into the request would OVERWRITE stored config (P1-73).
        action: {
          type: 'string',
          // WARNING: P1-72, REGRESSED and re-fixed 2026-08-13. `quarantine` and
          // `unquarantine` were advertised here and the addon answers
          // UNKNOWN_COPIER_ACTION for both -- measured against the live box, not
          // inferred. Quarantining is done through `set` with `isQuarantined`, which is
          // what the browser page posts. Keep this list identical to
          // McpBridgeAddOn.CopierConfig's knownActions whitelist; a case in
          // tests/tool-schema.test.js pins the two together.
          enum: [
            'get', 'get_groups',
            'set', 'update', 'remove',
            'set_mode',
            'set_group', 'remove_group', 'add_follower_to_group', 'remove_follower_from_group',
          ],
          description: 'Default get. An unrecognised action is REFUSED, not treated as a read.',
        },
        leaderAccount:   { type: 'string', description: 'Leader account name. Required for every relationship write.' },
        followerAccount: { type: 'string', description: 'Follower account name. Required for every relationship write; never guessed.' },
        quantityRatio:   { type: 'number', description: 'Quantity scaling ratio, e.g. 2 copies 1 leader lot as 2' },
        autoConversion:  { type: 'boolean', description: 'Auto Mini <-> Micro symbol conversion. ⚠️ With ratio 1.0 this DROPS a 1-lot micro copy, because 1 MNQ translated to NQ rounds below one contract.' },
        sizingMode:      { type: 'string', enum: ['QuantityRatio', 'FixedLot', 'PerTickerMatrix'], description: 'PerTickerMatrix uses perTickerRatios' },
        perTickerRatios: { type: 'object', description: 'Per-instrument ratio matrix, e.g. {"NQ": 2, "ES": 1}. Case-insensitive keys.' },
        customSymbolMappings: { type: 'object', description: 'Leader-symbol -> follower-symbol overrides, e.g. {"MNQ": "NQ"}' },
        maxSlippageTicks: { type: 'number', description: 'Adverse entry slippage that quarantines the relationship. 0 disables.' },
        dailyLossLimit:  { type: 'number', description: 'Per-relationship daily loss limit ($)' },
        maxPositionSize: { type: 'number', description: 'Cap on the follower position size' },
        isEnabled:       { type: 'boolean', description: 'Whether the relationship copies at all' },
        stealthMode:     { type: 'boolean', description: 'Stealth submission' },
        mode:            { type: 'string', enum: ['Executions', 'Orders'], description: 'Copy TRIGGER source for one relationship. NOT the global copier mode -- that is copierMode.' },
        copierMode:      { type: 'string', enum: ['live', 'shadow', 'disabled'], description: 'GLOBAL copier mode, for action=set_mode. shadow logs the order it would have sent and submits nothing; disabled is off. Entering live runs the copier preflight and is REFUSED if a follower does not resolve. Affects EVERY relationship.' },
        isQuarantined:   { type: 'boolean', description: 'Quarantine state. false with action=set is how a quarantine is RELEASED -- there is no quarantine/unquarantine action (P1-72).' },
        armedForLive:    { type: 'boolean', description: 'Arm for live copying. true REQUIRES confirmLive: true.' },
        confirmLive:     { type: 'boolean', description: 'Explicit confirmation for arming. Real orders on a real account.' },
        quarantineReason: { type: 'string', description: 'Free text stored alongside a quarantine' },
        groupName:       { type: 'string', description: 'Group name. Required for every group action.' },
        followerAccounts: { type: 'array', items: { type: 'string' }, description: 'Group follower list, for set_group' },
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
        action:    { type: 'string', enum: ['flatten', 'webhook', 'notify'] },
      },
      required: ['symbol', 'condition', 'action'],
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
        account: { type: 'string', description: 'Target account name' },
      },
      required: ['account'],
    },
  },
  {
    name: 'nt_multi_account_orchestrator',
    description: 'Coordinated order routing and hedging across multiple accounts',
    inputSchema: {
      type: 'object',
      properties: {
        action:   { type: 'string', enum: ['sync_hedge', 'rebalance', 'group_flatten'] },
        accounts: { type: 'array', items: { type: 'string' }, description: 'List of target account names' },
      },
      required: ['action'],
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
        account:     { type: 'string', description: 'Account name' },
        params:      { type: 'object', description: 'Strategy parameter overrides { name: value }' },
        enable:      { type: 'boolean', description: 'Enable after adding', default: true },
        confirmLive: { type: 'boolean', description: 'Required to deploy to a non-sim (live) account', default: false },
      },
      required: ['strategy', 'instrument', 'account'],
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
