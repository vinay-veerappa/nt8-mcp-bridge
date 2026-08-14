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
using System.Collections.Generic;
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
        private static void TestP1_88_AnUnrecognisedCopierActionIsNotReportedAsAWrite()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-88/P1-89: the copier handler refuses an unknown action and resolves by BOTH accounts");

            var path = BridgeSourcePath();
            Assert(File.Exists(path), string.Format("The bridge source is readable at {0}", path));
            if (!File.Exists(path))
                return;

            var code = StripComments(File.ReadAllText(path));

            // P1-88. Found on the live box: a POST with action "set_relationship" -- not one of
            // the recognised names -- fell through every branch to the READ path, which returns
            // success:true, loaded:true and persisted:File.Exists(...). The caller is told the
            // write was persisted; nothing changed. That is P1-80's shape on the copier, and it
            // is worse here because the caller supplied a payload it believes was applied.
            //
            // The handler must name its unknown-action refusal explicitly.
            Assert(Regex.IsMatch(code, @"UNKNOWN_COPIER_ACTION"),
                "the copier handler has an explicit refusal for an action it does not recognise");

            // P1-89. The read branch resolved the relationship with FirstOrDefault on the LEADER
            // alone, so a leader with two followers returned an arbitrary one -- a request naming
            // SimCopy2 came back carrying Sim-ORB's object. Both accounts identify a relationship
            // everywhere else in this system; they have to identify it here too.
            var leaderOnly = Regex.Matches(
                code, @"FirstOrDefault\(\s*r\s*=>\s*r\.LeaderAccountName\.Equals\([^)]*\)\s*\)");
            Assert(leaderOnly.Count == 0,
                string.Format("no copier lookup resolves a relationship by leader alone ({0} found)",
                    leaderOnly.Count));

            // P1-85 closed exactly this in core. The bridge kept its own copy of the guess, so a
            // copier request that named no leader was still routed to a real, connected account.
            //
            // ⚠️ SCOPED TO THE COPIER HANDLER ON PURPOSE, and the reason is a bigger defect.
            // Seven more "Sim101" fallbacks remain in this file, and three of them are on ORDER
            // PLACEMENT paths, where the chain is worse than a single guess: an account name
            // that does not resolve falls back to Sim101, then to ANY non-Backtest account, then
            // to ANY account at all. On a box reporting 96 accounts that routes an order to one
            // nobody chose. That is P1-90, filed separately -- it needs its own change, not a
            // widened regex at the end of this one, and widening the scan here would either fail
            // for months or get narrowed by whoever hits it next.
            var copierMethod = Regex.Match(
                code, @"private object CopierConfig\(string body\)(?:.|\n)*?\n        \}");
            Assert(copierMethod.Success, "the copier config handler is locatable in the source");
            Assert(copierMethod.Success && !copierMethod.Value.Contains("\"Sim101\""),
                "the copier config handler names no default account of its own (P1-85 removed the "
                + "same guess from core; see P1-90 for the order paths)");
        }

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

        // P1-90, the behavioural half -- and the FIRST test in this repo that executes a
        // line of bridge production code instead of grepping it. That is only possible
        // because the resolver was extracted to a file naming no NinjaTrader type; see
        // addons/BridgeAccountResolver.cs.
        private static void TestP1_90_ResolverRefusesRatherThanGuessing()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-90: an account that does not resolve is refused, not substituted");

            var box = new[] { "Sim101", "Sim-ORB", "SimCopy2", "Backtest", "TAKEPROFITPRO524207503" };

            // The defect, stated as a test: a typo used to land on Sim101.
            var typo = BridgeAccountResolver.ResolveOrRefuse("Sim1O1", box, "place an order");
            Assert(typo.Refused, "A typo'd account name is refused");
            Assert(typo.Name == null,
                "and carries NO account name -- a caller that ignores Error must not find a "
                + "usable account sitting next to it, which is how P0-68 shipped");
            Assert(typo.Error != null && typo.Error.Contains("Sim1O1"),
                "and the refusal quotes what was asked for, so the operator can see the typo");
            Assert(typo.Error != null && !typo.Error.Contains("Sim101"),
                "and does NOT name Sim101 -- the substitution is gone from the text as well as "
                + "the behaviour");

            // Omission is the other half, and it is the one the three non-order sites had.
            foreach (var omitted in new[] { null, "", "   " })
            {
                var r = BridgeAccountResolver.ResolveOrRefuse(omitted, box, "place an order");
                Assert(r.Refused && r.Name == null, string.Format(
                    "An omitted account ({0}) is refused rather than defaulted",
                    omitted == null ? "null" : "'" + omitted + "'"));
                // P1-85's lesson, applied here: missing and blank are different inputs, and
                // "you did not say which account" is a different operator instruction from
                // "that account does not exist". Without this, narrowing the emptiness test
                // to a null check still refuses -- with the wrong reason -- and survives.
                Assert(r.Error != null && r.Error.Contains("no `account` field was supplied"),
                    "  and says the field was MISSING rather than that it was not found");
            }

            // Resolution still works, or this is a denial of service rather than a fix.
            var ok = BridgeAccountResolver.ResolveOrRefuse("Sim-ORB", box, "place an order");
            Assert(!ok.Refused && ok.Name == "Sim-ORB", "A named account still resolves");
            Assert(ok.Error == null, "and a resolution carries no error text");

            // The old code matched OrdinalIgnoreCase, so dropping that would break real
            // callers -- MCP clients spell accounts however the operator typed them.
            var cased = BridgeAccountResolver.ResolveOrRefuse("sim-orb", box, "place an order");
            Assert(!cased.Refused, "Case-insensitive matching is preserved");
            Assert(cased.Name == "Sim-ORB",
                "and it returns the CANONICAL spelling, not the caller's -- the name is passed "
                + "to Account lookups and log lines downstream");

            // Whitespace: " Sim101 " is a client typo with one possible intent, and treating
            // it as a named-but-absent account would report "not found" for an account that
            // is right there.
            var padded = BridgeAccountResolver.ResolveOrRefuse("  Sim101  ", box, "place an order");
            Assert(!padded.Refused && padded.Name == "Sim101", "A padded name is trimmed, not rejected");

            // An exact match must win over a case-insensitive one, or the resolver is itself
            // choosing between accounts.
            var ambiguous = new[] { "Fund", "FUND" };
            var exact = BridgeAccountResolver.ResolveOrRefuse("FUND", ambiguous, "place an order");
            Assert(!exact.Refused && exact.Name == "FUND", "An exact match wins over a case variant");
            var noExact = BridgeAccountResolver.ResolveOrRefuse("fund", ambiguous, "place an order");
            Assert(noExact.Refused,
                "but with no exact match, two case variants are refused as ambiguous rather "
                + "than resolved to whichever came first");

            // No accounts at all is a platform state, not a bad request, and the two must not
            // be reported as the same thing.
            var none = BridgeAccountResolver.ResolveOrRefuse("Sim101", new string[0], "place an order");
            Assert(none.Refused, "With no accounts available, a named account is refused");
            var nullList = BridgeAccountResolver.ResolveOrRefuse("Sim101", null, "place an order");
            Assert(nullList.Refused && nullList.Error != null,
                "and a null account list refuses instead of throwing");

            // Null entries in the list must not throw -- Account.All is a live platform
            // collection, not a curated array.
            var withNulls = BridgeAccountResolver.ResolveOrRefuse(
                "Sim101", new[] { null, "Sim101", "" }, "place an order");
            Assert(!withNulls.Refused && withNulls.Name == "Sim101",
                "Null and empty entries in the available list are skipped, not fatal");

            // The purpose text reaches the operator; a refusal that does not say what it
            // refused is the shape UI7 was about.
            var purposed = BridgeAccountResolver.ResolveOrRefuse("nope", box, "unlock an account");
            Assert(purposed.Error != null && purposed.Error.Contains("unlock an account"),
                "The refusal says what it refused to do");
        }

        // P1-90, the half that can only be a source assertion: the resolver existing proves
        // nothing about whether the six call sites USE it. This is the gate that would catch
        // the guess coming back, and it is source text because McpBridgeAddOn.cs is in no
        // test build (P2-27). Stated as a limitation, not sold as coverage.
        private static void TestP1_90_NoBridgePathInventsAnAccount()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-90: no bridge path substitutes an account it was not given");

            var path = BridgeSourcePath();
            if (!File.Exists(path)) { Assert(false, "The bridge source is readable"); return; }
            var code = StripComments(File.ReadAllText(path));

            // The literal itself. Comments are stripped, so the doc comments explaining the
            // defect are still allowed to quote it.
            int sim101 = Regex.Matches(code, "\"Sim101\"").Count;
            Assert(sim101 == 0, string.Format(
                "No executable line names \"Sim101\" (found {0}). Six sites used it as a "
                + "fallback; three of them then placed orders on it.", sim101));

            // The two other links in the chain, which are worse than Sim101 because they do
            // not even name the account they pick.
            Assert(!Regex.IsMatch(code, @"FirstOrDefault\s*\(\s*a\s*=>\s*!\s*a\.Name\.Equals\(\s*""Backtest"""),
                "and nothing selects `ANY account not called Backtest`");
            Assert(!Regex.IsMatch(code, @"\?\?\s*Account\.All\.FirstOrDefault\s*\(\s*\)"),
                "and nothing falls back to `ANY account at all`");

            // Positive evidence: the sites route through the tested resolver. Without this,
            // deleting the fallback and leaving `account == null` would also pass the above.
            int routed = Regex.Matches(code, @"ResolveOrRefuse\(").Count;
            Assert(routed >= 6, string.Format(
                "and all six account-resolution sites route through the tested resolver "
                + "(found {0})", routed));

            // The lockout path is the sharpest of the three non-order sites: it took the
            // guessed name straight into UnlockAccount, which REMOVES protection, with no
            // existence check at all.
            var unlock = Regex.Match(code, @"private object HandleLockout\(.*?\n        \}",
                RegexOptions.Singleline);
            Assert(unlock.Success, "HandleLockout is still locatable");
            if (unlock.Success)
            {
                int refusalBeforeUnlock = unlock.Value.IndexOf("ResolveOrRefuse");
                int unlockCall = unlock.Value.IndexOf("UnlockAccount");
                Assert(refusalBeforeUnlock >= 0 && (unlockCall < 0 || refusalBeforeUnlock < unlockCall),
                    "and it resolves the account BEFORE unlocking one -- unlocking removes "
                    + "protection, so a guess there is a guess about whose risk limits to drop");
            }
        }

        public static int Run()
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("nt8-mcp-bridge test harness");
            Console.WriteLine("====================================================");

            TestP2_38_NoBridgeGateClassifiesByAccountName();
            TestP1_90_ResolverRefusesRatherThanGuessing();
            TestP1_90_NoBridgePathInventsAnAccount();
            TestVendoredCoreIsPresentAndPinned();
            TestBridgeCarriesNoCopyOfACoreSource();
            TestP1_80_NoWritePathPersistsRiskConfigNothingReads();
            TestUi7_NoCopierWriteBranchDereferencesARefusal();
            TestP1_88_AnUnrecognisedCopierActionIsNotReportedAsAWrite();
            TestP334_EnforcingIsDerivedFromTheCopierGate();
            TestP334_TheNotEnforcingReasonNamesTheGlobalSwitch();
            TestP334_TheEndpointExposesAndCanSetTheMode();
            TestP1_97_TheOrderActionIsResolvedFromThePosition();
            TestP1_97_TheEndpointResolvesRatherThanHardcoding();
            TestP0_104_TheFlattensOwnOrderIsNotCancelled();
            TestP0_104_ARealResidualIsStillCancelled();
            TestP0_104_ABracketLegThatBecomesActiveIsAResidualNotANewOrder();
            TestP0_104_TheCallReportsWhatItPutOnTheBook();
            TestP0_104_TheEndpointDoesNotClaimAnUnobservedFlatten();
            TestP1_106_AnOrderThatCLOSESThePositionIsAdmittedUnderLockout();
            TestP1_106_AnEntryOnAFlatAccountIsStillRefused();
            TestP1_106_AnOrderThatADDSToThePositionIsRefused();
            TestP1_106_AReversalIsRefusedBecauseItOpensTheOtherSide();
            TestP1_106_TheShortSideWorksToo();
            TestP1_106_AnUnlockedAccountIsNotFlaggedAsReducing();
            TestP1_106_ABracketedOrderStaysRefused();
            TestP1_106_TheThreeOrderPathsAllConsultTheGate();

            // Harness self-check, mirroring the core suite's. A runner that silently skips
            // tests is worse than no runner, so the count is asserted rather than assumed.
            Console.WriteLine("\n[TEST] HARNESS: every declared test ran");
            const int declared = 26;
            Assert(_testsRun == declared,
                string.Format("all {0} declared tests were invoked (ran {1})", declared, _testsRun));

            Console.WriteLine();
            Console.WriteLine("====================================================");
            Console.WriteLine(string.Format("RESULTS: Passed = {0}, Failed = {1}", _passed, _failed));
            Console.WriteLine("====================================================");
            return _failed == 0 ? 0 : 1;
        }

        // ================================================================================
        // P3-34 read surface. EXECUTED, not grepped -- CopierEnforcementView names no NT8
        // type, so it compiles into this project. See addons/CopierEnforcementView.cs for
        // why it is its own file.
        //
        // The defect being defended: `enforcing` was `IsEnabled && ArmedForLive`, which was
        // correct until core v1.15.0 gave the copier a global mode, and wrong the moment it
        // did -- an enabled, armed relationship enforces NOTHING while the copier is in
        // shadow. F-9's finding in a second place: what a thing reports drifting from what
        // it does.
        // ================================================================================

        private static void TestP334_EnforcingIsDerivedFromTheCopierGate()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-34: `enforcing` accounts for the copier's global mode");

            // The case the old derivation got wrong, and the whole reason this exists.
            Assert(!CopierEnforcementView.IsEnforcing(true, true, false),
                "an ENABLED and ARMED relationship is NOT enforcing while the copier is not "
                + "acting -- the old `IsEnabled && ArmedForLive` said it was, and the page "
                + "would have shown a copier in shadow as enforcing");

            Assert(CopierEnforcementView.IsEnforcing(true, true, true),
                "and it IS enforcing when all three hold -- a gate that never opens is as "
                + "wrong as one that never closes");

            // The two pre-existing terms must still each be sufficient to stop it, or this
            // change would have widened what counts as enforcing while appearing to narrow it.
            Assert(!CopierEnforcementView.IsEnforcing(false, true, true),
                "a disabled relationship is not enforcing even with the copier live");
            Assert(!CopierEnforcementView.IsEnforcing(true, false, true),
                "an unarmed relationship is not enforcing even with the copier live");
            Assert(!CopierEnforcementView.IsEnforcing(false, false, false),
                "and nothing enforcing is not enforcing");
        }

        private static void TestP334_TheNotEnforcingReasonNamesTheGlobalSwitch()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-34: the reason names the global switch, not just 'false'");

            Assert(CopierEnforcementView.NotEnforcingReason(true, true, true, "live") == null,
                "an enforcing relationship has NO reason to render -- a reason beside a working "
                + "relationship is noise, and noise is how a real one gets skipped");

            string shadow = CopierEnforcementView.NotEnforcingReason(true, true, false, "shadow");
            Assert(shadow != null && shadow.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0,
                "an enabled+armed relationship under a shadow copier is explained by naming the "
                + "MODE. Got: " + (shadow ?? "<null>"));
            Assert(shadow != null && shadow.IndexOf("global", StringComparison.OrdinalIgnoreCase) >= 0,
                "and says it is GLOBAL, because the operator is looking at a relationship that "
                + "is configured correctly and needs to be told the cause is elsewhere. Got: "
                + shadow);

            string disabledMode = CopierEnforcementView.NotEnforcingReason(true, true, false, "disabled");
            Assert(disabledMode != null && disabledMode.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0,
                "`disabled` is explained as disabled, not as shadow -- P1-87: one outcome split "
                + "across two names is unfindable, and so is two outcomes sharing one. Got: "
                + (disabledMode ?? "<null>"));
            Assert(shadow != disabledMode,
                "and the two modes do not produce the same sentence");

            // An unrecognised mode is the P1-87 case: it does not trade, and the operator must
            // be told the mode is the problem rather than hunting the relationship.
            string typo = CopierEnforcementView.NotEnforcingReason(true, true, false, "Shadow_Mode_Typo");
            Assert(typo != null && typo.Contains("Shadow_Mode_Typo"),
                "an unrecognised mode is quoted back, so the typo is visible. Got: "
                + (typo ?? "<null>"));
            Assert(typo != null && typo.IndexOf("fails closed", StringComparison.OrdinalIgnoreCase) >= 0,
                "and the operator is told it fails CLOSED, so they do not read the silence as a "
                + "copier that is working. Got: " + typo);

            // The relationship's own terms still win when they are the cause: naming the mode
            // for a disabled relationship would send the operator to the wrong switch.
            string off = CopierEnforcementView.NotEnforcingReason(false, true, false, "shadow");
            Assert(off != null && off.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0
                   && off.IndexOf("global", StringComparison.OrdinalIgnoreCase) < 0,
                "a DISABLED relationship is explained by its own state, not by the global mode -- "
                + "the nearest cause is the actionable one. Got: " + (off ?? "<null>"));
        }

        /// <summary>
        /// The endpoint half, which is in McpBridgeAddOn.cs and therefore in no test build
        /// (P2-27). This is a SOURCE gate and proves strictly less than the two above: it
        /// shows the wiring is present, not that it behaves. Labelled as such deliberately --
        /// section 5.26's rule is to keep the source gate for the call sites and say which
        /// half proves less.
        /// </summary>
        private static void TestP334_TheEndpointExposesAndCanSetTheMode()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-34: the endpoint exposes copierMode and accepts set_mode (SOURCE gate)");

            string code = File.ReadAllText(BridgeSourcePath());

            Assert(Regex.IsMatch(code, @"""set_mode"""),
                "set_mode is in the copier action whitelist -- without it, P1-88's refusal "
                + "would reject the very action added to fix this");
            Assert(Regex.IsMatch(code, @"TrySetCopierMode"),
                "and it routes to TrySetCopierMode, which runs the copier preflight and REFUSES "
                + "the move to live rather than reporting a refusal and applying it anyway");
            Assert(Regex.IsMatch(code, @"copierMode = "),
                "and the read payload carries copierMode -- the mode shipped in core v1.15.0 "
                + "readable nowhere but the config file");

            // The derivation must not have been re-inlined beside the view. Two copies of
            // "is this enforcing?" is exactly how the report drifted from the gate to begin with.
            Assert(!Regex.IsMatch(code, @"enforcing = rel\.IsEnabled && rel\.ArmedForLive\s*;"),
                "and NO branch still derives `enforcing` as `IsEnabled && ArmedForLive` -- that "
                + "is the stale two-term answer, and there were TWO sites carrying it");
            Assert(Regex.Matches(code, @"CopierEnforcementView\.IsEnforcing").Count >= 2,
                "both branches derive it through CopierEnforcementView instead");

            // set_mode is a write. If it were readable over GET, a URL would change the
            // copier's mode -- the trap CopierReadFromQuery's whitelist exists to prevent.
            Assert(!Regex.IsMatch(code, @"action\.Equals\(""set_mode"", StringComparison\.OrdinalIgnoreCase\)\s*\|\|\s*isRead"),
                "and set_mode is not in the GET read whitelist, so it cannot be issued as a URL");
        }


        // ================================================================================
        // P1-97. EXECUTED. `nt_place_order` mapped buy/sell to Buy/Sell unconditionally, so
        // the bridge could never emit SellShort or BuyToCover. NT8 nets the position either
        // way -- the order works -- but the copier classifies exits from the LABEL:
        //
        //     bool leaderIsExiting = leadAction == OrderAction.Sell || leadAction == BuyToCover;
        //
        // Measured live 2026-08-13: a short ENTRY arrived as `Sell` and was read as an exit,
        // and a COVER arrived as `Buy` and was read as an ENTRY -- i.e. copied to followers as
        // a new position in the OPPOSITE direction to the leader's close.
        // ================================================================================

        private static void TestP1_97_TheOrderActionIsResolvedFromThePosition()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-97: buy/sell resolves to the right one of NT8's FOUR order actions");

            // The two the bridge could never produce, and the two defects they fix.
            Assert(BridgeOrderAction.Resolve("sell", null) == BridgeOrderAction.SellShort,
                "selling from FLAT is a short ENTRY -> SellShort. As `Sell` the copier read it as "
                + "an exit and never copied the short at all");
            Assert(BridgeOrderAction.Resolve("buy", "Short") == BridgeOrderAction.BuyToCover,
                "buying while SHORT is a COVER -> BuyToCover. As `Buy` the copier read it as an "
                + "entry and copied a position in the opposite direction to the leader's close");

            // The two that already worked, so the fix cannot be an inversion.
            Assert(BridgeOrderAction.Resolve("buy", null) == BridgeOrderAction.Buy,
                "buying from FLAT is a long entry -> Buy");
            Assert(BridgeOrderAction.Resolve("sell", "Long") == BridgeOrderAction.Sell,
                "selling while LONG is an exit -> Sell");

            // Adding to a position keeps the opening label.
            Assert(BridgeOrderAction.Resolve("buy", "Long") == BridgeOrderAction.Buy,
                "buying while LONG adds to it -> Buy, not BuyToCover");
            Assert(BridgeOrderAction.Resolve("sell", "Short") == BridgeOrderAction.SellShort,
                "selling while SHORT adds to it -> SellShort, not Sell");

            // Flat arrives in more than one spelling, and every one of them must open.
            foreach (var flat in new string[] { null, "", "Flat", "flat", "unknown" })
                Assert(BridgeOrderAction.Resolve("sell", flat) == BridgeOrderAction.SellShort,
                    "a side of '" + (flat ?? "<null>") + "' is not Long, so selling OPENS a short");

            // Case-insensitive on both arguments: the action comes off an HTTP body and the
            // side off MarketPosition.ToString().
            Assert(BridgeOrderAction.Resolve("BUY", "short") == BridgeOrderAction.BuyToCover,
                "the action and the side are both matched case-insensitively");
            Assert(BridgeOrderAction.Resolve("SELL", "LONG") == BridgeOrderAction.Sell,
                "and the other way round");

            // Every result must be a real NT8 OrderAction name -- this string is Enum.Parse'd
            // by the caller, so a typo here is a runtime throw on the order path.
            foreach (var a in new string[] { "buy", "sell" })
                foreach (var side in new string[] { null, "Long", "Short", "Flat" })
                {
                    string r = BridgeOrderAction.Resolve(a, side);
                    Assert(r == "Buy" || r == "Sell" || r == "BuyToCover" || r == "SellShort",
                        "Resolve(" + a + ", " + (side ?? "null") + ") returns a real OrderAction name, "
                        + "because the caller Enum.Parses it. Got: " + r);
                }
        }

        /// <summary>
        /// SOURCE gate -- proves the wiring exists, not that it behaves. The behaviour is the
        /// test above; this one exists because the call site is in McpBridgeAddOn.cs, which no
        /// test build reaches (P2-27).
        /// </summary>
        private static void TestP1_97_TheEndpointResolvesRatherThanHardcoding()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-97: nt_place_order calls the resolver (SOURCE gate)");

            string code = File.ReadAllText(BridgeSourcePath());

            Assert(Regex.IsMatch(code, @"BridgeOrderAction\.Resolve"),
                "the order path calls the resolver");
            Assert(!Regex.IsMatch(code, @"orderAction = actionStr\.Equals\(""buy"".*\? OrderAction\.Buy : OrderAction\.Sell"),
                "and the unconditional buy/sell mapping is GONE -- that exact expression is the "
                + "defect, and it is the thing a later refactor would most naturally restore");
        }


        // ================================================================================
        // P0-104. The panic kill-switch cancelled its own flatten order.
        //
        // EmergencyFlatten: cancel working orders -> acc.Flatten (ASYNCHRONOUS: it SUBMITS a
        // `Close` order) -> "second cancel pass for residual bracket/OCO orders" -> lockout.
        // The second pass enumerated every active order and cancelled all of it, including the
        // Close order the flatten had just put on the book.
        //
        // Measured on Sim101 2026-08-14, long 11 MNQ with one resting limit:
        //   firstPassCancelled: 1  (the limit -- correct)
        //   residualCancelled:  1  (the FLATTEN)
        //   flattenedAccounts:  1, success: true, position: STILL LONG 11
        // and then the lockout landed, so nt_place_order refused the exit the operator would
        // have placed by hand.
        //
        // BridgeFlattenPlan is the set arithmetic, executed here. Plain objects stand in for
        // orders on purpose: identity is the whole question, and NT8's OrderId is not stable.
        // ================================================================================

        private static void TestP0_104_TheFlattensOwnOrderIsNotCancelled()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P0-104: the flatten's own order is not in the residual cancel set");

            var restingLimit = new object();
            var flattenClose = new object();          // created by acc.Flatten during the call

            var before = new List<object> { restingLimit };
            var activeAfter = new List<object> { restingLimit, flattenClose };

            var residual = BridgeFlattenPlan.ResidualCancelSet(before, activeAfter);

            Assert(!residual.Contains(flattenClose),
                "THE DEFECT: the Close order acc.Flatten submitted during this call must never be "
                + "cancelled by the cleanup pass that follows it. Cancelling it leaves the position "
                + "open, and step 5 then locks the account so nobody can exit by hand.");
            Assert(residual.Count == 1 && residual[0] == restingLimit,
                "and the order that was already there is still cancelled -- got " + residual.Count);
        }

        private static void TestP0_104_ARealResidualIsStillCancelled()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P0-104: a genuine residual bracket leg is still cancelled");

            // The negative control, and the one that matters: the second pass exists for OCO legs
            // that survive the flatten. Fixing the defect by deleting the pass would pass every
            // assertion in the test above.
            var stopLeg = new object();
            var targetLeg = new object();
            var flattenClose = new object();

            var before = new List<object> { stopLeg, targetLeg };
            var activeAfter = new List<object> { stopLeg, targetLeg, flattenClose };

            var residual = BridgeFlattenPlan.ResidualCancelSet(before, activeAfter);

            Assert(residual.Count == 2 && residual.Contains(stopLeg) && residual.Contains(targetLeg),
                "both pre-existing bracket legs are still cancelled -- got " + residual.Count);
            Assert(!residual.Contains(flattenClose), "and still not the flatten");
        }

        private static void TestP0_104_ABracketLegThatBecomesActiveIsAResidualNotANewOrder()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P0-104: a leg that was known but inactive is a residual, not a new order");

            // This is why the "before" snapshot is acc.Orders UNFILTERED. A leg sitting in a
            // non-active state before the flatten and reaching Working after it is exactly what
            // the residual pass is for -- filtering "before" by state would classify it as new
            // and let it survive, which is this defect in the opposite direction.
            var inactiveLegBefore = new object();
            var flattenClose = new object();

            var beforeUnfiltered = new List<object> { inactiveLegBefore };
            var activeAfter = new List<object> { inactiveLegBefore, flattenClose };

            var residual = BridgeFlattenPlan.ResidualCancelSet(beforeUnfiltered, activeAfter);

            Assert(residual.Contains(inactiveLegBefore),
                "a pre-existing leg that only NOW became active is a residual and must be cancelled");
            Assert(!residual.Contains(flattenClose), "the flatten is still ours");
        }

        private static void TestP0_104_TheCallReportsWhatItPutOnTheBook()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P0-104: the call can name the orders it submitted");

            var restingLimit = new object();
            var flattenClose = new object();

            var mine = BridgeFlattenPlan.SubmittedByThisCall(
                new List<object> { restingLimit },
                new List<object> { restingLimit, flattenClose });

            Assert(mine.Count == 1 && mine[0] == flattenClose,
                "the flatten order is identified as this call's own -- the report used to count "
                + "the CALL to acc.Flatten and never looked at the book at all");

            // Nothing submitted: the account was already flat, so acc.Flatten put nothing on.
            var none = BridgeFlattenPlan.SubmittedByThisCall(
                new List<object> { restingLimit },
                new List<object> { restingLimit });
            Assert(none.Count == 0, "and an account that needed no flatten reports none");

            // Null tolerance: an account with no orders at all must not throw on the panic path.
            Assert(BridgeFlattenPlan.ResidualCancelSet<object>(null, null).Count == 0,
                "empty in, empty out -- this runs on every account in a panic, including flat ones");
            Assert(BridgeFlattenPlan.SubmittedByThisCall<object>(null, null).Count == 0,
                "same for the submitted set");
        }

        /// <summary>
        /// SOURCE gate -- proves the wiring, not the behaviour. The behaviour is the four tests
        /// above; the call site is in McpBridgeAddOn.cs, which no test build reaches (P2-27).
        /// </summary>
        private static void TestP0_104_TheEndpointDoesNotClaimAnUnobservedFlatten()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P0-104: the endpoint uses the plan and reports an observed position (SOURCE gate)");

            string code = File.ReadAllText(BridgeSourcePath());

            Assert(Regex.IsMatch(code, @"BridgeFlattenPlan\.ResidualCancelSet"),
                "the residual pass goes through the plan");
            // ⚠️ This assertion exists because a mutant SURVIVED the first run of
            // mutation/mutate_p0104.py. Extracting the arithmetic made the LOGIC executable; how
            // the caller BUILDS its arguments stayed in this file, and filtering the "before"
            // snapshot by order state reintroduces the defect in the opposite direction -- a
            // bracket leg that was inactive before the flatten and active after it reads as a new
            // order and survives the cleanup the pass exists for. Extraction moves the untested
            // boundary; it does not remove it.
            Assert(Regex.IsMatch(code, @"knownBeforeFlatten = acc\.Orders\.ToList\(\)"),
                "and the 'before' snapshot is EVERY order on the account, not just the active "
                + "ones -- see BridgeFlattenPlan's header for why that asymmetry is deliberate");
            Assert(!Regex.IsMatch(code, @"var residualOrders = acc\.Orders\.Where\(o => activeStates\.Contains"),
                "and the unfiltered enumeration is GONE -- that exact expression is the defect, "
                + "and it is what a later refactor would most naturally restore");
            Assert(!Regex.IsMatch(code, @"flattenedAccounts"),
                "the field that claimed an outcome nobody observed is gone. It counted the CALL to "
                + "acc.Flatten, which is asynchronous, so it was true before anything could close");
            Assert(Regex.IsMatch(code, @"accountsStillOpen"),
                "and the response carries a position read taken AFTER the pass");
            Assert(Regex.IsMatch(code, @"bool success = errors\.Count == 0 && accountsStillOpen\.Count == 0"),
                "a panic flatten that left a position open is not a success, whatever it cancelled");
        }

        // ================================================================================
        // P1-106. A lockout must stop you OPENING risk, never stop you CLOSING it.
        //
        // The three bridge order paths refused every order on a locked account, so an operator
        // holding a position could not place the exit -- the lockout trapped them in the risk it
        // exists to limit. Measured during P0-104's reproduction: Sim101 long 11, locked by the
        // panic switch, Sell refused.
        //
        // A test asserting only that a locked account refuses an ENTRY passes under the defect
        // AND under a gate deleted entirely. The discriminating case is the EXIT, and the
        // negative controls below are what stop "always allow" from passing.
        // ================================================================================

        private static void TestP1_106_AnOrderThatCLOSESThePositionIsAdmittedUnderLockout()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: a locked account may still close its position");

            // Sim101 long 11, locked, selling 11. This is the live reproduction verbatim.
            var d = BridgeLockoutGate.Evaluate(true, false, "Long", 11, 11, false, "Sim101");

            Assert(d.Allowed,
                "THE DEFECT: a lockout refused the order that would CLOSE the position it was "
                + "locking the operator out of. Refused reason was: " + d.Reason);
            Assert(d.AllowedAsReducing,
                "and it is flagged as reducing, so the admission is logged rather than silent -- "
                + "an exit slipping through a lockout unremarked is indistinguishable from the "
                + "gate being off");

            // A PARTIAL exit is the same question with a smaller answer.
            var partial = BridgeLockoutGate.Evaluate(true, false, "Long", 11, 4, false, "Sim101");
            Assert(partial.Allowed && partial.AllowedAsReducing,
                "and a partial exit (4 of a long 11) is admitted too");
        }

        private static void TestP1_106_AnEntryOnAFlatAccountIsStillRefused()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: NEGATIVE CONTROL -- an entry on a locked flat account is refused");

            // Without this, `return Allowed = true` passes the test above and every other
            // positive case. For a gate, the negative test is the one that proves it works.
            var flat = BridgeLockoutGate.Evaluate(true, false, null, 0, 5, false, "Sim101");
            Assert(!flat.Allowed,
                "a locked account that is FLAT has nothing to close, so this order can only open risk");

            var flatText = BridgeLockoutGate.Evaluate(true, true, "Flat", 0, 5, false, "Sim101");
            Assert(!flatText.Allowed,
                "and MarketPosition.Flat arrives as the string Flat, not as null -- both mean flat");
        }

        private static void TestP1_106_AnOrderThatADDSToThePositionIsRefused()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: NEGATIVE CONTROL -- scaling INTO a position under lockout is refused");

            var d = BridgeLockoutGate.Evaluate(true, true, "Long", 11, 5, false, "Sim101");
            Assert(!d.Allowed,
                "buying MORE of a long position on a locked account opens risk, which is the one "
                + "thing a lockout exists to stop");
            Assert(d.Reason.Contains("ADDS"),
                "and the refusal says why, rather than repeating the generic lockout text");
        }

        private static void TestP1_106_AReversalIsRefusedBecauseItOpensTheOtherSide()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: the quantity clamp -- a Sell 20 against a long 11 is refused");

            // THE LOAD-BEARING CASE. NT8 nets a Sell 20 against a long 11 into ONE order and the
            // operator sees an "exit". It is an exit AND a new short 9. Admitting it under a
            // lockout opens 9 contracts of fresh risk. Same arithmetic as P0-6's exit clamp and
            // P1-99's delta clamp: the clamp goes on what is NEW, not on the total.
            var d = BridgeLockoutGate.Evaluate(true, false, "Long", 11, 20, false, "Sim101");

            Assert(!d.Allowed,
                "a Sell 20 against a long 11 is an exit AND a new short 9, so it must not be "
                + "admitted under a lockout");
            Assert(d.Reason.Contains("9"),
                "and the refusal names the 9 that would be opened, plus the quantity that would "
                + "work -- a refusal the operator cannot act on is one they will retry blind");

            // The boundary itself is admitted: exactly flat, nothing opened.
            var exact = BridgeLockoutGate.Evaluate(true, false, "Long", 11, 11, false, "Sim101");
            Assert(exact.Allowed, "and quantity == |position| is a clean exit, not a reversal");
        }

        private static void TestP1_106_TheShortSideWorksToo()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: covering a SHORT is an exit (P0-96's family)");

            // P0-96 was exactly this blind spot one component over: 1311 green tests, every one
            // of them long-side, and the copier answered a leader's short-cover with a Sell that
            // DOUBLED the follower's short. NT8's Position.Quantity is ABSOLUTE -- the side is
            // MarketPosition -- so a short 11 arrives as ("Short", 11) and NEVER as -11.
            var cover = BridgeLockoutGate.Evaluate(true, true, "Short", 11, 11, false, "Sim101");
            Assert(cover.Allowed && cover.AllowedAsReducing,
                "buying to cover a short 11 on a locked account is an EXIT and must be admitted");

            var addShort = BridgeLockoutGate.Evaluate(true, false, "Short", 11, 5, false, "Sim101");
            Assert(!addShort.Allowed,
                "and selling MORE against a short is scaling in, which stays refused");

            var overCover = BridgeLockoutGate.Evaluate(true, true, "Short", 11, 20, false, "Sim101");
            Assert(!overCover.Allowed,
                "and over-covering a short 11 with 20 opens a long 9, refused like the long side");

            // DEFENCE IN DEPTH, and the mutation battery is why it is here. NT8 does not emit a
            // negative Quantity -- but the copier's P0-96 read the SIGN of that field anyway, and
            // 1311 green tests passed over it. If a caller ever hands this gate a signed -11, the
            // magnitude is what must be compared: without Math.Abs, `11 > -11` refuses the cover
            // and P1-106 is BACK, on the short side only, which is exactly how P0-96 hid.
            var signed = BridgeLockoutGate.Evaluate(true, true, "Short", -11, 11, false, "Sim101");
            Assert(signed.Allowed && signed.AllowedAsReducing,
                "a signed position quantity must not turn a legitimate cover into a refusal -- the "
                + "side comes from MarketPosition, so the quantity is compared as a magnitude");
        }

        private static void TestP1_106_AnUnlockedAccountIsNotFlaggedAsReducing()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: an UNLOCKED account is allowed without the reducing flag");

            // If AllowedAsReducing were set whenever the order reduces, the "admitted under
            // lockout" warning would fire on every ordinary exit on every account -- and an
            // alarm that is always on is off. This repo has six instances of that already.
            var d = BridgeLockoutGate.Evaluate(false, false, "Long", 11, 11, false, "Sim101");
            Assert(d.Allowed, "an unlocked account places orders normally");
            Assert(!d.AllowedAsReducing,
                "and it is NOT flagged as a lockout-time reduction, or the log line fires on every "
                + "ordinary exit and stops meaning anything");
            Assert(d.Reason.Length == 0,
                "and it carries no operator-facing reason, because nothing happened worth saying");

            // Not locked, and an order that would be refused if it were: still allowed.
            var entry = BridgeLockoutGate.Evaluate(false, true, "Long", 11, 50, false, "Sim101");
            Assert(entry.Allowed,
                "and an unlocked account is NOT subject to the reducing-only rule at all -- this "
                + "gate must not become a position limit by accident");
        }

        private static void TestP1_106_ABracketedOrderStaysRefused()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: an OCO/ATM bracket is refused even when its entry would reduce");

            // THE DELIBERATE ASYMMETRY, and it is not an omission. An OCO submits an entry plus
            // stop and target legs, and those legs take the OPPOSITE side -- so an OCO whose
            // entry flattens a long leaves a resting stop and target that OPEN a short the moment
            // either triggers. The bracket cannot be admitted on the strength of its entry.
            var d = BridgeLockoutGate.Evaluate(true, false, "Long", 11, 11, true, "Sim101");

            Assert(!d.Allowed,
                "a bracketed order is refused under lockout even though the same entry as a plain "
                + "order would be admitted");
            Assert(d.Reason.Contains("nt_close_position") || d.Reason.Contains("plain order"),
                "and the refusal names a path that DOES work, or the operator is back where "
                + "P1-106 found them");

            // And an unlocked account still places brackets normally.
            var unlocked = BridgeLockoutGate.Evaluate(false, true, null, 0, 5, true, "Sim101");
            Assert(unlocked.Allowed, "brackets are unaffected when the account is not locked");
        }

        private static void TestP1_106_TheThreeOrderPathsAllConsultTheGate()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-106: PlaceOrder, PlaceOcoOrder and PlaceAtmOrder all route through the gate");

            // EXTRACTION MOVES THE UNTESTED BOUNDARY, IT DOES NOT REMOVE IT -- P0-104's surviving
            // mutant is the precedent. Every test above executes the predicate; none of them can
            // see whether McpBridgeAddOn actually CALLS it, or whether it passes the position
            // rather than a label. That half is source text, and this test says so.
            string path = BridgeSourcePath();
            Assert(File.Exists(path), string.Format("The bridge source is readable at {0}", path));
            // Comments are stripped: this file's own prose quotes the defective pattern verbatim,
            // and the checks below would match the explanation instead of the code.
            string code = StripComments(File.ReadAllText(path));

            int calls = Regex.Matches(code, @"BridgeLockoutGate\.Evaluate\(").Count;
            Assert(calls >= 3,
                "all three order paths must consult the gate -- found " + calls + ". The defect was "
                + "three copies of a bare `if (IsAccountLocked(...)) return blocked;`");

            // The bare refusal must be GONE from the order paths. It is still legitimate in the
            // read-only lockout query, so this asserts the SHAPE that returned an order error.
            Assert(!Regex.IsMatch(code, @"if \(IsAccountLocked\([^)]*\)\)\s*\r?\n\s*return new \{ error = "),
                "no order path may still refuse with a bare lockout test -- that is the defect");

            // And the direction fed in must be the REQUEST's, not the resolved OrderAction.
            // Passing resolvedAction back in would make the gate read a label the caller chose,
            // which is P1-97 reintroduced one statement after it was fixed.
            Assert(!Regex.IsMatch(code, @"BridgeLockoutGate\.Evaluate\([^;]*resolvedAction"),
                "the gate must never be fed the resolved OrderAction label; it takes the request's "
                + "buy/sell direction and the real position");
        }


        public static int Main(string[] args)
        {
            return Run();
        }
    }
}
