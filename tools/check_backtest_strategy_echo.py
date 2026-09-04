"""`Backtest()` must not run a strategy other than the one it was asked for.

The Strategy Analyzer window is REUSED across calls (`_saWindow`) and the strategy
was applied with the lenient `SetP`, so a name that did not resolve failed silently
and the window kept whatever it already had. Measured 2026-09-04: a request for
`@SampleMACrossOver` ran `_McpTestBot` and returned `totalTrades: 0` -- which is
indistinguishable from the requested strategy having simply not traded. The trigger
is NT8's own convention: the file is `@SampleMACrossOver.cs` while the CLASS is
`SampleMACrossOver`, so the leading `@` resolves to nothing. A second call naming
`BollingerCrossOver` in the same session was honoured, so this is an
unresolvable-name FALLBACK, not the argument being ignored.

Same family as the phase 0.1 config-inheritance work, one level up: not the settings
inherited from a reused window, but the strategy itself.

WHAT THIS ASSERTS, inside the `Backtest(string body)` method body only:

  1. the requested name is RESOLVED (`FindStrategyType`) and the method RETURNS on
     null -- refusing before the shared window is touched, so no other strategy can
     be run in its place;
  2. the selection is READ BACK (`GetP(props, "Strategy")`) after the write and
     compared against the resolved `Type.Name`, because resolving a name proves it
     is a real strategy and NOT that the property took;
  3. the mismatch is routed into `paramErrors`, which is the existing fail-closed
     list -- a diagnosis written somewhere nobody reads is an alarm wired to a dead
     output ([[an-alarm-wired-to-a-dead-output]]);
  4. `StrategyIdentity` does NOT decide identity with `Contains`/`StartsWith`/
     `EndsWith`. `MACrossOver` is a substring of `SampleMACrossOver`, so a substring
     test would pass exactly the confusion this gate exists to catch
     ([[a-filter-that-matches-too-much]]).

C# comments are masked before every search: a condition NAMED in the comment that
explains the fix is not the condition being implemented
([[a-source-gate-must-assert-the-condition]]). Every pattern has a negative control
in `self_test()`, because a regex cannot see reachability and four mutants have
beaten gates of this shape before.

Exits 1 if any of the four conditions is not present in the method body.
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ADDON = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')


def mask_comments(text):
    """`//` and `/* */` -> spaces, newlines preserved so line numbers survive."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        if text[i] == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '
                i += 1
        elif text[i] == '/' and i + 1 < n and text[i + 1] == '*':
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            if i < n:
                out[i] = ' '
                if i + 1 < n:
                    out[i + 1] = ' '
                i += 2
        else:
            i += 1
    return ''.join(out)


def method_body(text, signature):
    """Brace-matched body of one method. Parsed, not split: a `}` inside a string
    literal or a nested lambda must not end the region
    ([[state-the-region-a-gate-inspects]])."""
    start = text.find(signature)
    if start < 0:
        return None
    i = text.find('{', start)
    if i < 0:
        return None
    depth, j, n = 0, i, len(text)
    in_str = in_chr = False
    while j < n:
        c = text[j]
        if in_str:
            if c == '\\':
                j += 2
                continue
            if c == '"':
                in_str = False
        elif in_chr:
            if c == '\\':
                j += 2
                continue
            if c == "'":
                in_chr = False
        elif c == '"':
            in_str = True
        elif c == "'":
            in_chr = True
        elif c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return text[i:j + 1]
        j += 1
    return None


