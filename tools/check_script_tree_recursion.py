"""NT8 compiles bin\\Custom\\Strategies and bin\\Custom\\Indicators RECURSIVELY.

Six handlers in `McpBridgeAddOn.cs` did not. `Directory.GetFiles(dir, "*.cs")`
defaults to `TopDirectoryOnly`, so every list and every existence check read only
the top level.

MEASURED 2026-09-04: `nt_list_strategies` reported 27 files. There are 59. The 32
it omitted live in six subfolders -- Vinay, PriceAction, RajAlgos,
bcomasStrategies, TradeSaberStrategies, TrendIsYourFriend -- and Vinay is where
this user's own bots are. That listing was read as evidence the bots were NOT
DEPLOYED, and a deploy was very nearly repeated on a live box on the strength of
it. An empty answer and an unsearched folder look identical.

THE CREATE PATH IS THE SEVERE ONE, and it is why this is a gate rather than a
comment. `CreateStrategy`/`CreateIndicator` checked `File.Exists` at the top level
only, so creating `Strategies/Foo.cs` while `Strategies/Vinay/Foo.cs` exists
writes a SECOND definition of the same class. NT8 then fails to compile the whole
Custom assembly, which stops EVERY addon loading -- the risk guard included --
and the only symptom is a deploy that had no effect, which looks exactly like one
that worked. The consumer repo hit this identical trap with indicators, where a
top-level-only check would have written 23 duplicate class definitions beside
copies already sitting in Indicators/Vinay and Indicators/RedTail.

WHAT THIS ASSERTS:

  1. the shared helpers really recurse, and no handler is left on a bare
     top-level `Directory.GetFiles`;
  2. both create paths consult the whole tree and REFUSE on a collision rather
     than overwriting -- `overwrite` means "replace the file I named", and a
     same-named file in another folder is a different file;
  3. both read paths report AMBIGUOUS rather than silently taking the first of
     several matches.

C# comments are masked before every search, and each condition has a negative
control in `self_test()`.

Exits 1 if any handler can see only the top level.
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ADDON = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

LIST_METHODS = ['private object ListStrategies(', 'private object ListIndicators(']
READ_METHODS = ['private object GetStrategySource(', 'private object GetIndicatorSource(']
CREATE_METHODS = ['private object CreateStrategy(', 'private object CreateIndicator(']


def mask_comments(text):
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
    """Brace-matched body. Parsed, not split: a `}` inside a string literal must
    not end the region."""
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


BARE_GETFILES = re.compile(
    r'Directory\.GetFiles\s*\((?:(?!AllDirectories)[^;])*?\)\s*[;.\n]', re.S)
RECURSES = re.compile(
    r'ListScriptsRecursive\s*\(|FindScriptFiles\s*\(|SearchOption\.AllDirectories')

HELPERS = (('ListScriptsRecursive', 'ListScriptsRecursive(string root)'),
           ('FindScriptFiles', 'FindScriptFiles(string root, string name)'))


def _refusal_is_reachable(body):
    """The collision list must be TESTED, not merely computed.

    The first version of this check asked whether `FindScriptFiles(...)` appeared
    within 600 characters of the word `refused`. Both survive `if (false)`, so the
    two most severe mutants in the battery -- CreateStrategy and CreateIndicator
    silently accepting a cross-folder collision -- passed it. That is the
    documented failure mode of source gates in this repo: asserting a value is
    COMPUTED when the thing that matters is that it is USED, in a branch that can
    actually be taken.

    So: find the variable assigned from FindScriptFiles, then require a guard that
    tests THAT variable's count and refuses. `if (false)` no longer matches,
    because `false` is not the variable.
    """
    if body is None:
        return False
    m = re.search(r'\bvar\s+(\w+)\s*=\s*FindScriptFiles\s*\(', body)
    if m is None:
        return False
    var = m.group(1)
    guard = re.compile(
        r'if\s*\(\s*' + re.escape(var) + r'\s*\.\s*Count\s*(?:>\s*0|!=\s*0|>=\s*1)\s*\)'
        r'.{0,600}?\brefused\b', re.S)
    return guard.search(body) is not None


def evaluate(text):
    masked = mask_comments(text)
    results = []

    for helper, sig in HELPERS:
        body = method_body(masked, sig)
        results.append(('%s recurses' % helper,
                        body is not None and 'SearchOption.AllDirectories' in body,
                        'every handler leans on this shared helper'))

    for sig in LIST_METHODS + READ_METHODS + CREATE_METHODS:
        name = sig.split()[-1].rstrip('(')
        body = method_body(masked, sig)
        if body is None:
            results.append(('%s exists' % name, False, 'handler not found -- renamed?'))
            continue
        bare = BARE_GETFILES.search(body) is not None
        results.append(('%s sees the whole tree' % name,
                        (not bare) and RECURSES.search(body) is not None,
                        'a top-level-only read makes an unsearched folder look empty'))

    for sig in CREATE_METHODS:
        name = sig.split()[-1].rstrip('(')
        body = method_body(masked, sig)
        results.append(('%s REFUSES a cross-folder collision' % name,
                        _refusal_is_reachable(body),
                        'a second class definition fails the whole Custom assembly '
                        'and stops every addon loading'))

    for sig in READ_METHODS:
        name = sig.split()[-1].rstrip('(')
        body = method_body(masked, sig)
        ok = body is not None and 'AMBIGUOUS' in body
        results.append(('%s reports AMBIGUOUS on duplicates' % name, ok,
                        'silently picking one of several hides the collision'))

    return results


def self_test():
    failures = []

    good = '''
        private static List<object> ListScriptsRecursive(string root)
        { return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).ToList(); }
        private static List<string> FindScriptFiles(string root, string name)
        { return Directory.GetFiles(root, name + ".cs", SearchOption.AllDirectories).ToList(); }
        private object ListStrategies() { var list = ListScriptsRecursive(dir); return list; }
        private object ListIndicators() { var list = ListScriptsRecursive(dir); return list; }
        private object GetStrategySource(string name)
        { var hits = FindScriptFiles(d, n); if (hits.Count > 1) return new { error = "AMBIGUOUS" }; return hits[0]; }
        private object GetIndicatorSource(string name)
        { var hits = FindScriptFiles(d, n); if (hits.Count > 1) return new { error = "AMBIGUOUS" }; return hits[0]; }
        private object CreateStrategy(string body)
        { var e = FindScriptFiles(d, n); if (e.Count > 0) return new { error = "refused: exists" }; return 1; }
        private object CreateIndicator(string body)
        { var e = FindScriptFiles(d, n); if (e.Count > 0) return new { error = "refused: exists" }; return 1; }
    '''
    res = evaluate(good)
    if not all(ok for _, ok, _ in res):
        failures.append('the intended shape does not PASS: %r'
                        % [l for l, ok, _ in res if not ok])

    mutants = {
        'helper stops recursing':
            good.replace('Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)',
                         'Directory.GetFiles(root, "*.cs")'),
        'a list handler goes top-level-only':
            good.replace('private object ListStrategies() { var list = ListScriptsRecursive(dir); return list; }',
                         'private object ListStrategies() { var list = Directory.GetFiles(dir, "*.cs"); return list; }'),
        'create stops refusing a collision':
            good.replace('{ var e = FindScriptFiles(d, n); if (e.Count > 0) return new { error = "refused: exists" }; return 1; }\n        private object CreateIndicator',
                         '{ var e = FindScriptFiles(d, n); return 1; }\n        private object CreateIndicator'),
        # THE MUTANT THAT BEAT THE FIRST VERSION OF THIS GATE. The FindScriptFiles
        # call and the word "refused" both survive `if (false)`; only the
        # REACHABILITY of the branch changes, and the first check could not see it.
        # Both create mutants in the battery passed against that version.
        'create neuters the refusal to if (false)':
            good.replace('if (e.Count > 0) return new { error = "refused: exists" }',
                         'if (false) return new { error = "refused: exists" }'),
        'read silently takes the first match':
            good.replace('{ var hits = FindScriptFiles(d, n); if (hits.Count > 1) return new { error = "AMBIGUOUS" }; return hits[0]; }\n        private object GetIndicatorSource',
                         '{ var hits = FindScriptFiles(d, n); return hits[0]; }\n        private object GetIndicatorSource'),
    }
    for name, mutant in mutants.items():
        if all(ok for _, ok, _ in evaluate(mutant)):
            failures.append('mutant %r SURVIVED' % name)

    commented = good.replace('Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)',
                             'Directory.GetFiles(root, "*.cs" /* SearchOption.AllDirectories */)')
    if all(ok for _, ok, _ in evaluate(commented)):
        failures.append('a commented-out AllDirectories SURVIVED -- comments are not masked')

    if failures:
        print('SELF-TEST FAILED -- this gate cannot be trusted:\n')
        for f in failures:
            print('  ' + f)
        sys.exit(1)


self_test()

if not os.path.exists(ADDON):
    print('FAILED: %s does not exist.' % ADDON)
    sys.exit(1)

results = evaluate(open(ADDON, encoding='utf-8').read())
print('Script-tree recursion (NT8 compiles Strategies/ and Indicators/ recursively):')
bad = []
for label, ok, why in results:
    print('  [%s] %s' % ('OK  ' if ok else 'FAIL', label))
    if not ok:
        bad.append((label, why))

if bad:
    print('\nFAILED: a handler can see only the top level of a tree NT8 compiles whole.')
    for label, why in bad:
        print('  - %s: %s' % (label, why))
    sys.exit(1)

print('\nOK: every script-tree handler searches the whole tree, both create paths '
      'refuse a cross-folder collision, and both read paths report duplicates.')
sys.exit(0)
