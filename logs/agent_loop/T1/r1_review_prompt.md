# TICKET T1: P1-91: schema defaults supply arguments the caller never sent -- four account defaults the addon now refuses, and two `action` defaults that fall through to something which moves a position

## Defect the patch claims to fix
`lib/tools.js` declares `default: 'Sim101'` on the `account` property of four tools -- `nt_place_oco_order`, `nt_place_atm_order`, `nt_compliance_report` and `nt_deploy_strategy`. TWO OF THEM PLACE ORDERS. Since P1-90 the NT8 addon refuses a missing or unresolvable account rather than substituting one, so (a) the contract now misdescribes the addon, telling callers that omitting `account` targets Sim101, and (b) any MCP client that materialises schema defaults would inject `Sim101` into an order call, at which point the addon resolves a name the caller never sent and the refusal is never reached.

Separately, none of the five tools whose handler refuses a missing account declares `account` in its `required` array -- including `nt_place_order`, which carries no default and so was not among the four originally filed. A field the handler cannot proceed without, left out of `required`, fails late and further from its cause.

WIDENED after the first loop run. The acceptance test that checks the CLASS rather than the four filed instances found two more, on `action`:
  nt_alert.action                       default 'webhook'    enum [flatten, webhook, notify]
  nt_multi_account_orchestrator.action  default 'sync_hedge' enum [sync_hedge, rebalance, group_flatten]
`sync_hedge` adjusts positions ACROSS ACCOUNTS. An omitted `action` doing that is P1-90's class: something consequential happening that the caller never named.

Two OTHER action defaults are SAFE and must be left alone -- nt_prop_limits ('get') and nt_trade_journal ('list') both default to the READ, which is the fail-closed direction. The first draft of the test forbade any `action` default and would have had you delete those two; it was corrected. The rule is about which way the default falls, not whether one exists.

## Required change
In `lib/tools.js`:

1. DELETE the `default: 'Sim101'` from the `account` property of `nt_place_oco_order`, `nt_place_atm_order`, `nt_compliance_report` and `nt_deploy_strategy`. Keep the `type` and the `description`. Do not add a replacement default of any kind.

2. Add `'account'` to the `required` array of these five tools: `nt_place_order`, `nt_place_oco_order`, `nt_place_atm_order`, `nt_compliance_report`, `nt_deploy_strategy`.
   - APPEND to the existing array; do not rewrite it. Losing `idempotencyKey` from an order tool would admit duplicate orders, which is worse than the defect being fixed, and a test pins those entries.
   - `nt_compliance_report` has NO `required` array today. Add one containing exactly `['account']`.

3. `nt_alert`: DELETE `default: 'webhook'` from `action`, and append `'action'` to its required array (which already holds symbol and condition). An alert must say what it does; its enum includes `flatten`.

4. `nt_multi_account_orchestrator`: DELETE `default: 'sync_hedge'` from `action`, and add a required array of exactly `['action']` -- it has none today. Its enum includes `group_flatten`.

5. Do NOT touch `nt_prop_limits.action` (default 'get') or `nt_trade_journal.action` (default 'list'). Both default to a READ, which is correct and is what the test permits. Deleting them would make the tools worse to satisfy a misread of the rule.

6. Change NOTHING else. Do not touch the other nine tools that take an `account`. Do not add a `default:` to any field. Do not rename a tool or a property -- an MCP tool name is a published contract. Preserve the aligned-property formatting inside `inputSchema.properties`.

## Mechanical gates already passed
static: 7 block(s) well-formed; compile: build succeeded; test: no regressions; 40 passed, 0 failed, 3 expected failure(s) now green; all 3 acceptance test(s) green; lock-scope: no lock primitive in javascript

## SETTLED DECISIONS - AUTHORITATIVE, DO NOT RE-LITIGATE
The arbiter has already decided these. They SUPERSEDE the ticket text wherever they conflict. Do NOT raise a finding that contradicts one, and do not report directive-compliant code as a spec violation.

