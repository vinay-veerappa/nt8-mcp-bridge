"""P2-158, made mechanical: no broker call directly inside a `lock (_stateLock)` block.

The one invariant whose violation is a deadlock or a stranded position: `_stateLock` is never held
across a broker call. `RiskGuardAddOn` is one re-entrant lock serialising the FSM; a Flatten / Cancel
/ Submit / CreateOrder made while holding it can block the guard (a broker call that waits) or race
the very state the lock protects. P1-157 slipped a `Cancel` under the lock exactly this way.

Until now the check existed ONLY in the agent-loop review profile, so a HAND-WRITTEN change -- the
common case -- got no lock-scope review at all. This ports it to a CI gate. [[a-gate-is-per-repo]]:
the SAME script ships in nt8-riskguard and nt8-mcp-bridge, so the invariant is enforced in whichever
repo grows a `_stateLock`. The bridge has none today; the gate there finds zero blocks and says so,
and its self-test still supplies a synthetic violation so it is ARMED rather than vacuously green
([[closing-the-last-instance-disarms-the-gate]] -- a check over an empty set that cannot fail is not
a check).

⚠️ SCOPE, stated plainly. This is a SYNTACTIC gate: it flags a risk call written DIRECTLY inside a
`lock (_stateLock) { ... }` body (any nesting of if/for within it). It does NOT follow calls into
helper methods -- a regex cannot see reachability ([[a-source-gate-must-assert-the-condition]]). That
is deliberately the shape of P1-157 (a bare `.Cancel(` under the lock) and it is the shape a
drive-by edit takes. The semantic, transitive cases the review panel argues every round
(ArmGraceTimer, SeedFsmsForExistingPositions, MarkRuleLockout via IsActingMode) are METHOD calls that
make no broker call, so they never match the four risk patterns and need no allowlist entry here.

⚠️ FAILURE DIRECTION. The dangerous miss is a real broker call read as safe. So comments and strings
are masked (a `.Submit(` named in a log line is not a call), but dead branches are NOT stripped: a
risk call parked in `if (false)` under the lock is flagged, because a loud false alarm is cheaper
than a silent miss on this invariant.

Exits 1 on any risk call directly inside a `lock (_stateLock)` block that is not in ALLOWLIST.
"""
import os
import re
import sys

# ⚠️ Windows defaults stdout to cp1252; a finding here is full of non-ASCII and prints only on
# FAILURE, so without this the gate dies exactly when it has something to say.
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ADDONS = os.path.join(REPO, 'addons')

# Matches the agent-loop profile (agent/nt8_riskguard.py): the one lock, and the four broker calls.
LOCK_NAME = '_stateLock'
RISK_CALLS = ('Flatten', 'Cancel', 'Submit', 'CreateOrder')
RISK_RE = re.compile(r'\.\s*(' + '|'.join(RISK_CALLS) + r')\s*\(')
LOCK_HEAD = re.compile(r'\block\s*\(\s*' + re.escape(LOCK_NAME) + r'\s*\)')

# Risk calls known to be acceptable directly under the lock, each with the reason. Empty today: the
# addon QUEUES orphan cancels (UpdateFsmOnPosition -> _pendingCancels under the lock, DrainPendingCancels
# after release, P1-35) rather than calling inline, so no real site matches. An entry added here MUST
# name why the call cannot block or race, and the gate fails if a listed site later disappears.
# Format: (basename, line_number_hint_ignored, "reason"). Keyed on the exact source line text.
ALLOWLIST = {
    # "acct.Submit(...)": "reason it cannot block/race under the lock",
}


def mask_comments_and_strings(text):
    """Comments and string literals -> spaces, newlines kept so line numbers are unchanged. A risk
    call NAMED in a doc comment or a log string is not a call. (Duplicated from
    check_no_dead_safety_machinery.py deliberately: each safety gate carries its own tested copy of
    this so a change to one cannot silently break another. The self-test guards it.)"""
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


def lock_blocks(masked):
    """Yield (start_offset, end_offset) of each `lock (_stateLock) { ... }` body, brace-matched."""
    for m in LOCK_HEAD.finditer(masked):
        j = m.end()
        while j < len(masked) and masked[j] in ' \t\r\n':
            j += 1
        if j >= len(masked) or masked[j] != '{':
            continue  # a single-statement lock body holds no risk call worth a block scan
        depth = 0
        start = j
        while j < len(masked):
            if masked[j] == '{':
                depth += 1
            elif masked[j] == '}':
                depth -= 1
                if depth == 0:
                    yield (start, j + 1)
                    break
            j += 1


