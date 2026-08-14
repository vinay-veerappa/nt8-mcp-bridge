"""Assert that agent/loop_test_reporter.mjs emits something the LOOP can parse.

Why this is a script and not a note in a commit message: the reporter exists only
to satisfy `agent_loop.gates.parse_tests`, and "it looks like the NT8 format" is
not evidence. A reporter that emits an almost-right summary hands the loop either
a false green baseline or an ERROR before it reaches a model, and the first run on
this profile died exactly that way.

So this feeds the reporter's real output through the real parser and asserts on
what comes back.

Run it with tvDownloadOHLC's venv, from this repo root:
    "C:/Users/vinay/tvDownloadOHLC/.venv/Scripts/python.exe" agent/verify_reporter.py

Exits 0 if the parser agrees with the reporter, 1 if not, 2 if it could not run.
"""
from __future__ import annotations

import subprocess
import sys

try:
    from agent_loop.gates import parse_tests
except ImportError:
    print("CANNOT RUN: agent_loop is not importable. Use tvDownloadOHLC's venv. "
          "SKIPPED, not passed.")
    sys.exit(2)

proc = subprocess.run(
    ["node", "agent/loop_test_reporter.mjs"],
    capture_output=True, text=True,
)
out = proc.stdout + proc.stderr
outcome = parse_tests(out)

print("reporter exit code : %s" % proc.returncode)
print("parser: ran        : %s" % outcome.ran)
print("parser: passed     : %s" % outcome.passed)
print("parser: failed     : %s" % outcome.failed)
print("parser: errors     : %s" % getattr(outcome, "errors", 0))
print("parser: failures   :")
for f in sorted(outcome.failures):
    print("    - %s" % f)

problems = []

# `ran` false is the exact failure this reporter was written to fix: the loop
# raises "produced no parseable result summary" and refuses the ticket.
if not outcome.ran:
    problems.append("parser reports ran=False -- the RESULTS line is missing or malformed")

# A suite-level error is reported separately by the loop and aborts the baseline
# with a different message, so it must be zero here.
if getattr(outcome, "errors", 0):
    problems.append("parser reports suite-level errors, which aborts the baseline")

# The counts must be real. A reporter that finds no test files and prints
# 0 passed / 0 failed would satisfy `ran` and hand over a green baseline for a
# suite that never executed.
if outcome.passed == 0 and outcome.failed == 0:
    problems.append("parser sees 0 passed and 0 failed -- did the reporter find any tests?")

# The per-failure NAMES are the part that matters. The loop matches a ticket's
# expect_green entries against these; without them the test-first gate cannot
# tell which test went green and is vacuous.
if outcome.failed and not outcome.failures:
    problems.append(
        "parser extracted NO failure names despite %d failing test(s) -- "
        "expect_green would match nothing and the test-first gate would be "
        "vacuous" % outcome.failed
    )

# The exit code must track the result, or a red suite reads as a passing command.
if outcome.failed and proc.returncode == 0:
    problems.append("reporter exited 0 with failing tests")
if not outcome.failed and proc.returncode != 0:
    problems.append("reporter exited non-zero with no failing tests")

print()
if problems:
    for p in problems:
        print("FAIL: %s" % p)
    sys.exit(1)

print("OK: the loop's own parser reads this reporter's output "
      "(%d passed, %d failed, %d failure name(s) extracted)."
      % (outcome.passed, outcome.failed, len(outcome.failures)))
sys.exit(0)
