/**
 * Maps `nt_copier_config` tool arguments onto an HTTP request for the NT8 bridge.
 *
 * This lives in its own module for two reasons. Importing nt-mcp-server.js starts
 * its stdin readline loop, so a test of a function defined there would hang. And it
 * is the same rule the bridge follows for `ApplyRelationshipRequest`: put the
 * request->object mapping where an executed test can reach it, never inline in a
 * surface that nothing covers.
 *
 * Three invariants, each with a defect behind it:
 *
 *   1. A READ CARRIES NO BODY, and goes over GET. `/api/copier/config` had no GET
 *      until 2026-08-13 (P1-69), so the only way to inspect the copier was to write
 *      to it -- which defeats the GET-mutate-POST-GET-diff discipline this project
 *      relies on, and is how `nt_riskguard_config` once flattened the live risk
 *      config to defaults while echoing the request back as "applied" (P2-41).
 *
 *   2. A WRITE SENDS ONLY THE KEYS THE CALLER SUPPLIED (P1-73).
 *      TradeCopierEngine.ApplyRelationshipRequest merges: an absent key preserves
 *      the stored value, a present key overwrites it. So a manufactured default is
 *      not a convenience, it is silent data loss -- the schema declared
 *      quantityRatio 1.0 and autoConversion true, and a caller nudging one field
 *      reset the other. An explicit `false` or `0` is a value and IS sent; only
 *      absence means absence.
 *
 *   3. AN UNKNOWN ACTION THROWS (P1-72). The bridge's if-chain ends in
 *      `else { read }`, so anything it does not recognise returns a config read
 *      with success: true. The schema advertised `quarantine` and nothing anywhere
 *      implemented it: a misbehaving follower kept copying while the caller was
 *      told it had been quarantined.
 */

// Reads. Everything else is a write, and an action not in either table throws.
const READ_ACTIONS = new Set(['get', 'get_groups']);

// Actions the bridge's CopierConfig() actually branches on, plus the two aliases
// this wrapper resolves itself (quarantine/unquarantine -> set + isQuarantined).
const RELATIONSHIP_WRITES = new Set(['set', 'update', 'remove', 'clear', 'delete', 'quarantine', 'unquarantine']);
const GROUP_WRITES = new Set([
  'set_group', 'upsert_group', 'remove_group', 'delete_group',
  'add_follower_to_group', 'remove_follower_from_group',
]);

// Group writes that name a single follower rather than a list.
const NEEDS_ONE_FOLLOWER = new Set(['add_follower_to_group', 'remove_follower_from_group']);

// Every relationship field the engine stores and this tool can now set. Ordered as
// the operator thinks about them, not alphabetically.
const RELATIONSHIP_FIELDS = [
  'quantityRatio',
  'autoSymbolConversion',
  'sizingMode',
  'perTickerRatios',
  'customSymbolMappings',
  'maxSlippageTicks',
  'dailyLossLimit',
  'isEnabled',
  'armedForLive',
  'isQuarantined',
  'quarantineReason',
];

const GROUP_FIELDS = [
  'followerAccounts',
  'quantityRatio',
  'autoSymbolConversion',
  'sizingMode',
  'perTickerRatios',
  'customSymbolMappings',
  'maxSlippageTicks',
  'dailyLossLimit',
  'isEnabled',
  'armedForLive',
];

const PATH = '/api/copier/config';

function known() {
  return [...READ_ACTIONS, ...RELATIONSHIP_WRITES, ...GROUP_WRITES].join(', ');
}

/** Present means present. `false` and `0` are values; only undefined/null are absent. */
function has(args, key) {
  return Object.prototype.hasOwnProperty.call(args, key) && args[key] !== undefined && args[key] !== null;
}

function require_(args, key, action) {
  if (!has(args, key) || String(args[key]).trim() === '') {
    throw new Error(
      `nt_copier_config: action '${action}' requires ${key}. ` +
      `Refusing to guess: the engine falls back to leaderAccount 'Sim101' and ` +
      `followerAccount 'SimCopy2', both real accounts, so an underspecified write ` +
      `edits a live relationship silently.`,
    );
  }
}

function copyPresent(args, keys, into) {
  for (const k of keys) if (has(args, k)) into[k] = args[k];
  return into;
}

