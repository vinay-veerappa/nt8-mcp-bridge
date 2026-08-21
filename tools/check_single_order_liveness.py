"""P1-131. There must be exactly ONE answer to "is this order still live at the broker?"

The defect was a SECOND list: `McpBridgeAddOn.cs` hand-rolled `OccupiesSlotForBridge` (and a third
inline filter in `GetOrders`) as disjunctions of `OrderState` literals, and nothing ever compared
them to the core's classifier or to each other. They disagreed in both directions -- a departing
order counted, six live states invisible -- and the count feeds `BridgeConnectionPlan.WouldStrand`,
which refuses a disconnect. The fix extracted `BridgeOrderLiveness` (state-NAME based, so the
harness can execute it) and routed all three sites through it.

This gate keeps it ONE list. The regression it prevents is a FOURTH hand-rolled liveness set
reappearing in `McpBridgeAddOn.cs` -- the measured shape is a disjunction of `OrderState.<Name>`
comparisons joined by `||` (exactly what `OccupiesSlotForBridge` and the `GetOrders` filter were).
[[a-second-reader-of-the-same-state]]: count the sites, and stop a new one being born.

It asserts TWO things:
  1. `McpBridgeAddOn.cs` contains NO `OrderState.<A> || ... OrderState.<B>` liveness disjunction.
  2. `McpBridgeAddOn.cs` still ROUTES through `BridgeOrderLiveness` (>=1 `IsTerminal`, >=1
     `WouldBeStrandedByDisconnect`) -- an absence gate that passed because the calls were deleted
     would be worse than useless ([[a-code-move-disarms-a-source-gate]]).

Comments and strings are masked first; the negative direction is asserted in the self-test.
Exits 1 on a re-hand-rolled list or a severed route.
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
TARGET = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# A disjunction of two OrderState literals, not crossing a statement boundary. `[^;{}]` keeps the
# match inside one expression so two unrelated single comparisons on nearby lines do not join.
DISJUNCTION = re.compile(r'OrderState\s*\.\s*[A-Za-z]+[^;{}]*\|\|[^;{}]*OrderState\s*\.\s*[A-Za-z]+')
IS_TERMINAL = re.compile(r'\bBridgeOrderLiveness\s*\.\s*IsTerminal\s*\(')
WOULD_STRAND = re.compile(r'\bBridgeOrderLiveness\s*\.\s*WouldBeStrandedByDisconnect\s*\(')


def mask_comments_and_strings(text):
    """`//`, `/* */`, and string/char literals -> spaces, newlines kept. A disjunction quoted in a
    log string or described in the comment that explains the fix is not a second list."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '; i += 1
        elif c == '/' and i + 1 < n and text[i + 1] == '*':
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            for _ in range(2):
                if i < n:
                    out[i] = ' '; i += 1
        elif c == '@' and i + 1 < n and text[i + 1] == '"':
            out[i] = ' '; i += 2; out[i - 1] = ' '
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out[i] = out[i + 1] = ' '; i += 2; continue
                    out[i] = ' '; i += 1; break
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
        elif c == '"' or c == "'":
            quote = c
            out[i] = ' '; i += 1
            while i < n and text[i] != quote:
                if text[i] == '\\' and i + 1 < n:
                    out[i] = out[i + 1] = ' '; i += 2; continue
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            if i < n:
                out[i] = ' '; i += 1
        else:
            i += 1
    return ''.join(out)


def disjunction_hits(text):
    masked = mask_comments_and_strings(text)
    return [m.group(0)[:70] for m in DISJUNCTION.finditer(masked)]


def routes_through_liveness(text):
    masked = mask_comments_and_strings(text)
    return bool(IS_TERMINAL.search(masked)), bool(WOULD_STRAND.search(masked))


def self_test():
    problems = []
    hand_rolled = ('bool F(OrderState s){ return s == OrderState.Working || s == OrderState.Accepted '
                   '|| s == OrderState.CancelPending; } '
                   'void G(){ if(BridgeOrderLiveness.IsTerminal(x)) return; '
                   'BridgeOrderLiveness.WouldBeStrandedByDisconnect(y); }')
    clean = ('void G(){ if(BridgeOrderLiveness.IsTerminal(order.OrderState.ToString())) continue; '
             'if(BridgeOrderLiveness.WouldBeStrandedByDisconnect(o.OrderState.ToString())) n++; '
             'if(order.OrderState == OrderState.Working){} }')  # a single comparison is fine
    commented = ('void G(){ /* old: s == OrderState.Working || s == OrderState.Accepted */ '
                 'if(BridgeOrderLiveness.IsTerminal(x)) return; '
                 'BridgeOrderLiveness.WouldBeStrandedByDisconnect(y); }')
    severed = 'bool F(OrderState s){ return s == OrderState.Working; }'  # no BridgeOrderLiveness calls

    if not disjunction_hits(hand_rolled):
        problems.append('a re-hand-rolled OrderState disjunction was NOT detected')
    if disjunction_hits(clean):
        problems.append('clean routed code was flagged as a hand-rolled list -- false positive')
    if disjunction_hits(commented):
        problems.append('a disjunction in a COMMENT was flagged -- masking is broken')
    it, ws = routes_through_liveness(clean)
    if not (it and ws):
        problems.append('routed code was reported as NOT routing through BridgeOrderLiveness')
    it2, ws2 = routes_through_liveness(severed)
    if it2 or ws2:
        problems.append('code with no BridgeOrderLiveness calls was reported as routing -- a severed route would pass')
    if problems:
        print('SELF-TEST FAILED -- this gate cannot be trusted:\n')
        for p in problems:
            print('  * ' + p)
        sys.exit(1)


self_test()

if not os.path.exists(TARGET):
    print('FAILED: %s does not exist.' % TARGET)
    sys.exit(1)

text = open(TARGET, encoding='utf-8').read()
hits = disjunction_hits(text)
has_terminal, has_strand = routes_through_liveness(text)

failures = []
print('Order-liveness in addons/McpBridgeAddOn.cs (P1-131 -- one list, not four):')
if hits:
    print('  [SECOND LIST] %d OrderState disjunction(s) found:' % len(hits))
    for h in hits:
        print('      ' + h)
    failures.append('a hand-rolled OrderState liveness disjunction is back -- route it through '
                    'BridgeOrderLiveness instead of listing states inline.')
else:
    print('  [OK] no inline OrderState liveness disjunction (BridgeOrderLiveness is the one list).')

if has_terminal and has_strand:
    print('  [OK] routes through BridgeOrderLiveness (IsTerminal + WouldBeStrandedByDisconnect).')
else:
    missing = ', '.join(n for n, ok in (('IsTerminal', has_terminal),
                                         ('WouldBeStrandedByDisconnect', has_strand)) if not ok)
    print('  [SEVERED] McpBridgeAddOn.cs no longer calls: %s' % missing)
    failures.append('the liveness route was severed (%s absent) -- the counts/filter fell back to '
                    'some other decision.' % missing)

if failures:
    print('\nFAILED:')
    for f in failures:
        print('  * ' + f)
    sys.exit(1)

print('\nOK: exactly one order-liveness predicate, and McpBridgeAddOn.cs routes through it.')
sys.exit(0)
