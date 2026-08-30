// tools/sync_client_caches.mjs — regenerate MCP client tool caches FROM lib/tools.js.
//
// WHY THIS EXISTS. Every client (VS Code allowlist, Antigravity per-tool schema files)
// keeps its own copy of the tool list. Hand-editing those copies is P1-72's failure
// mode repeated at three sites: the copy was true when someone typed it, cannot see
// the next tools.js change, and nothing reports the drift -- a tool that works in
// opencode but is invisible in VS Code looks to the caller like a broken server.
//
// The addon side solved this once already ("the wrapper's list must be DERIVED from
// the receiver, not transcribed"); this applies the same rule to the clients: the
// generator runs FROM tools.js, removes every cache entry first (so retired tools
// cannot linger), and rewrites. Idempotent by construction.
//
// USAGE:  node tools/sync_client_caches.mjs [--check]
//   --check  exit 1 if any cache is stale, write nothing (CI / pre-deploy gate)
//
// WHAT IT COVERS (the three Antigravity dirs + the VS Code allowlist):
//   %USERPROFILE%\.gemini\antigravity*\mcp\ninjatrader\*.json   one schema file per tool
//   %APPDATA%\Code\User\settings.json                           alwaysOnTools allowlist
//
// WHAT IT DOES NOT COVER: the MCP server path/args/env blocks in client config
// files. Those are names, not the tool list, and renaming servers by script is
// how one typo takes out every client at once.
import { TOOLS } from '../mcp/lib/tools.js';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const CHECK_ONLY = process.argv.includes('--check');
const HOME = os.homedir();
let stale = 0;

// ── 1. The three Antigravity tool-schema caches ──────────────────────────
const ANTIGRAVITY_DIRS = [
  path.join(HOME, '.gemini', 'antigravity', 'mcp', 'ninjatrader'),
  path.join(HOME, '.gemini', 'antigravity-cli', 'mcp', 'ninjatrader'),
  path.join(HOME, '.gemini', 'antigravity-ide', 'mcp', 'ninjatrader'),
];

for (const dir of ANTIGRAVITY_DIRS) {
  if (!fs.existsSync(dir)) {
    console.log(`  [SKIP] ${dir} (no such directory)`);
    continue;
  }
  const existing = fs.readdirSync(dir).filter(f => f.endsWith('.json'));
  const wanted = new Set(TOOLS.map(t => t.name + '.json'));

  const removed = existing.filter(f => !wanted.has(f));
  const changed = [];
  for (const t of TOOLS) {
    const file = path.join(dir, t.name + '.json');
    const body = JSON.stringify(t);
    if (!fs.existsSync(file) || fs.readFileSync(file, 'utf8') !== body) changed.push(t.name);
  }
  if (removed.length === 0 && changed.length === 0) {
    console.log(`  [OK] ${path.basename(path.dirname(path.dirname(dir)))}: ${existing.length} files in sync`);
    continue;
  }
  stale++;
  if (CHECK_ONLY) {
    console.log(`  [STALE] ${dir}: ${changed.length} to update, ${removed.length} to remove (${changed.slice(0, 3).join(', ')}${changed.length > 3 ? ', ...' : ''})`);
    continue;
  }
  for (const f of removed) fs.unlinkSync(path.join(dir, f));
  for (const t of TOOLS) fs.writeFileSync(path.join(dir, t.name + '.json'), JSON.stringify(t));
  console.log(`  [SYNCED] ${path.basename(path.dirname(path.dirname(dir)))}: +${changed.length}/-${removed.length} -> ${TOOLS.length} files`);
}

// ── 2. The VS Code alwaysOnTools allowlist ───────────────────────────────
const VSCODE_SETTINGS = path.join(process.env.APPDATA || path.join(HOME, 'AppData', 'Roaming'), 'Code', 'User', 'settings.json');
if (!fs.existsSync(VSCODE_SETTINGS)) {
  console.log(`  [SKIP] ${VSCODE_SETTINGS} (no such file)`);
} else {
  const raw = fs.readFileSync(VSCODE_SETTINGS, 'utf8');
  const prefix = 'mcp_nt-mcp-server_nt_';
  const listed = new Set([...raw.matchAll(new RegExp(prefix + '([a-z0-9_]+)', 'g'))].map(m => m[1]));
  // nt_ prefix on the tool name itself; the regex above captures e.g. "health" from "nt_health"
  const toolNames = new Set(TOOLS.map(t => t.name.replace(/^nt_/, '')));
  const wanted = new Set(TOOLS.map(t => t.name));
  const missing = [...wanted].filter(n => !listed.has(n.replace(/^nt_/, '')));
  const retired = [...listed].map(n => 'nt_' + n).filter(n => !wanted.has(n));

  if (missing.length === 0 && retired.length === 0) {
    console.log(`  [OK] VS Code allowlist: ${listed.size} entries in sync`);
  } else {
    stale++;
    if (CHECK_ONLY) {
      console.log(`  [STALE] VS Code allowlist: missing ${missing.length} (${missing.slice(0, 3).join(', ')}${missing.length > 3 ? ', ...' : ''}), stale ${retired.length} (${retired.join(', ')})`);
    } else {
      let lines = raw.split(/\r?\n/);
      // Drop retired entries.
      for (const r of retired) {
        lines = lines.filter(l => !new RegExp('^\\s*"' + prefix + r.replace(/^nt_/, '') + '",?\\s*$').test(l));
      }
      // Find the last listed entry; append the missing ones after it.
      let last = -1;
      for (let i = 0; i < lines.length; i++) if (lines[i].includes(prefix)) last = i;
      if (last >= 0 && missing.length) {
        const indent = lines[last].match(/^\s*/)[0];
        // Ensure the current last entry has a comma before appending.
        if (!lines[last].replace(/\s+$/, '').endsWith(',')) lines[last] = lines[last].replace(/\s+$/, '') + ',';
        const inserts = missing.map(n => `${indent}"${prefix}${n.replace(/^nt_/, '')}"`);
        lines.splice(last + 1, 0, ...inserts);
        // De-comma our own last insert: find it and strip its trailing comma.
        for (let i = last + 1; i <= last + inserts.length; i++) {
          const isLastInsert = i === last + inserts.length;
          const nextIsEntry = /^\s*"/.test(lines[i + 1] || '');
          if (isLastInsert && !nextIsEntry) lines[i] = lines[i].replace(/,\s*$/, '');
        }
        fs.writeFileSync(VSCODE_SETTINGS, lines.join('\n'));
      }
      console.log(`  [SYNCED] VS Code allowlist: +${missing.length}/-${retired.length} -> ${TOOLS.length} entries`);
    }
  }
}

if (CHECK_ONLY) {
  console.log(stale === 0 ? 'OK: all client caches in sync.' : `DRIFT: ${stale} cache(s) out of sync.`);
  process.exit(stale === 0 ? 0 : 1);
}
console.log('DONE: client caches regenerated from mcp/lib/tools.js.');