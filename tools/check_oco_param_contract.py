"""The OCO order contract has two sides in this ONE repo, and they drifted.

`nt_place_oco_order` advertises its price fields in the wrapper schema (`mcp/lib/tools.js`),
and the addon (`addons/McpBridgeAddOn.cs`, `PlaceOcoOrder`) reads them out of the POST body.
For months the wrapper marked `limitPrice` REQUIRED (profit target) while the addon read only
`targetPrice` and rejected anything else with "targetPrice required" -- so every call made through
the documented schema failed, and the standalone-OCO half of `P2-181`'s validation was blocked on
it. The two halves of one contract, in two languages, cannot be pinned in one type; the only thing
that keeps them honest is a gate. Contract drift is the recurring wrapper-defect class
([[nt8-mcp-wrapper-defects]]): P1-72 regressed twice on exactly this shape.

WHAT THIS ASSERTS: every price field the wrapper marks REQUIRED for `nt_place_oco_order` is
actually READ by the addon's `PlaceOcoOrder` handler (by that literal name, or -- for `limitPrice`
-- via the `targetPrice` alias the handler accepts). A field NAMED only in a comment is not read
([[a-source-gate-must-assert-the-condition]]), so C# comments are masked before the search, and the
negative direction is asserted in the self-test rather than trusted.

Exits 1 if a required wrapper price field is not read by the addon.
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
TOOLS = os.path.join(REPO, 'mcp', 'lib', 'tools.js')
ADDON = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# Price fields the gate cares about. Non-price required fields (symbol, quantity, account,
# idempotencyKey, action) are handled by the idempotency/account layer, not by name inside
# PlaceOcoOrder, so asserting THEY are read there would false-fail. The drift class is prices.
PRICE_FIELDS = {'limitPrice', 'stopPrice', 'targetPrice', 'price'}
# The addon may satisfy a wrapper field via a documented alias it reads instead.
ALIASES = {'limitPrice': {'limitPrice', 'targetPrice'}}


def mask_line_comments(text):
    """`//` line comments and `/* */` blocks -> spaces (newlines kept). A field NAMED in the
    comment that explains the fix must not count as a read."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        if text[i] == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '; i += 1
        elif text[i] == '/' and i + 1 < n and text[i + 1] == '*':
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            for _ in range(2):
                if i < n:
                    out[i] = ' '; i += 1
        else:
            i += 1
    return ''.join(out)


def oco_required_price_fields(tools_src):
    """The `required: [...]` price fields declared for the nt_place_oco_order tool."""
    m = re.search(r"name:\s*'nt_place_oco_order'", tools_src)
    if not m:
        return None
    req = re.search(r'required:\s*\[([^\]]*)\]', tools_src[m.end():m.end() + 4000])
    if not req:
        return set()
    names = set(re.findall(r"'([^']+)'", req.group(1)))
    return names & PRICE_FIELDS


def place_oco_body(addon_src):
    """The body of `private object PlaceOcoOrder(...)`, comments masked."""
    m = re.search(r'\bPlaceOcoOrder\s*\([^)]*\)\s*\{', addon_src)
    if not m:
        return None
    i = m.end() - 1
    depth = 0
    start = i
    while i < len(addon_src):
        if addon_src[i] == '{':
            depth += 1
        elif addon_src[i] == '}':
            depth -= 1
            if depth == 0:
                return mask_line_comments(addon_src[start:i + 1])
        i += 1
    return mask_line_comments(addon_src[start:])


def field_is_read(body, field):
    """Does the handler read `field` (or an accepted alias) out of the request?"""
    for name in ALIASES.get(field, {field}):
        if re.search(r'req\s*\.\s*(?:GetValueOrDefault|ContainsKey|TryGetValue|\[)\s*[("]*' + re.escape(name), body) \
           or ('"' + name + '"') in body:
            return True
    return False


def self_test():
    """Negative controls -- the dangerous direction is a required field reported READ when the
    handler does not read it (the exact defect this gate exists for)."""
    problems = []
    tools_ok = "name: 'nt_place_oco_order',\n  inputSchema: { required: ['symbol', 'limitPrice', 'stopPrice'] }"
    if oco_required_price_fields(tools_ok) != {'limitPrice', 'stopPrice'}:
        problems.append('failed to extract the OCO required price fields from a known-good schema')
    body_reads = 'object PlaceOcoOrder(string b){ var t = req.ContainsKey("targetPrice"); var s = req.GetValueOrDefault("stopPrice", 0); }'
    body_missing = 'object PlaceOcoOrder(string b){ var s = req.GetValueOrDefault("stopPrice", 0); }'
    body_comment_only = 'object PlaceOcoOrder(string b){ /* reads limitPrice as targetPrice */ var s = req.GetValueOrDefault("stopPrice",0); }'
    b1 = place_oco_body(body_reads)
    b2 = place_oco_body(body_missing)
    b3 = place_oco_body(body_comment_only)
    if not (b1 and field_is_read(b1, 'limitPrice') and field_is_read(b1, 'stopPrice')):
        problems.append('a handler that reads targetPrice+stopPrice was reported as NOT reading them')
    if b2 and field_is_read(b2, 'limitPrice'):
        problems.append('a handler missing the target field was reported as reading limitPrice -- the drift would pass')
    if b3 and field_is_read(b3, 'limitPrice'):
        problems.append('limitPrice named only in a COMMENT was counted as read -- masking is broken')
    if problems:
        print('SELF-TEST FAILED -- this gate cannot be trusted:\n')
        for p in problems:
            print('  * ' + p)
        sys.exit(1)


self_test()

for path, what in ((TOOLS, 'wrapper schema'), (ADDON, 'addon handler')):
    if not os.path.exists(path):
        print('FAILED: %s (%s) does not exist.' % (path, what))
        sys.exit(1)

required = oco_required_price_fields(open(TOOLS, encoding='utf-8').read())
if required is None:
    print('FAILED: nt_place_oco_order not found in mcp/lib/tools.js -- did the tool move or rename?')
    sys.exit(1)

body = place_oco_body(open(ADDON, encoding='utf-8').read())
if body is None:
    print('FAILED: PlaceOcoOrder not found in addons/McpBridgeAddOn.cs -- did the handler move or rename?')
    sys.exit(1)

print('nt_place_oco_order price-field contract (wrapper schema <-> addon handler):')
missing = []
for field in sorted(required):
    if field_is_read(body, field):
        via = ' (via targetPrice alias)' if field == 'limitPrice' and '"limitPrice"' not in body else ''
        print('  [OK]   wrapper requires %-11s -> addon reads it%s' % (field, via))
    else:
        print('  [DRIFT] wrapper requires %-11s -> addon NEVER reads it' % field)
        missing.append(field)

if missing:
    print('\nFAILED: the addon does not read wrapper-required OCO price field(s): %s' % ', '.join(missing))
    print('A caller using the documented schema would have that price defaulted to 0 and rejected.')
    sys.exit(1)

print('\nOK: every wrapper-required OCO price field is read by the addon handler.')
sys.exit(0)
