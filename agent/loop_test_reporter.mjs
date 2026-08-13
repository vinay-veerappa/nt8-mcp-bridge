/**
 * agent/loop_test_reporter.mjs — run this repo's tests and print a summary the
 * agent-loop can actually parse.
 *
 * WHY THIS EXISTS. `agent_loop.gates.parse_tests` understands exactly two output
 * formats: the NT8 suite's (`[FAIL] msg` lines plus a final
 * `RESULTS: Passed = N, Failed = M`) and pytest's (`N passed, M failed`). Node's
 * test runner prints neither — it prints `ℹ pass 36` / `ℹ fail 3`. So the first
 * loop run on this profile died at baseline with "produced no parseable result
 * summary", before ever reaching a model.
 *
 * `Profile.test_runner_regex` looks like the intended configuration point for
 * exactly this, and it is DEAD: declared at `agent_loop/profiles.py:78` and read
 * by nothing in the package. That is `P1-83`'s defect class, in the tool rather
 * than in this repo.
 *
 * So this emits the NT8 shape, which is the better of the two targets because its
 * `[FAIL]` lines carry the failing test's NAME. The loop matches a ticket's
 * `expect_green` entries against those failure lines, so without per-failure names
 * the test-first gate would be vacuous — it could not tell which test went green.
 *
 * Verified against the real parser rather than by eye: `agent/verify_reporter.py`
 * feeds this file's output through `agent_loop.gates.parse_tests` and asserts on
 * what comes back.
 */
import { run } from 'node:test';
import { readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const testsDir = join(repoRoot, 'tests');

const files = readdirSync(testsDir)
  .filter((f) => f.endsWith('.test.js'))
  .map((f) => join(testsDir, f));

if (files.length === 0) {
  // Fail loudly. A reporter that prints "0 passed, 0 failed" when it found no
  // test files hands the loop a GREEN baseline for a suite that never ran, which
  // is worse than crashing.
  console.error('[FATAL] no *.test.js found in ' + testsDir);
  process.exit(2);
}

let passed = 0;
let failed = 0;

// Consumed with `for await` rather than with an 'end' listener. The 'end' event
// did not fire reliably here: the process exited having printed the [FAIL] lines
// but never the RESULTS line, which is the one thing this file exists to produce.
// Awaiting the stream to completion makes the summary unconditional.
for await (const event of run({ files, concurrency: 1 })) {
  switch (event.type) {
    case 'test:pass':
      // Node also emits a pass/fail event for each FILE as a whole; those carry a
      // nesting above 0 and would double-count.
      if (event.data.nesting === 0) passed += 1;
      break;
    case 'test:fail': {
      if (event.data.nesting !== 0) break;
      failed += 1;
      console.log(`[FAIL] ${event.data.name || '(unnamed test)'}`);
      const message = event.data.details?.error?.message;
      if (message) console.log(`       ${String(message).split('\n')[0]}`);
      break;
    }
    case 'test:stderr':
      process.stderr.write(event.data.message ?? '');
      break;
    default:
      break;
  }
}

console.log('====================================================');
console.log(`RESULTS: Passed = ${passed}, Failed = ${failed}`);
console.log('====================================================');
process.exitCode = failed === 0 ? 0 : 1;
