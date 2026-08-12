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

        public static int Run()
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("nt8-mcp-bridge test harness");
            Console.WriteLine("====================================================");

            TestP2_38_NoBridgeGateClassifiesByAccountName();
            TestVendoredCoreIsPresentAndPinned();
            TestBridgeCarriesNoCopyOfACoreSource();

            // Harness self-check, mirroring the core suite's. A runner that silently skips
            // tests is worse than no runner, so the count is asserted rather than assumed.
            Console.WriteLine("\n[TEST] HARNESS: every declared test ran");
            const int declared = 3;
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