/**
 * P1-74. The tool argument is `autoConversion` -- that is its documented name and
 * callers use it -- but the engine's field is `AutoSymbolConversion`, and
 * `autoConversion` is not in its ConfigAliasMap, so Json.NET dropped it as an
 * unknown member. The parameter had never done anything, on the one feature that
 * silently dropped a live copy (MNQ->NQ at ratio 1.0 rounds below a contract).
 *
 * Translate rather than rename: `autoConversion` keeps working for existing callers
 * and the wire key becomes the one the engine reads. An explicit
 * `autoSymbolConversion` wins, because naming the real field is unambiguous. Pinned
 * on the engine side by TestP1_74_AutoConversionIsNotAFieldAndIsSilentlyDropped in
 * nt8-riskguard -- these tests can only prove what is emitted, never what is read.
 */
function translateAutoConversion(args, body) {
  if (has(body, 'autoSymbolConversion')) return body;
  if (has(args, 'autoConversion')) body.autoSymbolConversion = args.autoConversion;
  return body;
}

/**
 * @param {object} args tool arguments
 * @returns {{method: 'GET'|'POST', path: string, body: object|null}}
 */
export function buildCopierConfigRequest(args = {}) {
  const action = (has(args, 'action') ? String(args.action) : 'get').trim();

  if (!READ_ACTIONS.has(action) && !RELATIONSHIP_WRITES.has(action) && !GROUP_WRITES.has(action)) {
    throw new Error(
      `nt_copier_config: unknown action '${action}'. Known actions: ${known()}. ` +
      `Refusing rather than falling through to a read -- the bridge would return ` +
      `success: true and change nothing, which is how the quarantine action went ` +
      `unimplemented without anyone noticing (P1-72).`,
    );
  }

  // ── Reads: GET, no body. Query params carry what the caller asked about.
  if (READ_ACTIONS.has(action)) {
    // encodeURIComponent, NOT URLSearchParams: the latter encodes a space as `+`,
    // which only decodes back to a space under form-urlencoded rules. The consumer
    // is .NET's HttpListener.QueryString, and NT8 account names may contain spaces,
    // so use the percent-encoding that means the same thing under both.
    const params = [];
    if (action !== 'get') params.push(`action=${encodeURIComponent(action)}`);
    if (has(args, 'leaderAccount')) params.push(`leaderAccount=${encodeURIComponent(args.leaderAccount)}`);
    return { method: 'GET', path: params.length ? `${PATH}?${params.join('&')}` : PATH, body: null };
  }

  // ── Writes: POST, and only what was supplied.
  const body = {};

  if (GROUP_WRITES.has(action)) {
    require_(args, 'groupName', action);
    body.action = action;
    body.groupName = args.groupName;
    if (NEEDS_ONE_FOLLOWER.has(action)) {
      require_(args, 'followerAccount', action);
      body.followerAccount = args.followerAccount;
    }
    if (has(args, 'leaderAccount')) body.leaderAccount = args.leaderAccount;
    copyPresent(args, GROUP_FIELDS, body);
    translateAutoConversion(args, body);
  } else {
    require_(args, 'leaderAccount', action);
    require_(args, 'followerAccount', action);

    // quarantine/unquarantine are this wrapper's own aliases: the bridge has no
    // branch for either, and the field is settable through `set`. Resolving it
    // here is what turns an advertised no-op into a real state change (P1-72).
    body.action = (action === 'quarantine' || action === 'unquarantine') ? 'set' : action;
    body.leaderAccount = args.leaderAccount;
    body.followerAccount = args.followerAccount;
    copyPresent(args, RELATIONSHIP_FIELDS, body);
    translateAutoConversion(args, body);

    if (action === 'quarantine' || action === 'unquarantine') {
      body.isQuarantined = action === 'quarantine';
    }
  }

  // Arming is refused at the boundary rather than downgraded. The engine's
  // ApplyArmingGate correctly sets armed=false when confirmLive is missing, but
  // through a tool that reads as "I armed it, and it reports armed: false" -- a
  // response contradicting its own request, which is P0-68's shape.
  if (body.armedForLive === true && args.confirmLive !== true) {
    throw new Error(
      `nt_copier_config: armedForLive: true requires confirmLive: true. ` +
      `Without it the engine silently stores armedForLive: false and the response ` +
      `contradicts the request. Arming a copier relationship sends real orders to ` +
      `a real account.`,
    );
  }
  if (has(args, 'confirmLive')) body.confirmLive = args.confirmLive;

  return { method: 'POST', path: PATH, body };
}
