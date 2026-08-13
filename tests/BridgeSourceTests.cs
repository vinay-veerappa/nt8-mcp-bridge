// Test harness for nt8-mcp-bridge.
//
// These three assertions came from TestP2_38 in nt8-riskguard's suite, where they were
// stranded: they assert on McpBridgeAddOn.cs's source text, but that file is not in that
// repo any more, and keeping them there meant the CORE asserting on the BRIDGE -- the one
// dependency direction the split exists to forbid. They live here now, next to the file
// they are about.
//
// They are SOURCE-TEXT assertions, and that is a real limitation, stated plainly: this
// harness does not execute a single line of McpBridgeAddOn.cs. It cannot yet -- see
// tests/README.md for the measured reason and the ordered remedy. A source assertion
// proves less than an execution would, but it proves the exact thing that regressed in
// P2-38: that no sim/live gate keys on an account NAME, and that all four gates route
// through the one shared classifier.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeSourceTests
    {
        private static int _passed;
        private static int _failed;
        private static int _testsRun;

        private static void Assert(bool condition, string message)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + message);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FAIL] " + message);
            }
        }

        // Anchored on this file's own location rather than the working directory, so the
        // runner behaves the same however it was launched.
        private static string BridgeSourcePath(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            return Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "addons", "McpBridgeAddOn.cs"));
        }

        private static string StripComments(string source)
        {
            // Comments are stripped before matching. The seam's own doc comments quote the
            // defective pattern verbatim, and that documentation is worth keeping -- a check
            // that forbids describing the bug it prevents is a check that gets the comment
            // deleted instead.
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return string.Join("\n", source
                .Split('\n')
                .Select(l => { int i = l.IndexOf("//"); return i >= 0 ? l.Substring(0, i) : l; }));
        }

        // P2-38. Three deploy/order gates classified an account as simulated with
        // `Name.StartsWith("Sim") || Provider.Contains("imulat")`, and a fourth used the
        // name alone. The provider test is correct; OR-ing a name prefix in front of it
        // means a funded account called "SimpsonFund" is treated as simulated and can be
        // deployed to, and traded on, without confirmLive=true. Same root cause as P1-20.
        //
        // The behavioural half of P2-38 -- that the shared classifier gets "SimpsonFund"
        // right -- stays in nt8-riskguard, where the classifier lives and can be executed.
        private static void TestP2_38_NoBridgeGateClassifiesByAccountName()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-38: no sim/live gate in the bridge classifies by account name");

            var path = BridgeSourcePath();
            Assert(File.Exists(path), string.Format("The bridge source is readable at {0}", path));
            if (!File.Exists(path))
                return;

            var code = StripComments(File.ReadAllText(path));

            var nameGate = new Regex(
                @"isSim\s*=\s*[^;]*Name\s*\.\s*StartsWith", RegexOptions.Singleline);
            Assert(!nameGate.IsMatch(code),
                "No sim/live gate in the bridge classifies by account name any more.");

            int shared = Regex.Matches(code, @"IsSimulationAccount\(").Count;
            Assert(shared >= 4,
                string.Format(
                    "All four gates use the shared classifier (found {0}). Two definitions of "
                    + "'simulated' drift, and the one that drifts is the one nobody is testing.",
                    shared));
        }

        // The vendored core is a compile-time dependency, not a convenience: NT8 has no
        // package manager, so every AddOn compiles into one assembly and calls the others'
        // types directly. Deploying the bridge without the core produces an assembly that
        // does not build, which in NT8 means EVERY addon stops loading -- including the risk
        // guard. That is why tools/deploy.py refuses to half-deploy, and why this is checked
        // here rather than trusted.
        private static void TestVendoredCoreIsPresentAndPinned()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] the vendored core is present (the bridge cannot compile without it)");

            var repoRoot = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(BridgeSourcePath()), ".."));
            var vendorAddons = Path.Combine(repoRoot, "vendor", "nt8-riskguard", "addons");

            Assert(Directory.Exists(vendorAddons),
                string.Format("vendor/nt8-riskguard/addons exists at {0}. If this fails, run "
                    + "`git submodule update --init`.", vendorAddons));
            if (!Directory.Exists(vendorAddons))
                return;

            foreach (var required in new[] { "RiskGuardAddOn.cs", "TradeCopierEngine.cs", "DynamicAtmManager.cs" })
            {
                Assert(File.Exists(Path.Combine(vendorAddons, required)),
                    string.Format("the vendored core provides {0}", required));
            }
        }

        // The bridge must never be the thing the core depends on, and it must not carry its
        // own copy of a core source either -- that was P2-28's shape, four copies drifting.
        private static void TestBridgeCarriesNoCopyOfACoreSource()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] the bridge carries no copy of a vendored core source");

            var addonsDir = Path.GetDirectoryName(BridgeSourcePath());
            var repoRoot = Path.GetFullPath(Path.Combine(addonsDir, ".."));
            var vendorAddons = Path.Combine(repoRoot, "vendor", "nt8-riskguard", "addons");
            if (!Directory.Exists(vendorAddons))
            {
                Assert(false, "vendored core missing, cannot check for duplicate sources");
                return;
            }

            var coreNames = Directory.GetFiles(vendorAddons, "*.cs").Select(Path.GetFileName).ToList();
            var ourNames = Directory.GetFiles(addonsDir, "*.cs").Select(Path.GetFileName).ToList();
            var duplicates = ourNames.Where(n => coreNames.Contains(n)).ToList();

            Assert(duplicates.Count == 0,
                string.Format("addons/ duplicates no core source (found {0}{1})",
                    duplicates.Count,
                    duplicates.Count > 0 ? ": " + string.Join(", ", duplicates) : ""));
        }

        // P1-80. A write path that REPORTS SUCCESS and configures nothing, ever.
        //
        // `RiskGuardConfig`'s read half already refuses when the guard is not loaded --
        // `{ error = "RiskGuardAddOn not loaded" }`. Its WRITE half did not: it stashed the
        // operator's risk config in a `Dictionary<string, JObject>` called `_riskGuardConfig`,
        // wrote that to `RiskGuard/riskguard_config.json`, and returned
        // `success = true, status = "persisted_only"`.
        //
        // **Nothing ever read it back.** The dictionary was declared, loaded from disk at
        // startup, and written to. It was never consumed by anything, so the config was not
        // applied then, was not applied at the next startup, and would never be applied. The
        // note said "NOT applied to a live engine", which is true about *now* and reads as
        // "it will be picked up later". It would not.
        //
        // Found on the live box, where that file held `trailingDrawdown: 500,
        // maxPositionCap: 5` from 2026-07-30 while the live config ran 1500 and 10 -- a file
        // stating a limit 3x TIGHTER than the one actually enforced. That is
        // CONFIGURED-and-not-EVALUATED, the same state as P1-77 and P?-64.
        //
        // The fix is deletion, not wiring: the fallback now refuses, matching the read half.
        // A read that refuses and a write that pretends is the asymmetry that hid this.
        private static void TestP1_80_NoWritePathPersistsRiskConfigNothingReads()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-80: the bridge has no risk-config write that succeeds without applying");

            var path = BridgeSourcePath();
            Assert(File.Exists(path), string.Format("The bridge source is readable at {0}", path));
            if (!File.Exists(path))
                return;

            var code = StripComments(File.ReadAllText(path));

            Assert(!code.Contains("riskguard_config.json"),
                "no code path names riskguard_config.json -- the store nothing read is gone, "
                + "not merely bypassed");

            Assert(!code.Contains("_riskGuardConfig"),
                "the write-only _riskGuardConfig dictionary is deleted rather than left "
                + "loaded-and-unused, which is what made it look like a working store");

            Assert(!code.Contains("persisted_only"),
                "no write reports success for config it did not apply");

            // The positive half. Without it, deleting the whole method would pass.
            Assert(Regex.Matches(code, @"RiskGuardAddOn not loaded").Count >= 2,
                "the write half now REFUSES exactly as the read half already did (both arms "
                + "present) -- a read that refuses beside a write that pretends is the "
                + "asymmetry that hid this for a month");
        }

        // UI7. `ApplyRelationshipRequest` and `ApplyGroupRequest` return null to refuse a
        // write -- a follower cannot be in a group and a direct relationship at once (P1-76).
        // Both write branches here took the result and dereferenced it immediately
        // (`rel.IsEnabled && rel.ArmedForLive`, `grp.GroupName`), so a REFUSAL reached the
        // operator as a NullReferenceException. And SaveToDisk had already run by then.
        //
        // The NT8 window got this right at all five of its call sites. The bridge got it
        // wrong at both of its two, which is what happens when the same rule has to be
        // remembered per call site instead of enforced -- the same shape as P1-69, where the
        // fix went into one of two read branches.
        //
        // Two assertions, and the pair is the point: a null check alone could be satisfied by
        // swallowing the refusal, and returning a reason alone could be written above a
        // deref that still runs. The engine now hands back the REASON (nt8-riskguard v1.10.0),
        // so there is something worth returning.
        private static void TestUi7_NoCopierWriteBranchDereferencesARefusal()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] UI7: no copier write branch dereferences a refused apply");

            var path = BridgeSourcePath();
            Assert(File.Exists(path), string.Format("The bridge source is readable at {0}", path));
            if (!File.Exists(path))
                return;

            var code = StripComments(File.ReadAllText(path));

            Assert(Regex.Matches(code, @"Apply(?:Relationship|Group)Request\s*\([^;]*out\s+\w+\s*\)").Count == 2,
                "both write branches take the refusal reason from the engine rather than "
                + "discarding it -- this surface has no log window to send the operator to");

            // The deref guard. Anchored on the two expressions that actually threw, so this
            // fails if either branch goes back to using the result without checking it.
            bool guarded = Regex.Matches(code, @"==\s*null\s*\)\s*\n\s*(?:\{|return)").Count >= 2
                && Regex.IsMatch(code, @"refused\s*=\s*true");

            Assert(guarded,
                "and each one checks for null BEFORE using the result, returning a stated "
                + "refusal. A refusal that arrives as a NullReferenceException is "
                + "indistinguishable from the bridge being broken");

            // A refusal changed nothing, so it must not write the file. Harmless today --
            // the engine refuses before mutating -- but it makes a rejected request look
            // like an accepted one to anything watching the config file's mtime.
            int saveInWriteBranches = Regex.Matches(
                code, @"refused\s*=\s*true[^;]*;[^}]*SaveToDisk").Count;
            Assert(saveInWriteBranches == 0,
                "and a refused write does not touch the config file on its way out");
        }

        public static int Run()
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("nt8-mcp-bridge test harness");
            Console.WriteLine("====================================================");

            TestP2_38_NoBridgeGateClassifiesByAccountName();
            TestVendoredCoreIsPresentAndPinned();
            TestBridgeCarriesNoCopyOfACoreSource();
            TestP1_80_NoWritePathPersistsRiskConfigNothingReads();
            TestUi7_NoCopierWriteBranchDereferencesARefusal();

            // Harness self-check, mirroring the core suite's. A runner that silently skips
            // tests is worse than no runner, so the count is asserted rather than assumed.
            Console.WriteLine("\n[TEST] HARNESS: every declared test ran");
            const int declared = 5;
            Assert(_testsRun == declared,
                string.Format("all {0} declared tests were invoked (ran {1})", declared, _testsRun));

            Console.WriteLine();
            Console.WriteLine("====================================================");
            Console.WriteLine(string.Format("RESULTS: Passed = {0}, Failed = {1}", _passed, _failed));
            Console.WriteLine("====================================================");
            return _failed == 0 ? 0 : 1;
        }

        public static int Main(string[] args)
        {
            return Run();
        }
    }
}