# Each check: (label, predicate over the masked method body, why it matters)
CHECKS = [
    ('resolves the requested name',
     lambda b: re.search(r'FindStrategyType\s*\(\s*strategy\s*\)', b) is not None,
     'an unresolvable name would fall through to the reused window'),
    ('returns on an unresolved name',
     lambda b: re.search(
         r'FindStrategyType\s*\(\s*strategy\s*\)\s*;.{0,600}?==\s*null.{0,400}?\breturn\b',
         b, re.S) is not None,
     'resolving without returning leaves the shared window to be reconfigured anyway'),
    ('reads the selection back',
     lambda b: re.search(r'GetP\s*\(\s*props\s*,\s*"Strategy"\s*\)', b) is not None,
     'resolving a name proves it is a real strategy, not that the property took'),
    ('compares the read-back to the resolved type name',
     lambda b: re.search(r'string\.Equals\s*\(\s*effectiveStrategy\s*,\s*'
                         r'stratTypeReq\.Name', b) is not None,
     'a read-back that is never compared is a value nobody checked'),
    ('routes the mismatch into paramErrors',
     lambda b: re.search(r'paramErrors\.Add\s*\(.{0,400}?Strategy did not take', b,
                         re.S) is not None,
     'paramErrors is the list the fail-closed guard reads; anywhere else is a '
     'diagnosis nobody consumes'),
    ('resolves BEFORE the shared window is written',
     lambda b: _resolve_precedes_write(b),
     'the Strategy Analyzer window is a single shared resource: a refusal that '
     'fires after it has been pointed at another strategy no longer prevents '
     'anything. Rank a refusal by what it BINDS'),
]


def _resolve_precedes_write(body):
    """Ordering, asserted by position rather than assumed.

    Both statements exist in either order, so the five checks above all pass on a
    body where the resolve has been moved below the write -- the class was real
    and the source mutant written for it was caught by the `return` check instead
    ([[check-the-exemplar-belongs-to-the-class]]). This is the condition itself.
    """
    resolve = re.search(r'FindStrategyType\s*\(\s*strategy\s*\)', body)
    write = re.search(r'SetP\s*\(\s*props\s*,\s*"Strategy"', body)
    if resolve is None or write is None:
        return False
    return resolve.start() < write.start()


def identity_is_not_substring_based(text):
    """`StrategyIdentity` must not settle identity with a substring test."""
    body = method_body(text, 'private static string StrategyIdentity(')
    if body is None:
        return None
    return not re.search(r'\.(Contains|StartsWith|EndsWith)\s*\(', body)


def evaluate(text):
    """(results, identity_ok, body_len) for an addon source string."""
    masked = mask_comments(text)
    body = method_body(masked, 'private object Backtest(string body)')
    if body is None:
        return None, None, 0
    results = [(label, bool(pred(body)), why) for label, pred, why in CHECKS]
    return results, identity_is_not_substring_based(masked), len(body)