def line_of(text, offset):
    return text.count('\n', 0, offset) + 1


def violations_in(text):
    """(line, source_line_text) for each risk call directly inside a _stateLock block."""
    masked = mask_comments_and_strings(text)
    lines = text.splitlines()
    found = []
    for start, end in lock_blocks(masked):
        for rm in RISK_RE.finditer(masked, start, end):
            ln = line_of(masked, rm.start())
            src = lines[ln - 1].strip() if 0 <= ln - 1 < len(lines) else ''
            if src in ALLOWLIST:
                continue
            found.append((ln, src))
    return found


def self_test():
    """⚠️ THE NEGATIVE CONTROL, run on every invocation, and the reason the bridge copy is not
    vacuous. The dangerous direction is a FALSE PASS: a real broker call under the lock read as safe.
    So a synthetic violation MUST be caught, and a call outside the block / in a comment MUST NOT."""
    violating = 'void M() { lock (_stateLock) {\n    acct.Submit(orders);\n} }'
    nested = 'void M() { lock (_stateLock) {\n    if (x) { acct.Cancel(o); }\n} }'
    outside = 'void M() { lock (_stateLock) { PrepareIntent(); }\n acct.Submit(orders); }'
    commented = 'void M() { lock (_stateLock) {\n    // acct.Submit(orders) -- queued, not here\n    Queue();\n} }'
    in_string = 'void M() { lock (_stateLock) {\n    Log("do not acct.Submit(x) here");\n    Queue();\n} }'
    other_lock = 'void M() { lock (_otherLock) {\n    acct.Submit(orders);\n} }'

    problems = []
    if not violations_in(violating):
        problems.append('a bare Submit directly under lock(_stateLock) was NOT flagged -- the gate '
                        'would miss P1-157 verbatim')
    if not violations_in(nested):
        problems.append('a Cancel nested in an if under the lock was NOT flagged -- nesting must not '
                        'hide it')
    for label, src in (('a call after the block closes', outside),
                       ('a call in a comment', commented),
                       ('a call in a string literal', in_string),
                       ('a call under a DIFFERENT lock', other_lock)):
        if violations_in(src):
            problems.append('%s was flagged -- a false alarm that direction trains operators to '
                            'ignore the gate' % label)
    if problems:
        print('SELF-TEST FAILED -- this gate cannot be trusted:\n')
        for p in problems:
            print('  * ' + p)
        sys.exit(1)


self_test()

if not os.path.isdir(ADDONS):
    print('OK: no addons/ directory in this repo; nothing to scan. (Gate armed via self-test.)')
    sys.exit(0)

sources = {}
for fname in sorted(os.listdir(ADDONS)):
    if fname.endswith('.cs'):
        path = os.path.join(ADDONS, fname)
        sources[path] = open(path, encoding='utf-8').read()

total_blocks = 0
failures = []
print('Broker-call-under-%s scan of addons/:\n' % LOCK_NAME)
for path, text in sorted(sources.items()):
    masked = mask_comments_and_strings(text)
    n_blocks = sum(1 for _ in lock_blocks(masked))
    total_blocks += n_blocks
    viols = violations_in(text)
    if viols:
        for ln, src in viols:
            failures.append('%s:%d  %s' % (os.path.basename(path), ln, src))

print('    %d lock (%s) block(s) inspected across %d file(s).'
      % (total_blocks, LOCK_NAME, len(sources)))

if failures:
    print('\nFAILED: a broker call is made directly while holding %s. This can block the guard or '
          'race the state the lock protects (P1-157). Collect intent under the lock and execute the '
          'broker call after releasing it (the addon queues cancels via DrainPendingCancels). If a '
          'site genuinely cannot block or race, add its exact source line to ALLOWLIST with the '
          'reason.\n' % LOCK_NAME)
    for f in failures:
        print('  * ' + f)
    sys.exit(1)

if total_blocks == 0:
    print('\nOK: this repo holds no %s (the bridge does not). The gate is armed for the day it does; '
          'its detector is proven by the self-test above.' % LOCK_NAME)
else:
    print('\nOK: no broker call is made while holding %s.' % LOCK_NAME)
sys.exit(0)
