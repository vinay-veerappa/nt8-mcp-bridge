"""Mutation battery for P1-90 (the bridge must not invent an account).

THE FIRST MUTATION BATTERY IN THIS REPO. Every one lived in nt8-riskguard until
now, because nothing here was executable (`P2-27`). `addons/BridgeAccountResolver.cs`
changes that for the resolver: it names no NinjaTrader type, so the test project
compiles and RUNS it.

That split is the thing to understand before reading the mutants. This battery has
two halves, and they prove different amounts:

  * MUTANTS 1-7 mutate the RESOLVER and are killed by tests that EXECUTE it. Real
    behavioural coverage.

  * MUTANTS 8-11 mutate the CALL SITES in McpBridgeAddOn.cs, which no test build
    compiles, so they can only be killed by a source assertion. Weaker, and stated
    as such -- but they are the half that matters for the defect, because the
    resolver being right proves nothing about whether the six sites use it. That
    is the exact gap P1-69 shipped through (fixed in one of two read branches) and
    P1-75 after it.

Why particular mutants are here:

  * MUTANT 2 refuses and ALSO hands back the name. Every "was it refused?"
    assertion still passes, and a caller that checks Error second finds a usable
    account sitting next to the refusal. That is P0-68's shape -- the unchanged
    price was already in the response body next to the success claim.

  * MUTANT 3 resolves a case-ambiguous name to whichever came first. That is P1-90
    again, smaller: the function choosing between accounts on the caller's behalf.

  * MUTANT 6 narrows the emptiness test to a null check, so `"   "` is reported as
    NOT FOUND rather than as MISSING. It still refuses, so it survived the first
    draft of these tests; the assertion that the reason distinguishes the two was
    added because of it. Missing and blank are different inputs (P1-85).

  * MUTANT 9 restores the guess at ONE of the three order sites. A fix applied to
    two of three is the shape this repo keeps finding.

  * MUTANT 11 removes the resolver call at a site WITHOUT restoring "Sim101", so
    the literal-absence assertions all still pass. It asks whether the positive
    "all six sites route through the resolver" evidence is real.

A crash or a missing result line counts as a kill (handover section 5.14).

Exits non-zero on any survivor, and exits 2 rather than running against a red
baseline -- a red baseline scores every mutant KILLED and proves nothing.
"""
import os
import re
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
RESOLVER = os.path.join(REPO, 'addons', 'BridgeAccountResolver.cs')
BRIDGE = os.path.join(REPO, 'addons', 'McpBridgeAddOn.cs')