def self_test():
    """Negative controls. A gate that passes anything proves nothing."""
    failures = []

    good = '''
        private object Backtest(string body)
        {
            var stratTypeReq = FindStrategyType(strategy);
            if (stratTypeReq == null) { return new { error = "nope" }; }
            SetP(props, "Strategy", strategy);
            effectiveStrategy = StrategyIdentity(GetP(props, "Strategy"));
            if (!string.Equals(effectiveStrategy, stratTypeReq.Name, StringComparison.OrdinalIgnoreCase))
                paramErrors.Add(string.Format("Strategy did not take: {0}", strategy));
        }
        private static string StrategyIdentity(object v) { return v.ToString(); }
    '''
    res, ident, _ = evaluate(good)
    if res is None or not all(ok for _, ok, _ in res) or not ident:
        failures.append('the intended shape does not PASS: %r / identity=%r' % (res, ident))

    # each mutant removes exactly one condition and must be caught
    mutants = {
        'no resolve': good.replace('var stratTypeReq = FindStrategyType(strategy);', 'var stratTypeReq = SomethingElse(strategy);'),
        'resolves but does not return': good.replace('if (stratTypeReq == null) { return new { error = "nope" }; }', 'if (stratTypeReq == null) { Log("nope"); }'),
        'no read-back': good.replace('StrategyIdentity(GetP(props, "Strategy"))', 'StrategyIdentity(strategy)'),
        'read-back never compared': good.replace('if (!string.Equals(effectiveStrategy, stratTypeReq.Name, StringComparison.OrdinalIgnoreCase))', 'if (false)'),
        'mismatch not routed to paramErrors': good.replace('paramErrors.Add(string.Format("Strategy did not take: {0}", strategy));', 'Log(string.Format("Strategy did not take: {0}", strategy));'),
    }
    for name, mutant in mutants.items():
        res, _, _ = evaluate(mutant)
        if res is None:
            failures.append('mutant %r broke the region parse rather than being caught' % name)
        elif all(ok for _, ok, _ in res):
            failures.append('mutant %r SURVIVED -- every check still passed' % name)

    # the substring hazard must be caught in the helper
    substr = good.replace('private static string StrategyIdentity(object v) { return v.ToString(); }',
                          'private static string StrategyIdentity(object v) { return v.ToString().Contains("x") ? "a" : "b"; }')
    _, ident, _ = evaluate(substr)
    if ident:
        failures.append('a Contains-based StrategyIdentity SURVIVED the substring check')

    # a condition present only in a COMMENT must not count
    commented = good.replace('effectiveStrategy = StrategyIdentity(GetP(props, "Strategy"));',
                             '// effectiveStrategy = StrategyIdentity(GetP(props, "Strategy"));')
    res, _, _ = evaluate(commented)
    if res is None or all(ok for _, ok, _ in res):
        failures.append('a commented-out read-back SURVIVED -- comments are not masked')

    # ORDERING, which no source mutant in the battery reaches: the resolve and the
    # write both exist in either order, so every OTHER check passes on this body.
    # This control is the only thing asserting the condition.
    reordered = '''
        private object Backtest(string body)
        {
            SetP(props, "Strategy", strategy);
            var stratTypeReq = FindStrategyType(strategy);
            if (stratTypeReq == null) { return new { error = "nope" }; }
            effectiveStrategy = StrategyIdentity(GetP(props, "Strategy"));
            if (!string.Equals(effectiveStrategy, stratTypeReq.Name, StringComparison.OrdinalIgnoreCase))
                paramErrors.Add(string.Format("Strategy did not take: {0}", strategy));
        }
        private static string StrategyIdentity(object v) { return v.ToString(); }
    '''
    res, _, _ = evaluate(reordered)
    if res is None:
        failures.append('the reordered control broke the region parse rather than failing')
    else:
        by_label = dict((label, ok) for label, ok, _ in res)
        if by_label.get('resolves BEFORE the shared window is written'):
            failures.append('a body that writes the window BEFORE resolving SURVIVED the '
                            'ordering check')
        others = [label for label, ok in by_label.items()
                  if label != 'resolves BEFORE the shared window is written' and not ok]
        if others:
            failures.append('the reordered control was caught by %r instead of by the '
                            'ordering check, so the ordering check is untested' % others)

    if failures:
        print('SELF-TEST FAILED -- this gate cannot be trusted:\n')
        for f in failures:
            print('  ' + f)
        sys.exit(1)


self_test()

if not os.path.exists(ADDON):
    print('FAILED: %s does not exist.' % ADDON)
    sys.exit(1)

text = open(ADDON, encoding='utf-8').read()
results, identity_ok, body_len = evaluate(text)

if results is None:
    print('FAILED: Backtest(string body) not found in addons/McpBridgeAddOn.cs -- '
          'did the method move or change signature?')
    sys.exit(1)

print('Backtest() strategy-selection contract '
      '(region inspected: %d chars of the Backtest method body):' % body_len)
bad = []
for label, ok, why in results:
    print('  [%s] %s' % ('OK  ' if ok else 'FAIL', label))
    if not ok:
        bad.append((label, why))

if identity_ok is None:
    print('  [FAIL] StrategyIdentity() not found')
    bad.append(('StrategyIdentity missing', 'the read-back has nothing to normalise it'))
else:
    print('  [%s] StrategyIdentity decides identity without a substring test'
          % ('OK  ' if identity_ok else 'FAIL'))
    if not identity_ok:
        bad.append(('substring identity',
                    "'MACrossOver' is a substring of 'SampleMACrossOver'"))

if bad:
    print('\nFAILED: Backtest() can run a strategy other than the one requested.')
    for label, why in bad:
        print('  - %s: %s' % (label, why))
    sys.exit(1)

print('\nOK: Backtest() resolves the name, refuses before touching the shared '
      'window, reads the selection back, and fails closed on a mismatch.')
sys.exit(0)