- P1-90: an account that cannot be resolved is REFUSED, never substituted. Do not propose restoring any fallback -- not to Sim101, not to the first connected account, not to a single-account convenience case. Fixed in nt8-mcp-bridge/addons/BridgeAccountResolver.cs and live-validated.
- A schema `default:` is treated as a possible injected argument, not as documentation. Do not argue that MCP clients never materialise defaults; the specification permits it, and whether a given client does is a property of that client, not of the contract.
- `account` stays OPTIONAL on tools whose handler treats its absence as 'all accounts' (nt_orders, nt_fill_events, nt_trade_chart, nt_riskguard_state, nt_extract_trades, nt_stop_strategy, nt_set_strategy_param). Only the five tools whose handler REFUSES without an account may declare it required. A passing test pins this boundary; do not argue for widening it.

## Implementer notes
- PlaceOrder: appended `'account'` to `required` so the handler's refusal of a missing account is enforced at schema time; no default existed here.
- PlaceOcoOrder: removed `default: 'Sim101'` from `account` (prevents MCP clients from injecting a synthetic Sim101 argument) and appended `'account'` to `required`.
- PlaceAtmOrder: removed `default: 'Sim101'` from `account` and appended `'account'` to `required`; left all ATM parameter defaults untouched.
- ComplianceReport: removed `default: 'Sim101'` from `account` and added `required: ['account']`, since this tool previously had no required array.
- DeployStrategy: removed `default: 'Sim101'` from `account` and appended `'account'` to `required`; left `enable`/`confirmLive` defaults untouched because they are not account/quantity/price/action defaults.
- Alert: removed `default: 'webhook'` from `action` (defaulting to `flatten`-capable `webhook` is consequential) and appended `'action'` to `required`.
- MultiAccountOrchestrator: removed `default: 'sync_hedge'` from `action` (it adjusts positions across accounts) and added `required: ['action']`.
- Intentionally left `nt_prop_limits.action` default `'get'` and `nt_trade_journal.action` default `'list'` unchanged, because both default to a read-only operation and are therefore fail-closed.
- No new dependencies, no renamed tools or properties, and no new defaults were added.


## REGION "PlaceOrder" (lib/tools.js)
### BEFORE
```javascript
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
```
### AFTER (proposed)
```javascript
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
```

## REGION "PlaceOcoOrder" (lib/tools.js)
### BEFORE
```javascript
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
```
### AFTER (proposed)
```javascript
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
```

## REGION "PlaceAtmOrder" (lib/tools.js)
### BEFORE
```javascript
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
        account:             { type: 'string', description: 'Target account', default: 'Sim101' },
        confirmLive:         { type: 'boolean', description: 'Explicit confirmation required when placing orders on live (non-Sim) accounts' },
        idempotencyKey:      { type: 'string', description: 'Mandatory UUID string to prevent duplicate orders' },
      },
      required: ['symbol', 'action', 'quantity', 'idempotencyKey'],
    },
```
### AFTER (proposed)
```javascript
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
```

## REGION "ComplianceReport" (lib/tools.js)
### BEFORE
```javascript
    name: 'nt_compliance_report',
    description: 'One-click generation of prop firm / broker compliance reports (daily P&L, trade log, max position exposure)',
    inputSchema: {
      type: 'object',
      properties: {
        account: { type: 'string', description: 'Target account name', default: 'Sim101' },
      },
    },
```
### AFTER (proposed)
```javascript
    name: 'nt_compliance_report',
    description: 'One-click generation of prop firm / broker compliance reports (daily P&L, trade log, max position exposure)',
    inputSchema: {
      type: 'object',
      properties: {
        account: { type: 'string', description: 'Target account name' },
      },
      required: ['account'],
    },
```

## REGION "DeployStrategy" (lib/tools.js)
### BEFORE
```javascript
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
```
### AFTER (proposed)
```javascript
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
```

## REGION "Alert" (lib/tools.js)
### BEFORE
```javascript
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
```
### AFTER (proposed)
```javascript
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
```

## REGION "MultiAccountOrchestrator" (lib/tools.js)
### BEFORE
```javascript
    name: 'nt_multi_account_orchestrator',
    description: 'Coordinated order routing and hedging across multiple accounts',
    inputSchema: {
      type: 'object',
      properties: {
        action:   { type: 'string', enum: ['sync_hedge', 'rebalance', 'group_flatten'], default: 'sync_hedge' },
        accounts: { type: 'array', items: { type: 'string' }, description: 'List of target account names' },
      },
    },
```
### AFTER (proposed)
```javascript
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
```