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

import { buildCopierConfigRequest } from './lib/copier-config-request.js';

// ─── Config ─────────────────────────────────────────────────────────────
const NT8_HOST = process.env.NT8_HOST || '127.0.0.1';
const NT8_PORT = parseInt(process.env.NT8_PORT || '7890', 10);
const NT8_BASE = `http://${NT8_HOST}:${NT8_PORT}`;
const NT8_MCP_TOKEN = process.env.NT8_MCP_TOKEN || '';

const SERVER_NAME = 'nt-mcp-server';
const SERVER_VERSION = '1.5.0';
const MCP_PROTOCOL_VERSION = '2024-11-05';


// ─── Tool Definitions ───────────────────────────────────────────────────
import { TOOLS } from './lib/tools.js';
import { summarise, forAccount, accountNames } from './lib/inventory-view.js';

// ─── HTTP Client to NT8 AddOn ──────────────────────────────────────────
function ntFetch(endpoint, method = 'GET', body = null, timeoutMs = 10000, retries = 3) {
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
      // Disable connection pooling — each request opens a fresh TCP connection.
      // Without this, stale keep-alive sockets from a previous bridge session
      // (e.g. after compile hot-swap or NT8 restart) cause ECONNRESET.
      agent: false,
    };

    if (NT8_MCP_TOKEN) {
      options.headers['Authorization'] = `Bearer ${NT8_MCP_TOKEN}`;
    }

    if (body) {
      const data = JSON.stringify(body);
      options.headers['Content-Type'] = 'application/json';
      options.headers['Content-Length'] = Buffer.byteLength(data);
    }

    const doRequest = (attempt) => {
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

      req.on('error', (err) => {
        if (attempt < retries && (err.code === 'ECONNRESET' || err.code === 'ECONNREFUSED')) {
          // Retry with fresh connection — bridge may have hot-swapped
          setTimeout(() => doRequest(attempt + 1), 500);
        } else {
          reject(new Error(`NT8 connection failed: ${err.message}`));
        }
      });
      req.on('timeout', () => { req.destroy(); reject(new Error('NT8 timeout')); });

      if (body) req.write(JSON.stringify(body));
      req.end();
    };

    doRequest(0);
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
        // P2-103. /api/riskguard/version had no tool either, and nt_health is where anyone
        // looks for "what is deployed". Folded in rather than given a tool of its own.
        //
        // ⚠️ It is fetched SEPARATELY and allowed to fail on its own. nt_health's job is to
        // answer whether the bridge is reachable; if the guard were unloaded and this threw,
        // a health check would report "disconnected" for a bridge that is perfectly fine --
        // an alarm firing on the wrong subject. A missing guard is reported AS a missing
        // guard, which is itself the answer someone running nt_health wants.
        let riskguard = null;
        try {
          const v = await ntFetch('/api/riskguard/version');
          riskguard = v.data;
        } catch (guardErr) {
          riskguard = { loaded: false, error: guardErr.message };
        }
        return {
          status: res.status === 200 ? 'connected' : 'error',
          server_version: SERVER_VERSION,
          nt8_bridge: res.data,
          riskguard,
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

    case 'nt_atm_bracket_status': {
      const params = new URLSearchParams();
      if (args.bracketId) params.append('bracketId', args.bracketId);
      const res = await ntFetch(`/api/order/atm/status?${params}`);
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
      // P3-111: `??` not `||`. `args.count || 100` turns an explicit count=0 into 100, and
      // periodValue=0 into 1 -- the wrapper silently answering a different question than the one
      // asked. Absent and zero are different inputs; the addon clamps zero to 1 and says so,
      // which is visible, where substituting 100 is not.
      const params = new URLSearchParams({
        symbol: args.symbol,
        period: args.period ?? 'Minute',
        periodValue: String(args.periodValue ?? 1),
        count: String(args.count ?? 100),
        offset: String(args.offset ?? 0),
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

    // P1-102. The route existed with no tool reaching it, so recovering a locked-out account
    // meant a raw curl with the token read off disk.
    //
    // ⚠️ NOTHING IS DEFAULTED HERE. `account` and `action` go through exactly as sent, so the
    // addon's resolver does the refusing (P1-90) and its whitelist does the rejecting (P1-102's
    // own fix). A `|| 'status'` here would be the wrapper quietly answering a different question
    // than the caller asked -- which is the defect this tool was built to expose, one layer up.
    // ⚠️ The positional signature is `ntFetch(endpoint, method, body)`. Written first as
    // `ntFetch(path, { method, body })` -- a fetch()-shaped options object -- which every other
    // POST handler in this file would have contradicted, and which the SCHEMA TEST CANNOT SEE:
    // it validates the advertised shape, not the call. Caught only by driving the server over
    // stdio, which is the same technique that validated P2-103 and the reason it is worth doing.
    case 'nt_lockout': {
      const res = await ntFetch('/api/lockout', 'POST', { account: args.account, action: args.action });
      return res.data;
    }

    // F-17. `status` is a GET so it stays a read all the way down; connect/disconnect POST.
    // The action is passed through verbatim rather than defaulted -- the addon owns the
    // whitelist and refuses anything outside it (P1-72).
    case 'nt_connection': {
      if (args.action === 'status' || args.action === undefined) {
        const res = await ntFetch('/api/connection', 'GET');
        return res.data;
      }
      const res = await ntFetch('/api/connection', 'POST', {
        action: args.action,
        name: args.name,
        provider: args.provider,
        confirmDisruptive: args.confirmDisruptive,
      });
      return res.data;
    }

    case 'nt_riskguard_state': {
      const params = new URLSearchParams();
      if (args.account) params.append('account', args.account);
      if (args.instrument) params.append('instrument', args.instrument);
      const res = await ntFetch(`/api/riskguard/fsm-state?${params}`);
      return res.data;
    }

    // P2-103. Read-only. The summarising happens HERE rather than in the addon because the
    // constraint is the CONTEXT WINDOW, not bandwidth: 635KB over localhost costs nothing, and
    // 635KB into a tool result costs the conversation. Measured on the live box: 635,447 bytes
    // -> 2,880 bytes of summary, with every number folded out of the same rule rows the
    // `account` view returns, so the two cannot disagree the way `F-9` did.
    case 'nt_riskguard_inventory': {
      const res = await ntFetch('/api/riskguard/inventory');
      const inv = res.data;
      if (!inv || !Array.isArray(inv.accounts)) {
        return { error: 'inventory unavailable', raw: inv };
      }

      const view = args.view || (args.account ? 'account' : 'summary');

      if (view === 'account' || args.account) {
        // P1-90 on a read path, which is exactly what P2-109 was: a name that matches nothing
        // is REFUSED with the available names, never answered about every account.
        const one = forAccount(inv, args.account);
        if (!one) {
          const names = accountNames(inv);
          return {
            error: `No account named '${args.account}' in the guard inventory ` +
                   `(${names.length} available). Refusing to answer about a different account.`,
            availableSample: names.slice(0, 10),
          };
        }
        return { takenUtc: inv.takenUtc, mode: inv.mode, isArmed: inv.isArmed, account: one };
      }

      if (view === 'full') return inv;
      return summarise(inv);
    }

    case 'nt_copier_snapshot': {
      const res = await ntFetch('/api/copier/snapshot');
      const snap = res.data;
      if (!snap || !Array.isArray(snap.rows)) return { error: 'snapshot unavailable', raw: snap };
      if (!args.account) return snap;

      // Either side of the relationship: asking about an account you lead from and an account
      // you copy INTO are the same question -- "what is this account involved in".
      const want = String(args.account).trim().toLowerCase();
      const rows = snap.rows.filter(r =>
        String(r.leaderAccountName || '').trim().toLowerCase() === want ||
        String(r.followerAccountName || '').trim().toLowerCase() === want);
      return { ...snap, rows, filteredTo: args.account, matchedRows: rows.length };
    }

    case 'nt_copier_config': {
      // The mapping lives in lib/copier-config-request.js so an executed test can
      // reach it -- the same rule the bridge follows for ApplyRelationshipRequest.
      // It also decides GET vs POST: a read must not write (P1-69/P2-41).
      const { method, path, body } = buildCopierConfigRequest(args);
      const res = await ntFetch(path, method, body);
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
        await ntFetch('/api/compile', 'POST', { debug: !!args.debug, ignoreWarnings: !!args.ignoreWarnings }, 30000);
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