# (target file, description, find, replace)
MUTANTS = [
    # ---- the resolver: killed by tests that EXECUTE it ----
    (RESOLVER,
     "an omitted account resolves to Sim101 again -- the defect, in its original form",
     '            if (string.IsNullOrEmpty(name))\n            {\n                return BridgeAccountResolution.Refuse(string.Format(',
     '            if (string.IsNullOrEmpty(name))\n            {\n                return BridgeAccountResolution.Resolved("Sim101");\n                #pragma warning disable 0162\n                return BridgeAccountResolution.Refuse(string.Format('),

    (RESOLVER,
     "a refusal ALSO carries the name, so a caller that checks Error second finds a usable\n"
     "     account next to the refusal (P0-68's shape)",
     '        internal static BridgeAccountResolution Refuse(string error)\n        {\n            return new BridgeAccountResolution(null, error);',
     '        internal static BridgeAccountResolution Refuse(string error)\n        {\n            return new BridgeAccountResolution("Sim101", error);'),

    (RESOLVER,
     "two accounts differing only in case resolve to whichever came first -- P1-90 again,\n"
     "     smaller, with the function choosing on the caller's behalf",
     '            if (matches.Count > 1)\n            {',
     '            if (matches.Count > 1)\n            {\n                return BridgeAccountResolution.Resolved(matches[0]);'),

    (RESOLVER,
     "the exact-match pass becomes case-insensitive, so it can no longer break a tie and\n"
     "     the ambiguity refusal is unreachable",
     'var exact = available.FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal));',
     'var exact = available.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));'),

    (RESOLVER,
     "the name is no longer trimmed, so '  Sim101  ' reads as an account that does not exist",
     'var name = requested == null ? null : requested.Trim();',
     'var name = requested;'),

    (RESOLVER,
     "the emptiness test narrows to a null check, so '   ' is reported NOT FOUND rather than\n"
     "     MISSING. It still refuses -- this survived the first draft of the tests",
     '            if (string.IsNullOrEmpty(name))',
     '            if (name == null)'),

    (RESOLVER,
     "a null account list throws instead of refusing",
     'var available = (availableNames ?? Enumerable.Empty<string>())',
     'var available = (availableNames)'),

    # ---- the call sites: source assertions only (P2-27) ----
    (BRIDGE,
     "PlaceOrder guesses again -- the full original chain, restored",
     '            var resolution = BridgeAccountResolver.ResolveOrRefuse(\n                reqAccount, Account.All.Select(a => a.Name), "place an order");\n            if (resolution.Refused) return new { error = resolution.Error };\n            Account account = Account.All.FirstOrDefault(a => a.Name == resolution.Name);',
     '            Account account = Account.All.FirstOrDefault(a => a.Name == "Sim101")\n                          ?? Account.All.FirstOrDefault(a => !a.Name.Equals("Backtest", StringComparison.OrdinalIgnoreCase))\n                          ?? Account.All.FirstOrDefault();'),

    (BRIDGE,
     "only the OCO site guesses again -- a fix applied to two of three sites is the shape\n"
     "     this repo keeps finding",
     'reqAccount, Account.All.Select(a => a.Name), "place an OCO order");',
     'reqAccount ?? "Sim101", Account.All.Select(a => a.Name), "place an OCO order");'),

    (BRIDGE,
     "the LOCKOUT site guesses again. This one takes the name into UnlockAccount, which\n"
     "     REMOVES protection",
     '                req.Str("account") ?? req.Str("Account"),\n                Account.All.Select(a => a.Name),\n                "read or clear a lockout");',
     '                req.Str("account") ?? req.Str("Account") ?? "Sim101",\n                Account.All.Select(a => a.Name),\n                "read or clear a lockout");'),

    (BRIDGE,
     "the compliance site stops using the resolver WITHOUT restoring \"Sim101\", so every\n"
     "     literal-absence assertion still passes. Asks whether the positive routing\n"
     "     evidence is real",
     '            var complianceResolution = BridgeAccountResolver.ResolveOrRefuse(\n                accountName, Account.All.Select(a => a.Name), "report compliance");\n            if (complianceResolution.Refused)\n                return new { error = complianceResolution.Error };\n            string accName = complianceResolution.Name;',
     '            string accName = accountName;'),
]


def run():
    build = subprocess.run(
        ['dotnet', 'build', 'BridgeTests.csproj', '-v', 'q', '--nologo'],
        cwd=os.path.join(REPO, 'tests'), capture_output=True, text=True)
    if build.returncode != 0:
        return 'BUILD FAILED'
    res = subprocess.run(
        ['dotnet', 'run', '--project', 'BridgeTests.csproj', '--no-build'],
        cwd=os.path.join(REPO, 'tests'), capture_output=True, text=True)
    m = re.search(r'Passed = \d+, Failed = \d+', res.stdout)
    return m.group(0) if m else 'NO RESULT LINE'


ORIGINALS = {p: open(p, encoding='utf-8').read() for p in (RESOLVER, BRIDGE)}


def restore():
    for p, text in ORIGINALS.items():
        open(p, 'w', encoding='utf-8', newline='').write(text)


print('=== baseline ===')
baseline = run()
print(' ', baseline)

m = re.search(r'Passed = (\d+), Failed = (\d+)', baseline)
if not m:
    print('\nREFUSING TO RUN: could not read a result line from the baseline.')
    sys.exit(2)
if int(m.group(2)) != 0:
    print('\nREFUSING TO RUN: baseline is RED (%s failing). Every mutant would score KILLED '
          'on pre-existing failures and this battery would prove nothing.' % m.group(2))
    sys.exit(2)

survivors = []
for target, name, old, new in MUTANTS:
    original = ORIGINALS[target]
    if original.count(old) != 1:
        print('  [SKIP] %s: anchor matched %d times' % (name, original.count(old)))
        survivors.append(name + ' (ANCHOR)')
        continue
    open(target, 'w', encoding='utf-8', newline='').write(original.replace(old, new))
    res = run()
    mm = re.search(r'Failed = (\d+)', res)
    killed = ('BUILD FAILED' in res) or ('NO RESULT LINE' in res) \
        or (mm is not None and int(mm.group(1)) > 0)
    print('  [%s] %s: %s' % ('KILLED' if killed else 'SURVIVED', name, res))
    if not killed:
        survivors.append(name)
    restore()

restore()
print('\nrestored originals;', run())
print('\nSURVIVORS:', survivors if survivors else 'none')

sys.exit(1 if survivors else 0)
