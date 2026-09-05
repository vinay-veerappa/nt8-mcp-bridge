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
// P2-138 parses a payload CAPTURED from the deployed box, so the field names the tree
// reads are executed rather than typed a second time.
using Newtonsoft.Json.Linq;

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
            //
            // ⚠️ EXACT, not `>= 6`, and the exactness is the point. This read `>= 6` until
            // P1-105 added a SEVENTH site (ClosePosition) -- at which point a mutant that
            // removed the resolver from the compliance site left 6 and the assertion still
            // passed. Nothing in this gate changed; the CODE AROUND IT changed, and a
            // lower-bound count is satisfied by unrelated growth. `mutate_p190.py` caught it
            // on the first run after the addition.
            //
            // So: adding an eighth resolution site must bump this number in the same commit.
            // That is a deliberate speed bump, not an oversight -- it makes the author of a new
            // site look at what this gate is protecting.
            // ⚠️ 7 -> 8 in P2-109, which added GetOrders. The bump is the gate WORKING: it
            // fired on the very next change after the >= 6 leak was closed, and made this
            // author state that the new site is a deliberate addition rather than let it
            // quietly restore the slack that let a mutant survive.
            // ⚠️ 8 -> 9 for /api/events/fills (GetFillEvents), which had been dropping the
            // wrapper's `account` filter entirely -- P2-109's shape at a third endpoint.
            int routed = Regex.Matches(code, @"ResolveOrRefuse\(").Count;
            Assert(routed == 10, string.Format(
                "and all TEN account-resolution sites route through the tested resolver "
                + "(found {0}). If you added a site, raise this number; if you removed one, "
                + "say which and why", routed));

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

        // ------------------------------------------------------------------
        // P2-115. `/api/health` reported `feedConnected = accountCount > 0`. A running NT8 always
        // reports its Simulator accounts, so the field was `true` on every call, on every box,
        // forever. It is not a weak measurement of the data feed -- it is not a measurement of the
        // data feed. Measured live on 2026-08-15 it read `true` while NT8 sat on a DORMANT Playback
        // connection with no tradeable market at all (three orders placed on Sim101 stalled at
        // OrderState.Initialized), and read `true` again an hour later with a real broker attached.
        // IT DID NOT CHANGE VALUE WHEN THE THING IT NAMES CHANGED COMPLETELY.
        //
        // These reach BridgeFeedStatus by REFLECTION so they compile before it exists -- the same
        // technique P2-112 used, and for the same reason: the harness is an exe, so a missing type
        // is a BUILD failure, and a build failure cannot express a red acceptance test.
        // ------------------------------------------------------------------

        private static Type P2115Type()
        {
            return Type.GetType("NinjaTrader.NinjaScript.AddOns.BridgeFeedStatus, " +
                                typeof(BridgeSourceTests).Assembly.GetName().Name);
        }

        /// <summary>
        /// Calls BridgeFeedStatus.IsMarketDataConnected(names, providers, statuses) -> bool.
        /// Three parallel string arrays deliberately: this class must name NO NinjaTrader type, or
        /// it joins McpBridgeAddOn.cs in the set nothing can execute (P2-27).
        /// </summary>
        private static bool? P2115Ask(string[] names, string[] providers, string[] statuses)
        {
            var t = P2115Type();
            if (t == null) return null;
            var m = t.GetMethod("IsMarketDataConnected",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m == null) return null;
            return (bool)m.Invoke(null, new object[] { names, providers, statuses });
        }

        /// <summary>THE defect: the shipped field had exactly one reachable value.</summary>
        private static void TestP2_115_TheHealthFlagCanBeFalse()
        {
            Console.WriteLine("\n[TEST] P2-115: the health flag can be FALSE at all");

            var t = P2115Type();
            Assert(t != null,
                "BridgeFeedStatus exists and is reachable, so `feedConnected` is computed from "
                + "connection state instead of `Account.All.Count > 0` -- an expression that is "
                + "true on every running NT8 and therefore measures nothing");
            if (t == null) return;

            var answer = P2115Ask(
                new[] { "Sim101", "Backtest" },
                new[] { "Simulator", "Simulator" },
                new[] { "Connected", "Connected" });

            Assert(answer == false,
                "with only Simulator connections the answer is FALSE. The shipped expression "
                + "returned true here, and this is the whole defect: a status field that cannot "
                + "report the bad state is a constant wearing the name of a measurement.");
        }

        /// <summary>The exact live observation that found it.</summary>
        private static void TestP2_115_ADormantPlaybackConnectionIsNotAMarketFeed()
        {
            Console.WriteLine("\n[TEST] P2-115: a dormant Playback connection is not a market feed");

            if (P2115Type() == null)
            {
                Assert(false, "BridgeFeedStatus exists so the Playback case can be asked at all");
                return;
            }

            var answer = P2115Ask(
                new[] { "Playback101", "Sim101", "TAKEPROFITPRO524207503" },
                new[] { "Playback", "Simulator", "Provider31" },
                new[] { "Connected", "Connected", "Disconnected" });

            Assert(answer == false,
                "Playback connected + broker disconnected reports FALSE. This is the state measured "
                + "on the box on 2026-08-15: MNQ frozen eight days at volume 0, orders stalling at "
                + "Initialized, and the health endpoint saying the feed was connected.");
        }

        /// <summary>
        /// The negative half, and the one that stops the cheap fix. Returning a constant `false`
        /// passes both tests above. A detector needs a negative test; so does a status field.
        /// </summary>
        private static void TestP2_115_ALiveBrokerConnectionStillReportsTrue()
        {
            Console.WriteLine("\n[TEST] P2-115: a live broker connection still reports TRUE");

            if (P2115Type() == null)
            {
                Assert(false, "BridgeFeedStatus exists so the connected case can be asked at all");
                return;
            }

            var answer = P2115Ask(
                new[] { "Sim101", "TAKEPROFITPRO524207503" },
                new[] { "Simulator", "Provider31" },
                new[] { "Connected", "Connected" });

            Assert(answer == true,
                "a connected non-simulated provider reports TRUE. Without this a constant `false` "
                + "would satisfy every other assertion here, which is the same trap P2-112's "
                + "dispatcher test exists to close.");
        }

        /// <summary>
        /// Fail closed. Null, blank and anything not positively identified as connected must not
        /// read as a working feed -- P2-38's rule, at a reporting surface.
        /// </summary>
        private static void TestP2_115_AnUnknownConnectionStateFailsClosed()
        {
            Console.WriteLine("\n[TEST] P2-115: an unknown or absent connection state fails CLOSED");

            if (P2115Type() == null)
            {
                Assert(false, "BridgeFeedStatus exists so the unknown-state case can be asked");
                return;
            }

            var unknown = P2115Ask(
                new[] { "TAKEPROFITPRO524207503" },
                new[] { "Provider31" },
                new[] { (string)null });
            Assert(unknown == false, "a null connection status is not a connected feed");

            var none = P2115Ask(new string[0], new string[0], new string[0]);
            Assert(none == false,
                "and no connections at all is FALSE rather than vacuously true -- `All` over an "
                + "empty sequence is true, which is how the last instance of a class disarms its "
                + "own gate");

            // ⚠️ ADDED AFTER THE MUTATION BATTERY. A mutant turning the null-argument guard from
            // `return false` into `return true` SURVIVED, because every assertion above passes a
            // real array -- AN EMPTY ARRAY IS NOT A NULL ONE, and only the empty case had ever
            // been exercised. That mutant is the shipped defect in its purest form: always true.
            var nulls = P2115Ask(null, null, null);
            Assert(nulls == false,
                "null arrays are FALSE, not true. A caller that could not build the snapshot has "
                + "said nothing about the feed, and 'I do not know' must never read as 'connected'");

            // ⚠️ ALSO FROM THE BATTERY. A blank provider is not positively identified as real, so
            // it must not count toward a live feed. Every assertion above passes a NAMED provider,
            // so the mutant that admitted blanks changed no outcome.
            var blankProvider = P2115Ask(
                new[] { "Mystery" }, new[] { "   " }, new[] { "Connected" });
            Assert(blankProvider == false,
                "a CONNECTED account whose provider is blank is still FALSE -- anything not "
                + "positively identified as a real provider fails closed, the same rule P2-38 "
                + "established for account names");

            // ⚠️ AND THE RAGGED CASE. The route builds all three arrays in one loop so it cannot
            // produce this today, but the class is public and the next caller is the one that
            // will. The mutant that removed the shortest-length clamp survived because nothing
            // in the suite was ragged.
            // ⚠️ STATUSES MUST BE STRICTLY THE SHORTEST ARRAY, and getting that wrong is why the
            // first version of this test did not kill its mutant. With names=3, providers=1,
            // statuses=2 the length is pinned to 1 by the PROVIDERS clamp, so removing the
            // STATUSES clamp changes nothing and the mutant survives. Each clamp needs the array
            // it guards to be the one that would overrun.
            bool threw = false;
            bool? ragged = null;
            try
            {
                ragged = P2115Ask(
                    new[] { "A", "B" },
                    new[] { "Provider31", "Provider31" },
                    new[] { "Disconnected" });
            }
            catch (Exception) { threw = true; }
            Assert(!threw,
                "ragged arrays do not throw -- a health endpoint that raises is worse than one "
                + "that answers conservatively, and without the statuses clamp index 1 overruns");
            Assert(ragged == false,
                "and the answer comes from the entries that DO line up: the only status present "
                + "is Disconnected, so this is FALSE");

            // The mirror, so the clamp cannot be 'fixed' by always returning false: a shorter
            // statuses array that IS connected still reports true.
            var raggedTrue = P2115Ask(
                new[] { "A", "B" },
                new[] { "Provider31", "Provider31" },
                new[] { "Connected" });
            Assert(raggedTrue == true,
                "a connected first entry still reports TRUE when later entries have no status");
        }

        /// <summary>
        /// A SOURCE gate, labelled one because it proves less. `McpBridgeAddOn.cs` is in no test
        /// build (P2-27), so the route that CALLS the class above cannot be executed by anything
        /// here. Presence gates fail loudly; this one asserts the defective expression is gone AND
        /// that the replacement is actually wired, because a value that is computed is not a value
        /// that is used -- the weakness P1-105's and P2-109's batteries both found.
        /// </summary>
        private static void TestP2_115_TheRouteNoLongerDerivesTheFlagFromTheAccountCount()
        {
            Console.WriteLine("\n[TEST] P2-115: /api/health no longer derives the flag from the account count (source gate)");

            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(BridgeSourceTests).Assembly.Location),
                "..", "..", "..", "..", "addons", "McpBridgeAddOn.cs"));
            if (!File.Exists(path))
                path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "addons", "McpBridgeAddOn.cs"));

            Assert(File.Exists(path), "McpBridgeAddOn.cs is readable at " + path);
            if (!File.Exists(path)) return;

            var raw = File.ReadAllText(path);
            var code = System.Text.RegularExpressions.Regex.Replace(raw, @"//[^\r\n]*", "");

            Assert(!code.Contains("connectedToFeed = accountCount > 0"),
                "the health route no longer computes `feedConnected` from the account count");

            // ⚠️ THIS ASSERTION WAS `code.Contains("BridgeFeedStatus")` AND A MUTANT WALKED
            // STRAIGHT THROUGH IT: keep the call, throw the answer away, hardcode the flag true.
            // The comment below it already SAID that a value which is computed is not a value
            // that is used -- I wrote the warning and then shipped the weaker check anyway. It is
            // the third time this repo has found the same gap (P1-105, P2-109, now here), so the
            // assertion is now on the ASSIGNMENT, not the mention.
            Assert(code.Contains("connectedToFeed = BridgeFeedStatus.IsMarketDataConnected("),
                "and the flag is ASSIGNED from BridgeFeedStatus, not merely computed beside it. "
                + "Asserting only that the class is named passes when the call is present and its "
                + "answer discarded -- a value that is COMPUTED is not a value that is USED.");
        }

        // ------------------------------------------------------------------
        // F-17. Connection visibility and control, added 2026-08-15 at the operator's request
        // while closing P2-115.
        //
        // P2-115 gave `/api/health` an honest `feedConnected`, but a caller who reads `false`
        // still has no way to ask WHY, and no way to act on it. Worse, the negative half of
        // P2-115 could not be validated at all, because nothing on the box could disconnect a
        // connection -- so `feedConnected: false` was a state the code could produce and no test,
        // live or otherwise, had ever observed.
        //
        // ⚠️ DISCONNECTING IS A DESTRUCTIVE ACT ON A TRADING PLATFORM. It severs the path by
        // which a position is managed, which is `P1-106`'s family exactly: a control that stops
        // you fixing the thing it just broke. So the plan REFUSES by default when any account on
        // the connection holds a position or a working order, and says which.
        //
        // These reach BridgeConnectionPlan by reflection, so they compile before it exists.
        // ------------------------------------------------------------------

        private static Type F17Type()
        {
            return Type.GetType("NinjaTrader.NinjaScript.AddOns.BridgeConnectionPlan, " +
                                typeof(BridgeSourceTests).Assembly.GetName().Name);
        }

        private static object[] F17Resolve(string requested, string[] available)
        {
            var t = F17Type();
            if (t == null) return null;
            var m = t.GetMethod("TryResolve",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m == null) return null;
            var args = new object[] { requested, available, null, null };
            var ok = (bool)m.Invoke(null, args);
            return new object[] { ok, args[2], args[3] };   // ok, resolved, refusal
        }

        private static object[] F17WouldStrand(int positions, int orders)
        {
            var t = F17Type();
            if (t == null) return null;
            var m = t.GetMethod("WouldStrand",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m == null) return null;
            var args = new object[] { positions, orders, null };
            var strands = (bool)m.Invoke(null, args);
            return new object[] { strands, args[2] };       // strands, reason
        }

        /// <summary>P1-90's rule at a new surface: resolve or refuse, never guess.</summary>
        private static void TestF17_AnUnknownConnectionIsRefusedNamingTheRealOnes()
        {
            Console.WriteLine("\n[TEST] F-17: an unknown connection name is refused, naming the ones that exist");

            var t = F17Type();
            Assert(t != null,
                "BridgeConnectionPlan exists, so connection control has a decision layer a test "
                + "can execute rather than living entirely in the untestable route");
            if (t == null) return;

            var available = new[] { "Provider31", "Playback", "Simulator" };

            var miss = F17Resolve("Provdier31", available);          // transposed, as a typo is
            Assert((bool)miss[0] == false, "a typo'd connection name does NOT resolve");
            Assert(miss[2] != null && miss[2].ToString().Contains("Provider31"),
                "and the refusal NAMES the available connections, so the caller can correct it "
                + "instead of guessing. Got: " + (miss[2] ?? "(no refusal text)"));

            var hit = F17Resolve("provider31", available);
            Assert((bool)hit[0], "an exact name resolves case-insensitively");
            Assert((string)hit[1] == "Provider31",
                "and it returns the CANONICAL spelling, because that string is what gets passed "
                + "to the platform and printed in the audit line");

            // ⚠️ THIS ASSERTION WAS THE OPPOSITE ONE AN HOUR AGO, AND IT WAS WRONG.
            //
            // Driving the live tool produced `Available: Playback, TPT, TPT.` and I read the
            // repetition as a display artefact -- the route builds its arrays PROVIDER-grained for
            // the feed predicate, so a two-provider connection contributes two entries. I
            // deduplicated it. Then a later live read showed THREE connections, with `TPT` present
            // twice as genuinely DISTINCT Connection objects: one Simulator with 5 accounts, one
            // Provider31 with 1.
            //
            // The duplicate was REAL. Deduplicating made the display tidy and the ambiguity
            // INVISIBLE, which is strictly worse than the cosmetic problem it solved -- and on a
            // path that connects and disconnects brokers, "whichever one matched first" is
            // `P1-90`'s defect exactly: acting on an arbitrary target instead of refusing.
            //
            // So an ambiguous name is REFUSED, and the refusal has to say what would disambiguate.
            var ambiguous = F17Resolve("TPT", new[] { "Playback", "TPT", "TPT" });
            Assert((bool)ambiguous[0] == false,
                "a name matching MORE THAN ONE connection does not resolve. Two connections really "
                + "can share a name -- measured on this box -- and picking the first is how a "
                + "broker switch acts on the wrong one");
            var text = ambiguous[2] == null ? "" : ambiguous[2].ToString();
            Assert(text.IndexOf("ambiguous", StringComparison.OrdinalIgnoreCase) >= 0,
                "and the refusal says it is AMBIGUOUS rather than 'not found', because those are "
                + "different problems with different fixes. Got: " + text);
        }

        /// <summary>
        /// The FOOLPROOF half of F-17. Measured live 2026-08-15: Kinetick's connection is
        /// named `Kinetick – End Of Day (Free)` with a U+2013 EN DASH, and THREE spellings of
        /// it reached the tool -- the exact en dash, an ASCII hyphen, and the cp1252 mojibake
        /// `â€“` (E2 80 93). Only the exact form resolved; two of three were REFUSED. The
        /// resolver now matches on a normalized key, so all three spellings find the ONE
        /// canonical connection.
        ///
        /// ⚠️ Normalization is NOT fuzzing. A name whose dash was DROPPED entirely still
        /// refuses, and two connections differing only in dash style are still AMBIGUOUS --
        /// normalization makes a duplicate visible rather than hiding it.
        /// </summary>
        private static void TestF17_DashAndMojibakeVariantsOfANameResolve()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] F-17: the en-dash connection resolves however the caller spells it");

            var t = F17Type();
            Assert(t != null,
                "BridgeConnectionPlan exists, so connection control has a decision layer a test "
                + "can execute rather than living entirely in the untestable route");
            if (t == null) return;

            // The canonical name, exactly as Kinetick carries it on this box.
            var available = new[] { "Kinetick \u2013 End Of Day (Free)" };

            // 1. The exact en dash -- the spelling the platform itself uses. Positive control.
            var exact = F17Resolve("Kinetick \u2013 End Of Day (Free)", available);
            Assert((bool)exact[0], "the exact en-dash spelling resolves");
            Assert((string)exact[1] == available[0],
                "and returns the CANONICAL en-dash spelling, not the caller's");

            // 2. The ASCII hyphen -- what a human actually types. This is the spelling the raw
            //    StringComparison.OrdinalIgnoreCase refused before the normalized key.
            var hyphen = F17Resolve("Kinetick - End Of Day (Free)", available);
            Assert((bool)hyphen[0], "the ASCII-hyphen spelling resolves the en-dash connection");
            Assert((string)hyphen[1] == available[0],
                "and still returns the canonical en-dash spelling -- the KEY is normalized, the ANSWER is not");

            // 3. The mojibake: UTF-8 bytes E2 80 93 read as cp1252 = `â€“` (U+00E2 U+20AC U+201C).
            //    This is what an MCP tool call actually produced live. It must repair and resolve.
            var mojibake = F17Resolve("Kinetick \u00E2\u20AC\u201C End Of Day (Free)", available);
            Assert((bool)mojibake[0], "the cp1252 mojibake `â€“` spelling repairs and resolves");
            Assert((string)mojibake[1] == available[0],
                "and returns the canonical en-dash spelling");

            // ⚠️ NEGATIVE CONTROL -- normalization is NOT fuzzing. A name whose dash was
            //    dropped entirely is mangled beyond repair and must STILL refuse. If this
            //    resolved, the normalization would be guessing, which is P1-90 verbatim.
            var dropped = F17Resolve("Kinetick End Of Day (Free)", available);
            Assert((bool)dropped[0] == false,
                "a name with the dash dropped entirely is STILL refused -- normalization collapses "
                + "dash VARIANTS, it does not invent them");

            // ⚠️ Ambiguity survives normalization. Two connections differing only in dash style
            //    are still two connections, and a name matching BOTH must stay AMBIGUOUS rather
            //    than silently resolving to the first -- the TPT lesson, dash-style edition.
            var two = F17Resolve("Kinetick - End Of Day (Free)",
                new[] { "Kinetick \u2013 End Of Day (Free)", "Kinetick - End Of Day (Free)" });
            Assert((bool)two[0] == false,
                "two connections differing only in dash style are still AMBIGUOUS -- normalization "
                + "makes the duplicate visible instead of hiding it");
            var text = two[2] == null ? "" : two[2].ToString();
            Assert(text.IndexOf("ambiguous", StringComparison.OrdinalIgnoreCase) >= 0,
                "and the refusal says AMBIGUOUS. Got: " + text);
        }

        /// <summary>
        /// ⚠️ A blank request is NOT a wildcard. On a path that DISCONNECTS, the failure
        /// directions are not symmetric -- `symbol: "M"` closing four instruments (P1-105) is the
        /// same shape, and here a blank name would mean "sever everything".
        /// </summary>
        private static void TestF17_ABlankConnectionNameIsNotAWildcard()
        {
            Console.WriteLine("\n[TEST] F-17: a blank connection name is refused, not treated as 'all'");

            if (F17Type() == null) { Assert(false, "BridgeConnectionPlan exists"); return; }
            var available = new[] { "Provider31", "Playback" };

            foreach (var blank in new[] { null, "", "   " })
            {
                var r = F17Resolve(blank, available);
                Assert((bool)r[0] == false,
                    "a blank name (" + (blank == null ? "null" : "'" + blank + "'")
                    + ") is refused rather than matching everything -- on a disconnect path that "
                    + "would sever every connection on the box");
                // ⚠️ THE GUARD'S DELIVERABLE IS THE MESSAGE, not the boolean. With the blank
                // guard removed the loop still refuses (no name matches ""), so `ok == false`
                // passes under the mutant -- but the operator is told "no connection named ''
                // exists", which reads like a typo, not like a safety boundary. Assert the
                // warning that says blank is NOT a wildcard.
                Assert(r[2] != null && r[2].ToString().IndexOf("wildcard", StringComparison.OrdinalIgnoreCase) >= 0,
                    "and the refusal says blank is deliberately NOT a wildcard -- the warning that "
                    + "makes a safety boundary read as one, not as a misspelling. Got: "
                    + (r[2] ?? "(no refusal text)"));
            }
        }

        /// <summary>The safety decision, in both directions.</summary>
        private static void TestF17_DisconnectingIsRefusedWhileAnythingIsLive()
        {
            Console.WriteLine("\n[TEST] F-17: a disconnect that would strand a position or an order is refused");

            if (F17Type() == null) { Assert(false, "BridgeConnectionPlan exists"); return; }

            var withPosition = F17WouldStrand(1, 0);
            Assert((bool)withPosition[0],
                "an open position makes a disconnect disruptive -- severing the connection is "
                + "severing the only path by which that position can be closed");
            Assert(withPosition[1] != null && withPosition[1].ToString().Contains("position"),
                "and the reason NAMES the position, so the operator can decide rather than be "
                + "told 'refused'. Got: " + (withPosition[1] ?? "(none)"));

            var withOrder = F17WouldStrand(0, 1);
            Assert((bool)withOrder[0],
                "a WORKING ORDER also makes it disruptive, not just a position -- a resting stop "
                + "is protection, and disconnecting abandons it while it still exists at the broker");
            Assert(withOrder[1] != null && withOrder[1].ToString().Contains("order"),
                "and that reason names the order rather than reusing the position wording");

            // ⚠️ THE NEGATIVE HALF. Without it, `return true` satisfies everything above and the
            // tool refuses every disconnect forever -- which is the same class of defect as
            // P2-115's constant, one layer up. A detector needs a negative test.
            var quiet = F17WouldStrand(0, 0);
            Assert((bool)quiet[0] == false,
                "a flat account with no working orders is NOT disruptive, so the ordinary "
                + "disconnect goes through. A control that always refuses is a control nobody keeps");
        }

        /// <summary>
        /// A SOURCE gate, and written this way FIRST rather than after a mutant walked through it.
        /// P1-105, P2-109 and P2-115 each shipped a gate asserting a value was COMPUTED; all three
        /// were satisfied by code that computed it and ignored the answer. This asserts the
        /// refusal RETURNS.
        /// </summary>
        private static void TestF17_TheDisconnectRouteActsOnTheRefusalRatherThanComputingIt()
        {
            Console.WriteLine("\n[TEST] F-17: the disconnect route RETURNS on the refusal (source gate)");

            var path = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(BridgeSourceTests).Assembly.Location),
                "..", "..", "..", "..", "addons", "McpBridgeAddOn.cs"));
            if (!File.Exists(path))
                path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "addons", "McpBridgeAddOn.cs"));
            Assert(File.Exists(path), "McpBridgeAddOn.cs is readable at " + path);
            if (!File.Exists(path)) return;

            var code = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), @"//[^\r\n]*", "");

            Assert(code.Contains("BridgeConnectionPlan.TryResolve"),
                "the route resolves the connection name through the plan");

            // The refusal must be RETURNED, not merely produced. `return new { ... refused` inside
            // the resolve failure is the shape being required.
            var refusalReturns = new System.Text.RegularExpressions.Regex(
                @"BridgeConnectionPlan\.TryResolve[\s\S]{0,400}?return\s+new\s*\{[^}]*refused",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert(refusalReturns.IsMatch(code),
                "and it RETURNS the refusal rather than computing it and carrying on. Three "
                + "tickets here (P1-105, P2-109, P2-115) shipped a gate that only asserted the "
                + "value was computed, and a mutant walked through every one of them.");

            // ⚠️ THIS ASSERTION USED TO REQUIRE A `return ... refused` NEAR THE CALL, AND A MUTANT
            // WALKED THROUGH IT: neutering the guard to `if (false)` leaves the return statement
            // sitting in the source, unreachable, and a regex over source text cannot tell. That
            // is "a value that is COMPUTED is not a value that is USED" for the FOURTH time here
            // (P1-105, P2-109, P2-115, now this) -- and this time in the gate written expressly to
            // avoid it. The lesson has moved on: on a guarded return, THE CONDITION IS THE
            // LOAD-BEARING PART, so that is what gets asserted.
            var strandGuard = new System.Text.RegularExpressions.Regex(
                @"if\s*\([^)]*\bstrands\b[^)]*confirmDisruptive[^)]*\)");
            Assert(strandGuard.IsMatch(code),
                "and the disconnect is GUARDED by both the strand result and the explicit "
                + "override, in one condition. Asserting only that a refusal is returned nearby "
                + "passes when the condition is neutered to `if (false)` and the return goes "
                + "unreachable -- which is exactly what a mutant did.");

            var strandReturns = new System.Text.RegularExpressions.Regex(
                @"BridgeConnectionPlan\.WouldStrand[\s\S]{0,400}?return\s+new\s*\{[^}]*refused",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert(strandReturns.IsMatch(code),
                "and the strand check still RETURNS its refusal -- kept alongside the condition "
                + "check above, because either one alone is satisfiable without the other");

            // Positive control, and it must FAIL on the mutant's shape as well as pass on the
            // real one -- otherwise it is just a second way of writing the same weak check.
            Assert(strandGuard.IsMatch("if (action == \"disconnect\" && strands && !req.Bool(\"confirmDisruptive\"))"),
                "positive control: the guard pattern matches the real condition");
            Assert(!strandGuard.IsMatch("if (false) { return new { refused = true }; }"),
                "negative control: and it does NOT match the neutered condition the mutant used");

            // Positive control on the regexes themselves: they must be able to match something.
            Assert(refusalReturns.IsMatch(
                    "BridgeConnectionPlan.TryResolve(x); return new { success = false, refused = true };"),
                "positive control: the refusal pattern still matches the shape it is about");
        }

        // ================================================================================
        // F-17 extension (2026-08-15): the configured-connection CATALOG. The box has EIGHT
        // brokers in Config.xml <ConnectOptions> and the account-derived snapshot showed only
        // the two carrying accounts, so the others were invisible and unconnectable.
        // BridgeConnectionCatalog.cs is visibility-only -- and must STAY that way, because it
        // is reachable over HTTP:7890. These tests execute it for real (the P2-27 glob put it
        // in this build the moment it existed) and one source assertion guards the marshalling.
        // ================================================================================

        // A faithful slice of the box's Config.xml <ConnectOptions>: the real connection names
        // and option types, with FAKE credential blobs. The credential fields exist in the
        // fixture precisely so the parse is proven not to read them.
        private const string SampleConfigConnectOptions = @"<?xml version=""1.0"" encoding=""utf-8""?>
<NinjaTrader>
  <ConnectOptions>
    <TradovateOptions>
      <Name>Apex</Name>
      <TypeName>NinjaTrader.Cbi.TradovateOptions</TypeName>
      <Provider>Provider31</Provider>
      <Mode>Live</Mode>
      <AccountType>Simulation</AccountType>
      <User>fake-user-blob</User>
      <Password>fake-password-blob</Password>
      <AccessToken>fake-access-token-blob</AccessToken>
      <MdAccessToken>fake-md-access-token-blob</MdAccessToken>
    </TradovateOptions>
    <TradovateOptions>
      <Name>LUCID</Name>
      <TypeName>NinjaTrader.Cbi.TradovateOptions</TypeName>
      <Provider>Provider31</Provider>
      <Mode>Live</Mode>
      <AccountType>Simulation</AccountType>
      <User>fake-user-blob</User>
      <AccessToken>fake-access-token-blob</AccessToken>
    </TradovateOptions>
    <TradovateOptions>
      <Name>TPT</Name>
      <TypeName>NinjaTrader.Cbi.TradovateOptions</TypeName>
      <Provider>Provider31</Provider>
      <Mode>Live</Mode>
      <AccountType>Simulation</AccountType>
      <User>fake-user-blob</User>
      <AccessToken>fake-access-token-blob</AccessToken>
    </TradovateOptions>
    <TradovateOptions>
      <Name>Tradeify</Name>
      <TypeName>NinjaTrader.Cbi.TradovateOptions</TypeName>
      <Provider>Provider31</Provider>
      <Mode>Live</Mode>
      <AccountType>Simulation</AccountType>
      <User>fake-user-blob</User>
      <AccessToken>fake-access-token-blob</AccessToken>
    </TradovateOptions>
    <SchwabOptions>
      <Name>My Schwab</Name>
      <TypeName>NinjaTrader.Cbi.SchwabOptions</TypeName>
      <Provider>Provider32</Provider>
      <Mode>Live</Mode>
      <User>fake-schwab-user</User>
      <AccessToken>fake-refresh-token-blob</AccessToken>
    </SchwabOptions>
    <KinetickEODOptions>
      <Name>Kinetick – End Of Day (Free)</Name>
      <TypeName>NinjaTrader.Cbi.KinetickEODOptions</TypeName>
      <Provider>Provider7</Provider>
      <Mode>Free</Mode>
      <User>fake-kinetick-user</User>
      <Password>fake-kinetick-password</Password>
    </KinetickEODOptions>
    <PlaybackOptions>
      <Name>Playback</Name>
      <TypeName>NinjaTrader.Cbi.PlaybackOptions</TypeName>
      <Provider>Playback</Provider>
      <Mode>Playback</Mode>
    </PlaybackOptions>
    <SimulatorOptions>
      <Name>Simulated Data Feed</Name>
      <TypeName>NinjaTrader.Cbi.SimulatorOptions</TypeName>
      <Provider>Simulator</Provider>
      <Mode>Simulator</Mode>
    </SimulatorOptions>
  </ConnectOptions>
</NinjaTrader>";

        private static void TestF17_CatalogParsesConfiguredConnectionsWithoutReadingCredentials()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] F-17: the catalog parses Config.xml <ConnectOptions>, visibility only");

            var rows = BridgeConnectionCatalog.Parse(SampleConfigConnectOptions);
            Assert(rows.Count == 8, string.Format(
                "all eight configured connections are catalogued (found {0})", rows.Count));

            var names = rows.Select(r => r.Name).ToList();
            foreach (var expected in new[]
                {
                    "Apex", "LUCID", "TPT", "Tradeify", "My Schwab",
                    "Kinetick – End Of Day (Free)", "Playback", "Simulated Data Feed"
                })
                Assert(names.Contains(expected), "the catalog names '" + expected + "'");

            var apex = rows.First(r => r.Name == "Apex");
            Assert(apex.TypeName == "NinjaTrader.Cbi.TradovateOptions"
                    && apex.Provider == "Provider31" && apex.Mode == "Live"
                    && apex.AccountType == "Simulation",
                "Apex carries type/provider/mode/accountType from the XML");

            Assert(rows.First(r => r.Name == "Playback").Provider == "Playback",
                "Playback is catalogued as the Playback provider");
            Assert(rows.First(r => r.Name == "My Schwab").Provider == "Provider32",
                "My Schwab is catalogued as Provider32");
            Assert(rows.First(r => r.Name == "Kinetick – End Of Day (Free)").Provider == "Provider7",
                "Kinetick is catalogued as Provider7");

            // The DTO is the whole contract of what this HTTP-reachable component may carry.
            // If a future edit adds a credential field, this fails on the shape before any
            // secret ever leaves Config.xml.
            var fields = typeof(BridgeConfiguredConnection)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => f.Name).ToList();
            Assert(fields.Count == 5 && fields.OrderBy(f => f).SequenceEqual(
                       new[] { "AccountType", "Mode", "Name", "Provider", "TypeName" }.OrderBy(f => f)),
                string.Format("the catalog DTO exposes exactly Name/TypeName/Provider/Mode/AccountType "
                    + "and nothing else (found: {0})", string.Join(",", fields)));
            Assert(!fields.Any(f => f.IndexOf("Password", StringComparison.OrdinalIgnoreCase) >= 0
                                     || f.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0
                                     || string.Equals(f, "User", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(f, "AccessToken", StringComparison.OrdinalIgnoreCase)),
                "and none of those fields is a credential -- the DTO carries no secret by shape");

            // Failure modes: the endpoint must never throw because its inventory file is bad.
            Assert(BridgeConnectionCatalog.Parse(null).Count == 0, "null config -> empty catalog");
            Assert(BridgeConnectionCatalog.Parse("").Count == 0, "blank config -> empty catalog");
            Assert(BridgeConnectionCatalog.Parse("<this is not xml").Count == 0,
                "corrupt config -> empty catalog, not a throw");
            Assert(BridgeConnectionCatalog.Parse("<NinjaTrader></NinjaTrader>").Count == 0,
                "no <ConnectOptions> section -> empty catalog");
        }

        private static void TestF17_CatalogAbsentKeepsOnlyConfiguredRows()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] F-17: absent() keeps only the configured connections with no live presence");

            var catalog = BridgeConnectionCatalog.Parse(SampleConfigConnectOptions);
            var absent = BridgeConnectionCatalog.Absent(catalog, new[] { "Playback", "TPT" });

            Assert(absent.Count == 6,
                string.Format("8 configured minus 2 present = 6 absent rows (got {0})", absent.Count));
            Assert(absent.All(a => !string.Equals(a.Name, "Playback", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(a.Name, "TPT", StringComparison.OrdinalIgnoreCase)),
                "the present names are excluded, case-insensitively");
            Assert(absent.Any(a => a.Name == "Apex" && a.Provider == "Provider31"),
                "an absent Provider31 broker (Apex) is in the absent set");

            Assert(BridgeConnectionCatalog.Absent(catalog, null).Count == 8,
                "no present names -> every configured row is absent");
            Assert(BridgeConnectionCatalog.Absent(null, new[] { "anything" }).Count == 0,
                "null catalog -> nothing absent");
        }

        private static void TestF17_TheConnectionCallIsMarshalledToTheUiDispatcher()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] F-17: connect/disconnect is marshalled to the UI dispatcher (P2-112 family)");

            // Measured live 2026-08-15: the bare `Connection.Connect(target.Options)` on the HTTP
            // thread returned without throwing and the connection stayed Disconnected -- a
            // UI-affine call from the wrong thread. The fix is the dispatcher block; this source
            // assertion is the bridge's own version of P2-112's test, because McpBridgeAddOn.cs
            // is in no executable build (tests/README.md) and nt_compile is the only executor.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            var invoke = Regex.Match(code,
                @"connectionUiDispatcher\.InvokeAsync\(\(\) =>\s*\{(.*?)\}\);",
                RegexOptions.Singleline);
            Assert(invoke.Success,
                "the connection call sits inside a connectionUiDispatcher.InvokeAsync(() => ...) block");

            // ⚠️ GUARD THE NEGATIVE CONTROL. On the first run of the InvokeAsync change this
            // method did not FAIL, it CRASHED the whole harness: the match had gone false, so
            // Groups[0].Value was "" and String.Replace threw ArgumentException, taking every
            // later test with it. A gate that dies where it meant to report red tells you less
            // than one that reports red, and it stops the run.
            string codeWithoutInvoke = invoke.Success
                ? code.Replace(invoke.Groups[0].Value, "")
                : code;
            Assert(!codeWithoutInvoke.Contains("Connection.Connect(target.Options)"),
                "no bare Connection.Connect(target.Options) exists outside the marshalled block");
            Assert(!codeWithoutInvoke.Contains("target.Disconnect()"),
                "no bare target.Disconnect() exists outside the marshalled block");

            if (invoke.Success)
            {
                var inner = invoke.Groups[1].Value;
                // ⚠️ REPOINTED 2026-08-15, same commit as the credential route. The connect half
                // used to pass `target.Options` (the live Account.All object) -- which reached
                // Tradovate.Adapter.Connect with user='' even though it carried the decrypted
                // credential, while the menu connected cleanly. The fix resolves from the
                // CANONICAL `Core.Globals.ConnectOptions` entry keyed by Name (the documented
                // pattern); `Connection.Connect` decrypts the credential from THAT object, and
                // does not forward credential fields from one you pass it. The anchor asserting
                // `Connection.Connect(target.Options)` matched 0 times in that very commit -- the
                // gate working; the subject is unchanged, so it is repointed, never retired.
                Assert(inner.Contains("Connection.Connect(connectOptions)"),
                    "the connect half calls Connection.Connect(connectOptions), the canonical-resolved options");
                Assert(inner.Contains("Core.Globals.ConnectOptions"),
                    "the connect half resolves from Core.Globals.ConnectOptions -- the credential route");
                Assert(inner.Contains("target.Disconnect()"),
                    "and the disconnect half still calls target.Disconnect()");
            }

            Assert(Regex.IsMatch(code, @"var connectionUiDispatcher\s*=\s*System\.Windows\.Application\.Current\?\.Dispatcher"),
                "the dispatcher is Application.Current.Dispatcher, not a new thread");

            // ⚠️ THE BOUND IS THE LOAD-BEARING HALF, so it gets its own assertion rather than
            // riding along on "a dispatcher is mentioned". A blocking `Dispatcher.Invoke` has no
            // timeout: a busy NT8 UI thread parks the HTTP listener forever and the bridge stops
            // answering, panic flatten included. `InvokeAsync` alone has the opposite failure --
            // it would report on having QUEUED the work (`P1-105`). Only the bounded WAIT is
            // both non-hanging and honest, so assert the wait, not the marshalling.
            Assert(Regex.IsMatch(code, @"op\.Task\.Wait\(TimeSpan\.FromSeconds\(\d+\)\)"),
                "the queued operation is waited on with a BOUNDED timeout");
            Assert(Regex.IsMatch(code,
                    // ⚠️ `[^)]*` for the argument was wrong on the first run, and quietly:
                    // TimeSpan.FromSeconds(5) contains a `)`, so the class stopped early and the
                    // assertion failed on correct code. A negative character class cannot span a
                    // nested call -- match the nesting explicitly.
                    @"if\s*\(\s*!\s*op\.Task\.Wait\(TimeSpan\.FromSeconds\(\d+\)\)\s*\)\s*return[\s\S]{0,300}?UI_THREAD_BUSY"),
                "and a wait that times out RETURNS UI_THREAD_BUSY -- computed is not used");
            Assert(!Regex.IsMatch(code, @"connectionUiDispatcher\.Invoke\("),
                "no unbounded blocking Dispatcher.Invoke on the connection path");
        }


        // ============================================================================
        // P2-27's validator MOVED TO nt8-riskguard, 2026-08-16 (session 48).
        //
        // Nine assertions stood here, red, pinning a `BridgeGuardConfigEdit` in THIS repo.
        // They are gone because the class they pinned is not being built here, and a test that
        // can never go green is not a test-first gate -- it is a red CI, which this project has
        // twice let run for ten pushes while everyone stopped reading the signal.
        //
        // ⚠️ THEY WERE NOT DELETED TO GO GREEN. The validator is `GuardConfigEdit` in
        // nt8-riskguard, and the reason is structural: the submodule direction is bridge ->
        // core, so a class here is unreachable from `RiskGuardWindow.OnSaveConfigClick` -- the
        // OTHER writer to the same config, and as it turns out one of THREE (P2-119). A
        // validator only one of three writers can call is the defect shape, not the fix.
        //
        // What replaced them is strictly stronger: 21 assertions in the core harness, EXECUTED
        // rather than reflected at a maybe-absent type, plus `mutation/mutate_p227.py` at 11
        // mutants / 0 survivors. Two of those mutants are the specs these tests encoded.
        //
        // WHAT WAS GENUINELY MISSING came back with P2-120, below: the ROUTE assertions. The
        // shape changed on the way, and the change is worth reading. The plan was to assert that
        // the route CONSULTS the validator. It does not, and should not -- the validator moved
        // INSIDE `SaveAndReloadConfig`, so all three writers get it whether they remember to ask
        // or not. A route-level call would have been a fourth copy of a decision that now has
        // exactly one home. What the route owns is the OUTCOME, so that is what is pinned.
        // ============================================================================

        /// <summary>
        /// P2-120. The route discarded SaveAndReloadConfig's return value and answered
        /// `success = true` whatever happened, because the method used to return void and
        /// swallow its own exception. A refusal and a failed write both reported "applied" --
        /// the API returning the NEGATION of its own outcome, which is worse than returning
        /// nothing, because a script acting on it proceeds.
        ///
        /// ⚠️ A SOURCE gate, and it proves less than an executed test would: McpBridgeAddOn.cs
        /// is in no test build (P2-27). It carries a NEGATIVE CONTROL for that reason -- a
        /// pattern that must be ABSENT as well as ones that must be present, so the gate cannot
        /// pass by looking at nothing.
        /// </summary>
        private static void TestP2120_TheConfigRouteReportsWhatTheSaveDid()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-120: the config POST route reports the save's real outcome");
            // Comments STRIPPED, per this file's convention: the doc comment above quotes the
            // defective reply verbatim, and the negative control below forbids exactly that
            // string. Without stripping, this test would fail on its own documentation -- and
            // the fix somebody would reach for is deleting the explanation.
            string src = StripComments(File.ReadAllText(BridgeSourcePath()));

            Assert(src.IndexOf("SaveAndReloadConfig", StringComparison.Ordinal) >= 0,
                "Positive control: the route still calls SaveAndReloadConfig at all.");

            Assert(src.IndexOf("var saved = RiskGuardAddOn.Instance.SaveAndReloadConfig(cfg);",
                               StringComparison.Ordinal) >= 0,
                "P2-120: the route CAPTURES the result. Discarding it is the defect, and it "
                + "compiles silently either way because C# lets a return value be dropped.");

            Assert(src.IndexOf("if (!saved.Saved)", StringComparison.Ordinal) >= 0,
                "P2-120: the route BRANCHES on the outcome. A gate that a value is captured is "
                + "not a gate that it is used -- four mutants in this project have beaten that "
                + "assumption.");

            Assert(src.IndexOf("saved.Refusal", StringComparison.Ordinal) >= 0,
                "P2-120: a refusal is surfaced to the caller rather than folded into a generic "
                + "failure. The refusal names the field, and that sentence is the whole point of "
                + "GuardConfigEdit.");

            // ⚠️ THE NEGATIVE CONTROL. Every assertion above is satisfiable by a file that also
            // still contains the old unconditional literal somewhere on the success path. This
            // is the one that fails if the defect is left in place beside the fix.
            Assert(src.IndexOf("return new { success = true, status = \"applied\", config =",
                               StringComparison.Ordinal) < 0,
                "P2-120: the old unconditional `success = true, status = \"applied\"` reply is "
                + "GONE, not merely bypassed. Leaving it reachable is how a fix and its defect "
                + "ship together.");
        }

        public static int Run()
        {
            // cp1252 is NOT native on net8.0-windows, and F-17's mojibake repair depends on it
            // (NT8, net48, has it natively). Registering here is what lets the harness exercise
            // the SAME repair path the live addon runs.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

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
            TestP2_115_TheHealthFlagCanBeFalse();
            TestP2_115_ADormantPlaybackConnectionIsNotAMarketFeed();
            TestP2_115_ALiveBrokerConnectionStillReportsTrue();
            TestP2_115_AnUnknownConnectionStateFailsClosed();
            TestP2_115_TheRouteNoLongerDerivesTheFlagFromTheAccountCount();
            TestF17_AnUnknownConnectionIsRefusedNamingTheRealOnes();
            TestF17_DashAndMojibakeVariantsOfANameResolve();
            TestF17_ABlankConnectionNameIsNotAWildcard();
            TestF17_DisconnectingIsRefusedWhileAnythingIsLive();
            TestF17_TheDisconnectRouteActsOnTheRefusalRatherThanComputingIt();
            TestF17_CatalogParsesConfiguredConnectionsWithoutReadingCredentials();
            TestF17_CatalogAbsentKeepsOnlyConfiguredRows();
            TestF17_TheConnectionCallIsMarshalledToTheUiDispatcher();
            TestP334_EnforcingIsDerivedFromTheCopierGate();
            TestP334_TheNotEnforcingReasonNamesTheGlobalSwitch();
            TestP334_TheEndpointExposesAndCanSetTheMode();
            TestP3122_TheBindingGateIsNamedBeforeTheSurprisingOne();
            TestP3122_TheLabelAndTheSentenceAreOneOrdering();
            TestP1125_TheSystemCellReportsTheCopiersOwnMode();
            TestP1125_SeverityIsANameAndAnUnknownRankIsNotHealthy();
            TestP1125_TheSnapshotRouteDelegatesEveryDecision();
            TestP1125_ThePageActuallyReadsWhatTheRouteNowSends();
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
            TestP1_105_TheFiledDefectExactly();
            TestP1_105_NothingMatchedIsNotAClose();
            TestP1_105_AClosedPositionIsReportedClosed();
            TestP1_105_SubmittedButUnconfirmedIsItsOwnAnswer();
            TestP1_105_ASymbolPrefixIsNotAMatch();
            TestP1_105_TheSymbolRootMatchesHoweverItWasSpelled();
            TestP1_105_EverySymbolMeansEverySymbol();
            TestP1_105_AnUnnameableInstrumentIsOutOfScope();
            TestP1_105_TheAccountFilterIsExactOrEverything();
            TestP1_105_ScopeIsBothHalvesAndNothingElse();
            TestP1_105_TheEndpointObservesRatherThanClaiming();
            TestP1_105_BothPassesUseTheSameScopePredicate();
            TestP2_109_TheFilteredAndUNfilteredAnswersMustDIFFER();
            TestP2_109_AnOmittedAccountStillMeansEveryAccount();
            TestP2_109_TheCloseAndOrdersPathsShareONEDefinition();
            TestP2_109_TheLimitIsParsedSafelyAndClamped();
            TestP2_109_TheOffsetIsParsedSafely();
            TestP2_109_ThePageArithmetic();
            TestP2_109_TheRouteActuallyPassesTheQueryThrough();
            TestP3_111_TheFiledDefectExactly();
            TestP3_111_TheAdvertisedCapIsActuallyEnforced();
            TestP3_111_AnUnknownPeriodIsRefusedAndNotGUESSED();
            TestP3_111_BarsArePagedFromTheRIGHTEdge();
            TestP3_111_PagingPastTheStartIsEmptyNotWrapped();
            TestP3_111_TheREQUESTGrowsWithTheOffsetButTheRESPONSEDoesNot();
            TestP3_111_TheOrdersAndBarsPathsShareONEDefinition();
            TestP3_111_TheBarsRouteHandsTheRawStringsThrough();
            TestP3_111_NoBarsPathParsesAPeriodWithoutResolvingItFirst();
            TestP3_111_HasMoreIsNotStartGreaterThanZero();
            TestP1_102_AnUnknownLockoutActionIsREFUSEDNotAnsweredAsAStatus();
            TestEveryResolverSiteACTSOnTheRefusal();
            TestP2120_TheConfigRouteReportsWhatTheSaveDid();
            TestP2127_TheTwoIncomingSeverityScalesAreConvertedNotShared();
            TestP2127_TheWorstOfASetIsTheSmallestAndEmptyIsNotHealthy();
            TestP2127_TheTreeGroupsByGroupThenByLeader();
            TestP2127_EveryAccountAppearsExactlyOnce();
            TestP2127_EverythingSortsWorstFirstAndNothingHidesUnderAParent();
            TestP2127_OneNodePerFollowerNotOnePerRow();
            TestP2127_TheOrderIsTOTALSoTheSameDataGivesTheSameList();
            TestP2127_AnAccountWithNoCopierRelationshipIsNotRankedWORST();
            TestP2127_TheUnlinkedNodeIsPresentEvenWhenItIsEmpty();
            TestP1131_EveryNonTerminalStateWouldBeStranded();
            TestP1131_TheThreeTerminalStatesAndOnlyThose();
            TestP1131_AnUnknownStateNameFailsSAFE();
            TestP1131_NoBridgePathKeepsItsOwnStateList();
            // P2-178. The exec-time stamp that lied about its zone. The two DST cases are the
            // discriminators -- a constant 4h subtraction passes one and fails the other.
            TestP2178_TheMeasuredDefectConvertsToTrueUtc();
            TestP2178_TheOffsetFollowsTheDateNotAConstant();
            TestP2178_TheTrailingZIsNowTrueUtc();
            TestP2178_TheSpringForwardGapDoesNotThrow();
            TestP2178_AKindTaggedInputIsTreatedAsAWallClock();
            TestP2178_BothBridgeCallSitesConvertRatherThanStampingZ();
            // P2-181. The bridge twin of P2-150 -- PlaceOcoOrder's dead synchronous verdict.
            TestP2181_TheOcoPathReportsPendingLegsNotADeadVerdict();
            // P2-127 slice 3: the inspector's three tabs, the only tabs in the app.
            TestP2127_AllThreeTabsAlwaysExistInSectionFoursOrder();
            TestP2127_TheRiskTabFoldsInertNotJustUnevaluated();
            TestP2127_TheBadgeIsRecountedFromTheRowsAndFollowsTheSelection();
            TestP2127_TheLiveInventoryMapsOntoTheTabsInput();
            // Both written from arbitrating the panel by hand: one finding of five held.
            TestP2127_TheRouteServesTheTabsAndDecidesNothing();
            TestP2127_AKnownCleanTabDoesNotRankAsTheWorst();
            TestP2127_AnUnreadableRuleStateRanksWorstNotBest();
            TestP2127_TheAccountFilterIgnoresCaseLikeTheCoreDoes();
            // Slice 4: the events pane and section 4 decision 4's system row -- section 4's
            // last two unbuilt regions. Every threshold in them was set by measuring the
            // deployed box, not by reading the code; the numbers are in the block header.
            TestP2127_TheEventsPaneDropsTheMeasuredTelemetry();
            TestP2127_RepeatedEventsCollapseAndKeepTheirCount();
            TestP2127_ASelectionNeverHidesABoxWideEvent();
            TestP2127_TheNewestEventsSurviveTheCap();
            TestP2127_AnUnknownEventTypeDoesNotRankRoutine();
            TestP2127_TheSystemRowNeverMergesGuardAndCopier();
            TestP2127_AnUnevaluatedRuleBeatsEveryOtherGuardState();
            TestP2127_AnAccountOnNoConnectionIsNotAFeedFailure();
            TestP2127_TheCopierCellDefersToItsOwnProducer();
            TestP2127_TheRouteServesEventsAndSystemAndDecidesNeither();
            TestP2127_ThePageRendersEventsAndTheSystemRow();

            // P1-149. The configured contract cap, enforced BEFORE the order exists. The refusals
            // are the discriminators: a gate that allowed everything would still pass every
            // "allowed" case below. [[detector-needs-a-negative-test]].
            TestP1149_TheMeasuredDefectIsRefused();
            TestP1149_NoCapConfiguredAllowsEverything();
            TestP1149_ACapNeverRefusesTheOrderThatClosesThePosition();
            TestP1149_AnUnderCapOrderThatLeavesAnOverCapPositionIsRefused();
            TestP1149_TheBoundaryIsInclusive();
            TestP1149_AReversalIsJudgedOnWhatItLeavesAndOffersTheExit();
            TestP1149_ThePositionQuantityIsReadAsAMagnitude();
            TestP1149_EveryOrderPathConsultsTheGate();
            TestP1149_TheBridgeDoesNotCarryItsOwnCopyOfTheCap();
            TestBacktestTradesCarryExcursionsAndPerLegFields();
            TestP2138_TheLivePayloadMapsOntoTheTreesInput();
            TestP2138_AFollowerWithTwoRelationshipsShowsTheRefusal();
            TestP2138_TheRouteServesTheTreeItBuilds();
            TestP2138_ThePageRendersTheFleetTree();
            // P2-126. The page dispatches the copier actions the backend already accepts.
            TestP2126_ThePageDispatchesTheCopierActions();
            // P2-127 follow-up. The dormant-account filter.
            TestP2127_DormantAccountsAreFlaggedNotHidden();
            TestP2127_ThePageRendersTheDormantFilter();

            // Harness self-check, mirroring the core suite's. A runner that silently skips
            // tests is worse than no runner, so the count is asserted rather than assumed.
            Console.WriteLine("\n[TEST] HARNESS: every declared test ran");
            const int declared = 124;
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
        // P3-122 / P1-125. EXECUTED.
        //
        // P3-122: the reason ordering. `NotEnforcingReason` named the mode LAST, on the
        // stated grounds that it is "the one an operator will not think to check" -- right
        // about which reason SURPRISES, wrong about which one BINDS. An enabled, unarmed
        // relationship under a `shadow` copier was told it "copies to SIMULATION followers
        // only" while the copy path blocks before any follower is reached, so it copies to
        // simulation followers too. The sentence described a behaviour that was not
        // happening, which is worse than saying nothing.
        //
        // Found by comparing two readers of one question: CopierStatusView (the WPF window,
        // core) ranks mode ABOVE armed and got this right. Both now do.
        //
        // P1-125: the browser page never stated the copier's global mode at all.
        // ================================================================================

        private static void TestP3122_TheBindingGateIsNamedBeforeTheSurprisingOne()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-122: the reason names the gate that BINDS, not the one that surprises");

            // ⚠️ THE REGRESSION ROW, verbatim from the ticket: enabled, NOT armed, shadow.
            // Under the old ordering this returned the simulation sentence.
            string unarmedShadow = CopierEnforcementView.NotEnforcingReason(true, false, false, "shadow");
            Assert(unarmedShadow != null
                   && unarmedShadow.IndexOf("copies to SIMULATION", StringComparison.OrdinalIgnoreCase) < 0,
                "an enabled, UNARMED relationship under a `shadow` copier is NOT described as "
                + "copying to simulation followers -- in shadow the copy path blocks before any "
                + "follower is reached, so that sentence is false in the direction that reassures. "
                + "Got: " + (unarmedShadow ?? "<null>"));
            // ⚠️ Paired with the POSITIVE half, because the assertion above is a substring test
            // and a reworded false claim would walk past it. Any sentence that also states
            // nothing is submitted contradicts itself visibly if the promise creeps back.
            Assert(unarmedShadow != null
                   && unarmedShadow.IndexOf("submits nothing", StringComparison.OrdinalIgnoreCase) >= 0,
                "and it states the TRUE behaviour outright -- nothing is submitted, to anyone. "
                + "Got: " + (unarmedShadow ?? "<null>"));
            Assert(unarmedShadow != null
                   && unarmedShadow.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0,
                "it names the MODE instead. Got: " + (unarmedShadow ?? "<null>"));

            // ⚠️ And the moved branch must not have taken the arming claim with it. The old
            // mode sentence asserted "enabled and armed"; reached now by unarmed rows too, an
            // unchanged string would state the opposite of the row it is explaining.
            Assert(unarmedShadow != null && unarmedShadow.IndexOf("and armed", StringComparison.Ordinal) < 0,
                "and it does NOT claim the relationship is armed, because this row is not. "
                + "Got: " + unarmedShadow);
            string armedShadow = CopierEnforcementView.NotEnforcingReason(true, true, false, "shadow");
            Assert(armedShadow != null && armedShadow.IndexOf("armed", StringComparison.Ordinal) >= 0,
                "while an ARMED row under the same mode still says so -- the two rows differ in "
                + "a way the operator can act on. Got: " + (armedShadow ?? "<null>"));

            // The `disabled` mode takes the same path and must keep its own name (P1-87).
            string unarmedOff = CopierEnforcementView.NotEnforcingReason(true, false, false, "disabled");
            Assert(unarmedOff != null
                   && unarmedOff.IndexOf("simulation", StringComparison.OrdinalIgnoreCase) < 0
                   && unarmedOff.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0,
                "the same row under a `disabled` copier names that mode, and still does not "
                + "promise simulation followers. Got: " + (unarmedOff ?? "<null>"));

            // ⚠️ THE OTHER DIRECTION, which is the half a reordering breaks silently: the
            // simulation sentence must still be reachable. It is the correct answer whenever
            // the copier IS acting, and a reorder that deleted it in practice would pass every
            // assertion above.
            string simOnly = CopierEnforcementView.NotEnforcingReason(true, false, true, "live");
            Assert(simOnly != null
                   && simOnly.IndexOf("SIMULATION", StringComparison.OrdinalIgnoreCase) >= 0,
                "with the copier LIVE, an unarmed relationship really does copy to simulation "
                + "followers only, and is told so. Got: " + (simOnly ?? "<null>"));

            // The nearest actionable cause still wins over the global one.
            string off = CopierEnforcementView.NotEnforcingReason(false, false, false, "shadow");
            Assert(off != null && off.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0
                   && off.IndexOf("COPIER", StringComparison.Ordinal) < 0,
                "and a relationship the operator switched off is explained by ITS OWN state, not "
                + "by the global mode -- that is the switch they would go and flip. Got: "
                + (off ?? "<null>"));

            // Enforcing is still silent.
            Assert(CopierEnforcementView.NotEnforcingReason(true, true, true, "live") == null,
                "an enforcing relationship still has no reason at all");
        }

        private static void TestP3122_TheLabelAndTheSentenceAreOneOrdering()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-122: the short label and the full sentence are ONE decision");

            // The label exists so a table cell need not carry a 30-word paragraph. If it were
            // computed anywhere else it would be free to disagree with the sentence beside it,
            // which is this ticket at a smaller scale. Drive the whole space.
            string[] modes = { "live", "shadow", "disabled", "Shadow_Mode_Typo", "", null };
            int refusals = 0, enforcing = 0;

            foreach (bool isEnabled in new[] { true, false })
            foreach (bool armed in new[] { true, false })
            foreach (bool acting in new[] { true, false })
            foreach (string mode in modes)
            {
                var why = CopierEnforcementView.WhyNotEnforcing(isEnabled, armed, acting, mode);
                string sentence = CopierEnforcementView.NotEnforcingReason(isEnabled, armed, acting, mode);
                bool isEnf = CopierEnforcementView.IsEnforcing(isEnabled, armed, acting);

                if (why == null)
                {
                    enforcing++;
                    if (!(isEnf && sentence == null))
                        Assert(false, "a null refusal must mean IsEnforcing and a null sentence, "
                            + "for " + isEnabled + "/" + armed + "/" + acting + "/" + (mode ?? "<null>"));
                    continue;
                }

                refusals++;
                if (isEnf)
                    Assert(false, "a refusal was produced for a relationship that IS enforcing: "
                        + isEnabled + "/" + armed + "/" + acting + "/" + (mode ?? "<null>"));
                if (why.Sentence != sentence)
                    Assert(false, "NotEnforcingReason disagreed with WhyNotEnforcing().Sentence for "
                        + isEnabled + "/" + armed + "/" + acting + "/" + (mode ?? "<null>"));
                if (string.IsNullOrWhiteSpace(why.Label))
                    Assert(false, "a refusal carried no label for "
                        + isEnabled + "/" + armed + "/" + acting + "/" + (mode ?? "<null>"));
                if (why.Label.Length >= why.Sentence.Length)
                    Assert(false, "the label is not shorter than the sentence for "
                        + isEnabled + "/" + armed + "/" + acting + "/" + (mode ?? "<null>")
                        + " -- a 'short' form as long as the paragraph is not one");
            }

            Assert(refusals == 42 && enforcing == 6, string.Format(
                "all 48 combinations answered, 42 refusing and 6 enforcing (the 6 being "
                + "enabled+armed+acting, once per mode string -- IsEnforcing does not read the "
                + "mode NAME, only the answer the engine gave about it). Got {0} and {1}",
                refusals, enforcing));

            // A null/blank mode is quoted back as something rather than vanishing mid-sentence.
            var blank = CopierEnforcementView.WhyNotEnforcing(true, true, false, null);
            Assert(blank != null && blank.Sentence.Contains("(unset)"),
                "a null mode reads as '(unset)', not as an empty gap the operator has to "
                + "interpret. Got: " + (blank == null ? "<null>" : blank.Sentence));
        }

        private static void TestP1125_TheSystemCellReportsTheCopiersOwnMode()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-125: the system cell states the COPIER's mode, and the not-loaded case");

            var live = CopierEnforcementView.SystemCell(
                "live", true, 0, "[ COPIER LIVE - 2 ARMED ]", "2 relationships, 2 armed for live.", 0);
            Assert(live.Loaded && live.IsActing && live.Mode == "live" && live.Severity == "ok",
                "a live, acting copier is reported as loaded, acting and ok");
            Assert(live.Headline == "[ COPIER LIVE - 2 ARMED ]"
                   && live.Detail == "2 relationships, 2 armed for live.",
                "and the headline and detail are passed through UNCHANGED -- they are "
                + "CopierStatusView's words, and rewording them here is how this page would "
                + "start disagreeing with the window about the same copier");

            var shadow = CopierEnforcementView.SystemCell(
                "shadow", false, 2, "[ COPIER SHADOW ]", "Shadow: the copier logs and submits nothing.", 1);
            Assert(!shadow.IsActing && shadow.Severity == "warn" && shadow.ConfigConflicts == 1,
                "a shadow copier is not acting, warns, and carries its conflict count");

            var blank = CopierEnforcementView.SystemCell("   ", false, 3, "h", "d", 0);
            Assert(blank.Mode == "(unset)",
                "a blank mode renders as '(unset)' -- an empty header field reads as 'nothing to "
                + "report', which is the one thing it does not mean");

            // ⚠️ The state that must not look like health. `TradeCopierEngine.Instance` is null
            // on a box where the copier addon failed to load, and the route has to answer
            // something. A blank indicator in the header is read as fine.
            var none = CopierEnforcementView.NotLoadedCell();
            Assert(!none.Loaded && !none.IsActing && none.Severity == "critical",
                "no copier at all is CRITICAL and not acting -- not a blank");
            Assert(none.Headline.IndexOf("NOT LOADED", StringComparison.OrdinalIgnoreCase) >= 0,
                "and it says so in the headline. Got: " + none.Headline);
            Assert(none.Detail.IndexOf("not the same", StringComparison.OrdinalIgnoreCase) >= 0,
                "and distinguishes itself from a loaded copier with no relationships, because "
                + "the page already tells those two apart for the rows. Got: " + none.Detail);
            Assert(none.Mode != null && none.Mode != "live",
                "and it never reports a mode that would read as working");
        }

        private static void TestP1125_SeverityIsANameAndAnUnknownRankIsNotHealthy()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-125: severity crosses the wire as a NAME, and an unmapped rank is not 'ok'");

            // CopierStatusSeverity is Ok=0, Info=1, Warn=2, Critical=3.
            Assert(CopierEnforcementView.SeverityName(0) == "ok"
                   && CopierEnforcementView.SeverityName(1) == "info"
                   && CopierEnforcementView.SeverityName(2) == "warn"
                   && CopierEnforcementView.SeverityName(3) == "critical",
                "the four ranks map to the four names the page keys its colour off");

            // ⚠️ A member added to the enum upstream and not mapped here is NOT evidence of
            // health -- the same rule CopierSnapshotJson.SeverityRank applies when it ranks an
            // unrecognised verdict worst. Fail loud, in the direction that gets looked at.
            foreach (int rank in new[] { -1, 4, 99, int.MinValue, int.MaxValue })
                Assert(CopierEnforcementView.SeverityName(rank) == "critical",
                    "an unmapped rank (" + rank + ") reads as critical, never as ok");

            // ⚠️ AND IT IS A NAME, WHICH IS LOAD-BEARING. The rows in the SAME payload carry a
            // numeric `severity` from CopierSnapshotJson.SeverityRank where **0 is the worst**.
            // Two numbers with opposite polarity in one document is a trap for the next
            // consumer; a colour keyed off the wrong one paints an ORPHAN green.
            foreach (int rank in new[] { 0, 1, 2, 3, 7 })
            {
                string name = CopierEnforcementView.SeverityName(rank);
                int parsed;
                Assert(!int.TryParse(name, out parsed),
                    "rank " + rank + " crosses the wire as a word, not a digit");
            }
        }

        /// <summary>
        /// The route half, in McpBridgeAddOn.cs and therefore in no test build (P2-27). A
        /// SOURCE gate, and it proves less than the five above -- said plainly, per 5.26.
        ///
        /// What it is actually for: this route is the one place the new payload could grow a
        /// SECOND opinion about whether the copier is copying. The whole design is that it has
        /// none, and a source gate is the only thing that can see that here.
        /// </summary>
        private static void TestP1125_TheSnapshotRouteDelegatesEveryDecision()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-125: the snapshot route decides NOTHING for itself (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            var body = Regex.Match(code,
                @"private object GetCopierSnapshot\(\)\s*\{(?<b>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}");
            Assert(body.Success,
                "GetCopierSnapshot is locatable, so the assertions below inspect the METHOD and "
                + "not the whole 7,000-line file -- a check that does not state its region is a "
                + "check that passes over code it never read");
            string method = body.Success ? body.Groups["b"].Value : "";

            Assert(Regex.IsMatch(code, @"case ""/api/copier/snapshot"":\s*return GetCopierSnapshot\(\);"),
                "and the route calls it");

            // Delegation, and USED not merely called: four gates have now been beaten by a
            // mutant that left a call in place and threw its answer away.
            Assert(Regex.IsMatch(method, @"CopierStatusView\.Describe\("),
                "the headline comes from CopierStatusView.Describe -- the same producer the WPF "
                + "window reads, which is why the two surfaces cannot disagree (P3-122)");
            Assert(Regex.IsMatch(method, @"headline\.Severity") && Regex.IsMatch(method, @"headline\.Text"),
                "and its answer is USED: a gate that Describe is CALLED passes under a mutant "
                + "that discards what it returns");
            Assert(Regex.IsMatch(method, @"CopierEnforcementView\.SystemCell\(")
                   && Regex.IsMatch(method, @"CopierEnforcementView\.NotLoadedCell\(\)"),
                "the wire shape and the no-copier case come from the tested class, including the "
                + "null-engine branch -- the state where there is nothing to ask");
            Assert(Regex.IsMatch(method, @"CopierEnforcementView\.WhyNotEnforcing\(")
                   && Regex.IsMatch(method, @"notEnforcingReason")
                   && Regex.IsMatch(method, @"notEnforcingLabel"),
                "and every row carries the refusal, in both lengths -- rendering it is what makes "
                + "P3-122's ordering reachable by an operator at all");

            // ⚠️ NEGATIVE CONTROL for the two patterns above. A regex that cannot fail is a
            // comment; these are shown failing against a doctored copy in the same run.
            string neutered = method
                .Replace("CopierStatusView.Describe(", "SomethingElse.Describe(")
                .Replace("CopierEnforcementView.SystemCell(", "LocalCell(");
            Assert(!Regex.IsMatch(neutered, @"CopierStatusView\.Describe\(")
                   && !Regex.IsMatch(neutered, @"CopierEnforcementView\.SystemCell\("),
                "and both patterns DO fail when the delegation is removed");

            // The re-derivation this design exists to forbid. If the route ever compares the
            // mode to a literal itself, there are two definitions of an acting copier again --
            // which is P1-100, P2-98/P1-99, P1-105 and P3-111 at a fifth site.
            Assert(!Regex.IsMatch(method, @"copierMode\s*==\s*""|""live""\s*==\s*copierMode"),
                "and the method NEVER compares the mode to a literal -- TradeCopierEngine."
                + "IsCopierActingMode owns that, and a second copy is how the report drifts "
                + "from the gate");
        }

        /// <summary>
        /// The page half. `ui/index.html` is in no test build and no mutation battery can
        /// reach it, so this is a source gate over a static asset and it proves the least of
        /// anything here -- but the DEFECT WAS EXACTLY THIS: the fields existed in the API and
        /// the page read none of them. Measured before the fix: `copierMode`,
        /// `notEnforcingReason` and `configConflicts` appeared **0** times in this file and
        /// **21** times in the two sources that produce them.
        /// </summary>
        private static void TestP1125_ThePageActuallyReadsWhatTheRouteNowSends(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-125: the page reads the copier's state it is now sent (SOURCE gate)");

            string page = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "ui", "index.html"));
            Assert(File.Exists(page), "the served page is readable at " + page);
            if (!File.Exists(page)) return;

            string html = File.ReadAllText(page);

            Assert(html.Contains("hdrcopier"),
                "the header has a second indicator -- it showed the GUARD's mode and nothing "
                + "else, and an operator told about one mode assumes both were covered");
            Assert(Regex.IsMatch(html, @"data\.system") && Regex.IsMatch(html, @"sys\.mode"),
                "and it renders the `system` block the route now sends, rather than ignoring it");
            Assert(Regex.IsMatch(html, @"sys\.isActing"),
                "including whether the copier is ACTING, which is the whole question: a "
                + "`disabled` copier rendered identically to a working one");
            Assert(Regex.IsMatch(html, @"r\.notEnforcingLabel") && Regex.IsMatch(html, @"r\.notEnforcingReason"),
                "and each row shows why it is not enforcing -- P3-122 is a defect in a string, "
                + "and a string nothing displays is not reachable by the operator");
            Assert(Regex.IsMatch(html, @"renderCopierSystem\(data\.system\);"),
                "the indicator is rendered BEFORE the error and empty branches return, so a "
                + "bridge that did not answer leaves no blank where a state should be");

            // The page must not have started deciding for itself. Its whole contribution is a
            // colour, and the name it keys that colour off comes from the tested class.
            Assert(!Regex.IsMatch(html, @"sys\.mode\s*===?\s*""live"""),
                "and the page NEVER compares the copier mode to a literal -- the acting answer "
                + "is computed by the engine and shipped, not re-decided in JavaScript");
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


        // ================================================================================
        // P1-105. `nt_close_position` returned `positionClosed: true` having submitted
        // nothing. EXECUTED, not grepped -- BridgeClosePlan takes strings and ints and
        // names no NT8 type. The two SOURCE gates at the end cover what stays in
        // McpBridgeAddOn.cs: how the handler builds these arguments and what it reports.
        // ================================================================================

        private static void TestP1_105_TheFiledDefectExactly()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: the live measurement -- one position matched, nothing submitted, still open");

            // Sim101 2026-08-14 13:46:33Z: long 11 MNQ, Flatten called, NO order reached the
            // book (no ORDER_UPDATE in interventions.jsonl), position still long 11. The old
            // handler answered {"status": "flattened", "positionClosed": true}.
            Assert(!BridgeClosePlan.PositionClosed(positionsMatched: 1, positionsStillOpen: 1),
                "a matched position that is still open is NOT closed -- this is the exact live case");
            Assert(BridgeClosePlan.StatusFor(1, 1, 0) == "close_not_submitted",
                "and the status says nothing reached the book, which is what the operator needed to know");
            Assert(BridgeClosePlan.StatusFor(1, 1, 0) != "flattened",
                "the constant string this replaced was 'flattened' regardless of any of it");
        }

        private static void TestP1_105_NothingMatchedIsNotAClose()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: matching no position is not a successful close");

            Assert(!BridgeClosePlan.PositionClosed(positionsMatched: 0, positionsStillOpen: 0),
                "zero matched positions is not a close, however flat the account looks afterwards");
            Assert(BridgeClosePlan.StatusFor(0, 0, 0) == "nothing_to_close",
                "and it says so -- a typo'd symbol lands here, and reporting it as a close is the "
                + "defect coming back wearing new fields");
        }

        private static void TestP1_105_AClosedPositionIsReportedClosed()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: the healthy path still reports success (NEGATIVE test)");

            // For a detector, the negative test is the one that proves it works: a rule that
            // never says "closed" passes every test above.
            Assert(BridgeClosePlan.PositionClosed(positionsMatched: 1, positionsStillOpen: 0),
                "a matched position observed flat IS closed");
            Assert(BridgeClosePlan.StatusFor(1, 0, 1) == "flattened",
                "and the status is 'flattened' when it actually flattened");
            Assert(BridgeClosePlan.StatusFor(3, 0, 0) == "flattened",
                "observed flat wins over the order count -- a position closed by something else "
                + "between the two passes is still closed, and claiming otherwise would send the "
                + "operator to place a duplicate exit by hand");
        }

        private static void TestP1_105_SubmittedButUnconfirmedIsItsOwnAnswer()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: 'submitted but not confirmed' is distinct from 'not submitted'");

            Assert(BridgeClosePlan.StatusFor(1, 1, 1) == "close_submitted_not_confirmed",
                "an order on the book with the position still open is a slow fill, not a failure");
            Assert(BridgeClosePlan.StatusFor(1, 1, 0) == "close_not_submitted",
                "no order on the book with the position still open is the failure");
            Assert(BridgeClosePlan.StatusFor(1, 1, 1) != BridgeClosePlan.StatusFor(1, 1, 0),
                "and the two are never the same string -- collapsing them loses the whole signal");
        }

        private static void TestP1_105_ASymbolPrefixIsNotAMatch()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: the symbol filter compares ROOTS, not prefixes");

            // The old filter was FullName.StartsWith(root), so `symbol: "M"` was a request to
            // close MNQ, MES, MCL and MGC together.
            Assert(!BridgeClosePlan.MatchesSymbol("MNQ 09-26", "M"),
                "'M' does not match MNQ -- a prefix test on a path that CLOSES POSITIONS is "
                + "unbounded by construction");
            Assert(!BridgeClosePlan.MatchesSymbol("MES 09-26", "ES"),
                "'ES' does not match MES");
            Assert(!BridgeClosePlan.MatchesSymbol("MNQ 09-26", "MN"),
                "and a partial root is not a root");
        }

        private static void TestP1_105_TheSymbolRootMatchesHoweverItWasSpelled()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: root matching accepts the request either way (NEGATIVE test)");

            Assert(BridgeClosePlan.MatchesSymbol("MNQ 09-26", "MNQ 09-26"),
                "a full contract name matches its own position");
            Assert(BridgeClosePlan.MatchesSymbol("MNQ 09-26", "MNQ"),
                "and so does the bare root -- both spellings are the same request");
            Assert(BridgeClosePlan.MatchesSymbol("MNQ 09-26", "mnq"),
                "case-insensitively");
            Assert(BridgeClosePlan.MatchesSymbol("MNQ 09-26", "  MNQ  "),
                "and whitespace in a JSON field has exactly one possible intent");
            Assert(BridgeClosePlan.MatchesSymbol("MNQ 12-26", "MNQ 09-26"),
                "⚠️ the EXPIRY is deliberately not compared -- unchanged from before the fix, and "
                + "recorded as a known limit in BridgeClosePlan's header rather than guessed at");
        }

        private static void TestP1_105_EverySymbolMeansEverySymbol()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: an omitted symbol and 'ALL' are the same request");

            Assert(BridgeClosePlan.WantsEverySymbol("ALL"), "'ALL' means every instrument");
            Assert(BridgeClosePlan.WantsEverySymbol("all"), "case-insensitively");
            Assert(BridgeClosePlan.WantsEverySymbol(" ALL "), "and trimmed");
            Assert(BridgeClosePlan.MatchesSymbol("MNQ 09-26", "ALL"), "so everything is in scope");
            Assert(!BridgeClosePlan.WantsEverySymbol("MNQ"),
                "but a named instrument is NOT every instrument -- this is the branch that makes "
                + "the filter mean anything");
            // ⚠️ These two started as the opposite assertion and the test won. Turning absence
            // into "ALL" is the HANDLER's job, in one greppable line; a blank string arriving
            // here is a caller bug, and reading it as a wildcard would liquidate the account.
            Assert(!BridgeClosePlan.WantsEverySymbol(null),
                "a null symbol is NOT a wildcard -- the handler defaults an absent field to 'ALL' "
                + "before this is reached, so a null here is a caller bug, not a request");
            Assert(!BridgeClosePlan.WantsEverySymbol("   "),
                "and neither is whitespace: {\"symbol\": \"   \"} is a template that interpolated "
                + "an empty variable, and the two failure directions are not symmetric -- matching "
                + "nothing wastes a call, matching everything is an unrequested liquidation");
        }

        private static void TestP1_105_AnUnnameableInstrumentIsOutOfScope()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: in doubt means OUT of scope on a closing path");

            Assert(!BridgeClosePlan.MatchesSymbol(null, "MNQ"),
                "an instrument with no name cannot be shown to be what the caller asked for");
            Assert(!BridgeClosePlan.MatchesSymbol("", "MNQ"), "nor an empty one");
            Assert(!BridgeClosePlan.MatchesSymbol("MNQ 09-26", "   "),
                "⚠️ and a whitespace-only symbol is not a wildcard here -- closing every position "
                + "because a field held a space is the wrong way to be wrong");
            Assert(!BridgeClosePlan.MatchesSymbol("MNQ 09-26", null),
                "nor is a null one");
            // ⚠️ Added because a mutant SURVIVED: dropping the empty-root guard leaves
            // string.Equals("", ""), so a nameless instrument matches a nameless request and
            // BOTH sides being unknown reads as a match. The handler passes a null FullName for
            // an order with no instrument, so this pair is reachable.
            Assert(!BridgeClosePlan.MatchesSymbol(null, "   "),
                "and two unknowns are NOT a match -- an unnamed instrument does not answer an "
                + "unnamed request just because the two empty strings are equal");
            Assert(!BridgeClosePlan.MatchesSymbol("", ""),
                "in either spelling");
            Assert(BridgeClosePlan.RootOf(null) == "",
                "RootOf never throws on a path that closes positions");
            Assert(BridgeClosePlan.RootOf("MNQ 09-26") == "MNQ", "and it takes the leading token");
            Assert(BridgeClosePlan.RootOf("AAPL") == "AAPL",
                "including when there is no expiry to strip");
        }

        private static void TestP1_105_TheAccountFilterIsExactOrEverything()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: the account filter");

            Assert(BridgeClosePlan.MatchesAccount("Sim101", "Sim101"), "an exact name matches");
            Assert(BridgeClosePlan.MatchesAccount("Sim101", "sim101"), "case-insensitively");
            Assert(BridgeClosePlan.MatchesAccount("Sim101", " Sim101 "), "and trimmed");
            Assert(!BridgeClosePlan.MatchesAccount("Sim101", "Sim1O1"),
                "a typo matches nothing -- ⚠️ which is why the HANDLER resolves the name through "
                + "BridgeAccountResolver first (P1-90): 'matched nothing' is a far worse answer "
                + "than 'there is no account called that'");
            Assert(BridgeClosePlan.MatchesAccount("Sim101", null),
                "an omitted account means every account, the handler's long-standing contract");
            Assert(!BridgeClosePlan.MatchesAccount(null, "Sim101"),
                "but a nameless account is not the named one");
        }

        private static void TestP1_105_ScopeIsBothHalvesAndNothingElse()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: InScope is exactly account AND symbol, over the whole matrix");

            // The acting pass and the observing pass call InScope. If it were not exactly the
            // conjunction, the report would be true about a set the caller never named.
            var accounts = new[] { "Sim101", "Sim102", null };
            var instruments = new[] { "MNQ 09-26", "ES 09-26", null };
            var reqAccounts = new[] { "Sim101", null, "Nope" };
            var reqSymbols = new[] { "MNQ", "ALL", null, "ZZZ" };

            int checkedPairs = 0;
            bool consistent = true;
            bool sawTrue = false, sawFalse = false;
            foreach (var a in accounts)
                foreach (var i in instruments)
                    foreach (var ra in reqAccounts)
                        foreach (var rs in reqSymbols)
                        {
                            bool expected = BridgeClosePlan.MatchesAccount(a, ra)
                                         && BridgeClosePlan.MatchesSymbol(i, rs);
                            bool actual = BridgeClosePlan.InScope(a, i, ra, rs);
                            if (expected != actual) consistent = false;
                            if (actual) sawTrue = true; else sawFalse = true;
                            checkedPairs++;
                        }

            Assert(checkedPairs == 108,
                string.Format("all 108 combinations were driven (drove {0})", checkedPairs));
            Assert(consistent, "InScope agrees with its two halves on every one of them");
            Assert(sawTrue && sawFalse,
                "and the matrix contains both answers -- a predicate that is constant would "
                + "satisfy 'consistent' with a matching constant on the other side");
        }

        private static void TestP1_105_TheEndpointObservesRatherThanClaiming()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: the close endpoint reports an observed position (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            Assert(!Regex.IsMatch(code, @"positionClosed\s*=\s*true"),
                "the unconditional assignment is GONE -- `positionClosed = true` on the line after "
                + "an asynchronous Flatten recorded that control reached that line");
            Assert(!Regex.IsMatch(code, @"status\s*=\s*""flattened"""),
                "and so is the constant status string, which was returned whatever happened");
            Assert(Regex.IsMatch(code, @"BridgeClosePlan\.PositionClosed"),
                "positionClosed is derived from the plan");
            Assert(Regex.IsMatch(code, @"BridgeClosePlan\.StatusFor"),
                "and so is the status");
            Assert(Regex.IsMatch(code, @"positionsStillOpen"),
                "the response carries a position read taken AFTER the flatten pass");
            // ⚠️ Added because a mutant SURVIVED: replacing the poll's exit condition with a bare
            // `break` leaves a single immediate read, and Flatten is ASYNCHRONOUS -- so every
            // HEALTHY close would report "submitted but not confirmed". An alarm that is always
            // on is off, and the mutant that does it is a one-word edit.
            Assert(Regex.IsMatch(code, @"if \(positionsStillOpen\.Count == 0\) break;"),
                "and the re-read is a poll that stops when the positions are actually flat, not a "
                + "single read taken before an asynchronous Flatten could possibly have landed");
            Assert(Regex.IsMatch(code, @"BridgeFlattenPlan\s*\.?\s*\n?\s*\.SubmittedByThisCall")
                   || Regex.IsMatch(code, @"BridgeFlattenPlan[\s\S]{0,40}SubmittedByThisCall"),
                "and it reuses P0-104's observation of what actually reached the book, rather than "
                + "growing a second dialect for the same question");
            Assert(!Regex.IsMatch(code, @"o\.Instrument\.FullName\.StartsWith\(rootSymbol"),
                "the prefix filter is gone -- that exact expression made `symbol: \"M\"` a request "
                + "to close MNQ, MES, MCL and MGC");
        }

        private static void TestP1_105_BothPassesUseTheSameScopePredicate()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-105: acting and observing share one scope predicate (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // ⚠️ Counted, not merely present. The whole point of the extraction is that the pass
            // that ACTS and the pass that OBSERVES cannot disagree about which positions the
            // request was about; one call site satisfies "the plan is used" while leaving the
            // other pass with a hand-rolled filter, which is this defect in a new place.
            int symbolCalls = Regex.Matches(code, @"BridgeClosePlan\.MatchesSymbol").Count;
            int accountCalls = Regex.Matches(code, @"BridgeClosePlan\.MatchesAccount").Count;
            Assert(symbolCalls >= 3,
                string.Format("the symbol predicate is used by the cancel pass, the flatten pass "
                    + "and the re-read (found {0})", symbolCalls));
            Assert(accountCalls >= 2,
                string.Format("and the account predicate by both the acting and observing loops "
                    + "(found {0})", accountCalls));

            Assert(Regex.IsMatch(code, @"ResolveOrRefuse\([\s\S]{0,120}""close a position"""),
                "a supplied account name is RESOLVED, not filtered on -- P1-90 at a seventh site, "
                + "where a typo used to match nothing and be reported as a successful close");
            // ⚠️ Added because a mutant SURVIVED. The assertion above only proved the resolver is
            // CALLED; neutering the refusal to `if (false)` left the call in place and the gate
            // still passed. A source gate that a value is computed is not a gate that it is
            // USED -- P2-24's class ("dead safety machinery is invisible") reaching the gates
            // themselves. Every "is X called" assertion in this file deserves the same question.
            Assert(Regex.IsMatch(code, @"closeResolution\.Refused\)\s*return new \{ error = closeResolution\.Error \}"),
                "and the refusal is RETURNED, not merely computed and dropped");

            Assert(!Regex.IsMatch(code, @"cancelledOrdersCount \+= toCancel\.Count"),
                "the cancel count credits what was SENT, not the length of the list it tried -- "
                + "the old expression reported every order as cancelled when the call threw");
        }

        // ================================================================================
        // P2-109. `nt_orders` advertised account/limit/offset and implemented NONE of them,
        // because the route was `case "/api/orders": return GetOrders();` -- taking no
        // parameters at all, between two routes that were already passing `query[...]`.
        // ================================================================================

        private static void TestP2_109_TheFilteredAndUNfilteredAnswersMustDIFFER()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: filtering by account changes the answer (the live measurement)");

            // The defect, measured 2026-08-14 20:10Z: nt_orders(account="Sim101") and nt_orders()
            // returned BYTE-IDENTICAL payloads, and the single order in them was on a FUNDED
            // TakeProfit account. Sim101 had no working orders, so the honest answer was empty.
            //
            // ⚠️ Note the shape of this assertion. "The filter returns a subset" PASSES UNDER THE
            // DEFECT -- every set is a subset of itself. The regression test has to be that the
            // two answers are DIFFERENT.
            var accountsWithOrders = new[] { "TAKEPROFITPRO524207503" };

            int unfiltered = 0, filteredToSim101 = 0;
            foreach (var acct in accountsWithOrders)
            {
                if (BridgeAccountScope.Matches(acct, null)) unfiltered++;
                if (BridgeAccountScope.Matches(acct, "Sim101")) filteredToSim101++;
            }

            Assert(unfiltered == 1, "unfiltered, the funded account's order is in scope");
            Assert(filteredToSim101 == 0,
                "and asking for Sim101 returns NOTHING -- not the funded account's order");
            Assert(unfiltered != filteredToSim101,
                "the two answers DIFFER. A 'the filter returns a subset' assertion would pass "
                + "under the defect, because every set is a subset of itself");
        }

        private static void TestP2_109_AnOmittedAccountStillMeansEveryAccount()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: an omitted account still means every account (NEGATIVE test)");

            // For a filter, the negative test is the one that proves it is a filter and not an
            // outage: a predicate that returned false for everything would satisfy the test above.
            Assert(BridgeAccountScope.Matches("Sim101", null), "a null request matches");
            Assert(BridgeAccountScope.Matches("TAKEPROFITPRO524207503", ""), "an empty one matches");
            Assert(BridgeAccountScope.Matches("Sim101", "   "), "and a blank one matches");
            Assert(BridgeAccountScope.Matches("Sim101", "Sim101"), "an exact name matches its own account");
            Assert(BridgeAccountScope.Matches("Sim101", "sim101"), "case-insensitively");
            Assert(BridgeAccountScope.Matches("Sim101", " Sim101 "), "and trimmed");
            Assert(!BridgeAccountScope.Matches("Sim101", "Sim1O1"),
                "but a typo matches nothing -- which is why the HANDLER resolves the name first "
                + "(P1-90): on a READ path, 'no orders' for a nonexistent account reads as "
                + "reassurance, and reassurance is the whole damage");
            Assert(!BridgeAccountScope.Matches(null, "Sim101"), "and a nameless account is not the named one");
        }

        private static void TestP2_109_TheCloseAndOrdersPathsShareONEDefinition()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: the close path and the orders read agree by construction");

            // BridgeClosePlan.MatchesAccount delegates. If someone re-inlines a copy, these drift
            // and only one of them gets the next fix -- which is P1-90 (six sites) and P1-100
            // (three readers of one flag) in miniature.
            var cases = new string[][]
            {
                new string[] { "Sim101", "Sim101" }, new string[] { "Sim101", "sim101" },
                new string[] { "Sim101", "Sim1O1" }, new string[] { "Sim101", null },
                new string[] { null, "Sim101" },     new string[] { null, null },
                new string[] { "Sim101", "   " },    new string[] { "  Sim101  ", "Sim101" },
            };
            bool allAgree = true;
            foreach (var c in cases)
                if (BridgeClosePlan.MatchesAccount(c[0], c[1]) != BridgeAccountScope.Matches(c[0], c[1]))
                    allAgree = false;

            Assert(allAgree, string.Format(
                "all {0} cases give the same answer through both entry points", cases.Length));
        }

        private static void TestP2_109_TheLimitIsParsedSafelyAndClamped()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: limit parsing -- absent and unparseable are different inputs");

            Assert(BridgeOrderQuery.ParseLimit(null) == BridgeOrderQuery.DefaultLimit, "absent gives the default");
            Assert(BridgeOrderQuery.ParseLimit("") == BridgeOrderQuery.DefaultLimit, "blank gives the default");
            Assert(BridgeOrderQuery.ParseLimit("   ") == BridgeOrderQuery.DefaultLimit, "whitespace gives the default");
            // This comment used to end "-- the route next door still throws a FormatException on
            // a caller typo", naming /api/bars. It did, it was measured doing it (HTTP 500 + a
            // stack trace on count=abc), and P3-111 fixed it hours later. Kept as history rather
            // than deleted: the prediction was written down before the measurement, and both
            // routes now go through the same BridgeQueryValue.ParseInt.
            Assert(BridgeOrderQuery.ParseLimit("abc") == BridgeOrderQuery.DefaultLimit,
                "and an UNPARSEABLE value gives the default rather than throwing");
            Assert(BridgeOrderQuery.ParseLimit("10") == 10, "a real value is honoured");
            Assert(BridgeOrderQuery.ParseLimit(" 20 ") == 20, "trimmed");
            Assert(BridgeOrderQuery.ParseLimit("0") == 1,
                "0 clamps to 1 -- an empty page and an empty book are indistinguishable to the "
                + "reader, and that confusion IS this defect");
            Assert(BridgeOrderQuery.ParseLimit("-5") == 1, "and so does a negative");
            Assert(BridgeOrderQuery.ParseLimit("999999999") == BridgeOrderQuery.MaxLimit,
                "a huge value clamps to MaxLimit -- this bounds a response this process builds "
                + "in memory, not just the caller's convenience");
        }

        private static void TestP2_109_TheOffsetIsParsedSafely()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: offset parsing");

            Assert(BridgeOrderQuery.ParseOffset(null) == 0, "absent is 0");
            Assert(BridgeOrderQuery.ParseOffset("abc") == 0, "unparseable is 0, not an exception");
            Assert(BridgeOrderQuery.ParseOffset("7") == 7, "a real value is honoured");
            Assert(BridgeOrderQuery.ParseOffset("-1") == 0,
                "and a NEGATIVE offset is 0, never an index from the end -- Python's semantics "
                + "here would silently return the last page while looking like a caller error");
        }

        private static void TestP2_109_ThePageArithmetic()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: page size and hasMore are derived from the same three numbers");

            Assert(BridgeOrderQuery.PageSize(0, 50, 0) == 0, "an empty book gives an empty page");
            Assert(BridgeOrderQuery.PageSize(10, 50, 0) == 10, "a limit above the total returns everything");
            Assert(BridgeOrderQuery.PageSize(10, 3, 0) == 3, "a full page is the limit");
            Assert(BridgeOrderQuery.PageSize(10, 3, 9) == 1, "the last page is the remainder, not the limit");
            Assert(BridgeOrderQuery.PageSize(10, 3, 10) == 0, "an offset AT the end is an empty page");
            Assert(BridgeOrderQuery.PageSize(10, 3, 25) == 0,
                "and an offset past the end is an empty page -- not an error, and not wrapped");

            Assert(BridgeOrderQuery.HasMore(10, 3, 0), "more remains after a full first page");
            Assert(!BridgeOrderQuery.HasMore(10, 50, 0), "nothing remains when everything fit");
            Assert(!BridgeOrderQuery.HasMore(10, 3, 9), "nothing remains after the last partial page");
            Assert(!BridgeOrderQuery.HasMore(0, 50, 0), "and an empty book never claims more");
        }

        private static void TestP2_109_TheRouteActuallyPassesTheQueryThrough()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-109: the route passes the query to the handler (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // ⚠️ THE WHOLE DEFECT WAS ONE LINE: `case "/api/orders": return GetOrders();`. Every
            // other layer was correct -- the schema advertised the parameters, the MCP wrapper
            // built the query string and sent them, and the handler was a clean read. Nothing was
            // wrong with any component; the contract between them was never connected.
            Assert(!Regex.IsMatch(code, @"return GetOrders\(\s*\)"),
                "the no-argument call is GONE -- that single line discarded all three parameters");
            Assert(Regex.IsMatch(code, @"GetOrders\(\s*query\[""account""\]"),
                "and the route passes the account through, as the routes either side of it "
                + "already did");
            Assert(Regex.IsMatch(code, @"GetOrders\([^)]*query\[""limit""\][^)]*query\[""offset""\]"),
                "along with limit and offset, which the tool description has always promised as "
                + "'cursor pagination'");

            Assert(Regex.IsMatch(code, @"ResolveOrRefuse\([\s\S]{0,140}""list orders"""),
                "a supplied account name is RESOLVED, not ignored -- P1-90 on a read path, where "
                + "answering 'no orders' about an account that does not exist reads as reassurance");
            Assert(Regex.IsMatch(code, @"BridgeAccountScope\.Matches\(account\.Name, requestedAccount\)"),
                "and the orders loop filters through the SHARED predicate, not a second copy");
            Assert(Regex.IsMatch(code, @"BridgeOrderQuery\.PageSize"),
                "the page is sliced by the tested arithmetic rather than an inline expression");
        }

        private static void TestEveryResolverSiteACTSOnTheRefusal()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] every ResolveOrRefuse site RETURNS on a refusal, not just computes one");

            // ⚠️ WHY THIS IS A SWEEP AND NOT ANOTHER PER-SITE ASSERTION.
            //
            // P1-105 shipped a gate asserting `ResolveOrRefuse(... "close a position")` appears in
            // the source. A mutant neutered `if (closeResolution.Refused)` to `if (false)`, left
            // the call in place, and THE GATE STILL PASSED. A gate that a value is COMPUTED is not
            // a gate that it is USED.
            //
            // Hours later, writing P2-109's GetOrders, I wrote the identical incomplete gate and
            // its battery caught the identical survivor. That is this repo's own "second reader"
            // pattern (P1-100, P1-105) with me as the second reader: the lesson was learned at one
            // site and not carried to the next one written.
            //
            // So the check is derived from the source rather than enumerated: find EVERY
            // `x = BridgeAccountResolver.ResolveOrRefuse(...)` and require that same `x` to be
            // tested for `.Refused` and RETURNED on. A ninth site added tomorrow is covered the
            // moment it is written, without anyone remembering this exists.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            var assigned = Regex.Matches(code, @"(\w+)\s*=\s*BridgeAccountResolver\.ResolveOrRefuse\s*\(");
            Assert(assigned.Count >= 8, string.Format(
                "every resolver call assigns its result to a named variable (found {0} of the 8 "
                + "known sites). A call whose result is not even stored cannot be acted on", assigned.Count));

            var unused = new List<string>();
            var seen = new HashSet<string>();
            foreach (Match m in assigned)
            {
                var name = m.Groups[1].Value;
                if (!seen.Add(name)) continue;
                // The refusal must be tested AND must leave the method. `if (x.Refused) { }` and
                // a bare `x.Refused;` both compute it and neither refuses anything.
                var acts = Regex.IsMatch(code,
                    Regex.Escape(name) + @"\.Refused\s*\)\s*(?:\{\s*)?return\b");
                if (!acts) unused.Add(name);
            }

            Assert(unused.Count == 0, string.Format(
                "and every one of them RETURNS on the refusal (offenders: {0}). Neutering the "
                + "`if` to `if (false)` is a one-word edit that leaves the call, and the whole "
                + "point of P1-90 is the refusal, not the computation",
                unused.Count == 0 ? "none" : string.Join(", ", unused.ToArray())));
        }

        // ================================================================================
        // P3-111. `/api/bars` was filed as one line -- "int.Parse(query["count"] ?? "100"):
        // absent is handled, unparseable throws". Probing the live box first found FOUR defects
        // on one endpoint, and the parse crash was the least of them.
        //
        // Everything below is EXECUTED: BridgeBarsQuery and BridgeQueryValue name no NT8 type
        // (P2-27), so this project compiles and runs them.
        // ================================================================================

        private static void TestP3_111_TheFiledDefectExactly()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: count=abc is a caller typo, not an HTTP 500");

            // Measured on the live box: GET /api/bars?symbol=MNQ 09-26&count=abc returned an
            // HTTP 500 carrying a .NET stack trace. The `?? "100"` handled the parameter being
            // ABSENT; nothing handled it being PRESENT AND UNPARSEABLE. Different inputs.
            Assert(BridgeBarsQuery.ParseCount("abc") == BridgeBarsQuery.DefaultCount,
                "an unparseable count gives the default rather than throwing");
            Assert(BridgeBarsQuery.ParsePeriodValue("xyz") == BridgeBarsQuery.DefaultPeriodValue,
                "and so does an unparseable periodValue -- the SECOND of the three crashes");
            Assert(BridgeBarsQuery.ParseOffset("!!") == 0, "and an unparseable offset");

            Assert(BridgeBarsQuery.ParseCount(null) == BridgeBarsQuery.DefaultCount, "absent gives the default");
            Assert(BridgeBarsQuery.ParseCount("") == BridgeBarsQuery.DefaultCount, "blank gives the default");
            Assert(BridgeBarsQuery.ParseCount("   ") == BridgeBarsQuery.DefaultCount, "whitespace gives the default");
            Assert(BridgeBarsQuery.ParseCount("250") == 250, "and a real value is honoured");
            Assert(BridgeBarsQuery.ParseCount(" 250 ") == 250, "trimmed");
        }

        private static void TestP3_111_TheAdvertisedCapIsActuallyEnforced()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: the schema promised max 5,000 and the addon enforced nothing");

            // Measured: count=200000 returned 21,285,727 bytes and count=1000000 returned a
            // million bars, while the MCP tool schema advertised "max 5,000 rows" in TWO places.
            // Advertised-and-not-implemented is P1-72's shape, here on a size promise.
            Assert(BridgeBarsQuery.MaxCount == 5000,
                "the cap is the number the schema already advertised -- raising the code to meet "
                + "an existing written promise, not lowering the promise to meet the code");
            Assert(BridgeBarsQuery.ParseCount("200000") == BridgeBarsQuery.MaxCount,
                "the 21MB response clamps to the cap");
            Assert(BridgeBarsQuery.ParseCount("1000000") == BridgeBarsQuery.MaxCount, "and so does a million");
            Assert(BridgeBarsQuery.ParseCount("5000") == 5000, "exactly the cap is allowed through");

            // The lower end is the SILENT half and the more expensive lie: count=0 measured 0
            // bars, which reads as "this instrument has no data" rather than as a clamp.
            Assert(BridgeBarsQuery.ParseCount("0") == 1,
                "count=0 clamps to 1 -- an empty result on a read path reads as a fact about the "
                + "market, and that one is not true");
            Assert(BridgeBarsQuery.ParseCount("-5") == 1, "and so does a negative");

            Assert(BridgeBarsQuery.ParsePeriodValue("0") == 1, "periodValue is clamped at 1 too");
            Assert(BridgeBarsQuery.ParsePeriodValue("99999999") == BridgeBarsQuery.MaxPeriodValue,
                "and bounded above, so nonsense cannot reach NT8's BarsPeriod");
        }

        private static void TestP3_111_AnUnknownPeriodIsRefusedAndNotGUESSED()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: period=Banana -- Enum.Parse is int.Parse for names");

            var valid = new[] { "Minute", "Day", "Tick", "Volume", "Range", "Second", "Week", "Month" };
            string refusal;

            Assert(BridgeBarsQuery.ResolvePeriod(null, valid, out refusal) == BridgeBarsQuery.DefaultPeriod
                   && refusal == null, "absent gives the default with no refusal");
            Assert(BridgeBarsQuery.ResolvePeriod("  ", valid, out refusal) == BridgeBarsQuery.DefaultPeriod,
                "and so does blank");
            Assert(BridgeBarsQuery.ResolvePeriod("Day", valid, out refusal) == "Day" && refusal == null,
                "a known name passes through");
            Assert(BridgeBarsQuery.ResolvePeriod("day", valid, out refusal) == "day",
                "case-insensitively, as Enum.Parse was");
            Assert(BridgeBarsQuery.ResolvePeriod(" Tick ", valid, out refusal) == "Tick", "trimmed");

            // ⚠️ The refusal is the point. Coercing Banana to Minute would answer a question the
            // caller did not ask, with bars they would then reason over -- the guessing that
            // P1-90 exists to forbid, on a read path.
            var resolved = BridgeBarsQuery.ResolvePeriod("Banana", valid, out refusal);
            Assert(resolved == null, "an unknown name resolves to NOTHING rather than to Minute");
            Assert(refusal != null && refusal.Contains("Banana"),
                "the refusal quotes what the caller actually sent");
            Assert(refusal != null && refusal.Contains("Minute") && refusal.Contains("Week"),
                "and LISTS what would have worked -- a refusal that does not is another round "
                + "trip, and a stack trace says nothing at all");

            // Every period name the WRAPPER used to hard-code must survive, or removing its enum
            // would have traded one drift for a regression.
            foreach (var p in new[] { "Minute", "Day", "Tick", "Volume", "Range" })
                Assert(BridgeBarsQuery.ResolvePeriod(p, valid, out refusal) == p,
                    string.Format("the wrapper's old enum value '{0}' still resolves", p));
        }

        private static void TestP3_111_BarsArePagedFromTheRIGHTEdge()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: offset was advertised, sent, and dropped -- P2-109 at a second endpoint");

            int start, take;

            // offset=0: the most recent `count` bars. What every caller means by "the last 100".
            Assert(BridgeQueryValue.BarWindow(1000, 100, 0, out start, out take) && start == 900 && take == 100,
                "offset=0 takes the newest 100 of 1000 -- bars 900..999, not 0..99");

            // ⚠️ THE P2-109-SHAPED ASSERTION. Measured on the live box: offset=0 and offset=500
            // returned BYTE-IDENTICAL payloads. "The offset returns some bars" passes under that
            // defect. The two answers must DIFFER.
            int s0, t0, s5, t5;
            BridgeQueryValue.BarWindow(1000, 100, 0, out s0, out t0);
            BridgeQueryValue.BarWindow(1000, 100, 500, out s5, out t5);
            Assert(s0 != s5,
                "and a page at offset=500 is a DIFFERENT window from offset=0 -- the defect was "
                + "measured returning byte-identical payloads, which any 'returns a subset' "
                + "assertion passes under");
            Assert(s5 == 400 && t5 == 100, "specifically the 100 bars before the newest 500");

            // Contiguity: page 0 and page 1 must abut, or paging silently skips or repeats bars.
            int sA, tA, sB, tB;
            BridgeQueryValue.BarWindow(1000, 100, 0, out sA, out tA);
            BridgeQueryValue.BarWindow(1000, 100, 100, out sB, out tB);
            Assert(sB + tB == sA,
                "consecutive pages abut exactly -- a gap loses bars and an overlap double-counts "
                + "them, and neither is visible to whoever reads the series");
        }

        private static void TestP3_111_PagingPastTheStartIsEmptyNotWrapped()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: the end of history terminates the pager");

            int start, take;
            Assert(!BridgeQueryValue.BarWindow(100, 50, 100, out start, out take) && take == 0,
                "an offset AT the start of the series is an empty page");
            Assert(!BridgeQueryValue.BarWindow(100, 50, 500, out start, out take) && take == 0,
                "and past it -- not a wrapped-around full page, which would make an agent page "
                + "forever believing it was still reading new bars");
            Assert(!BridgeQueryValue.BarWindow(0, 50, 0, out start, out take), "no bars at all is empty");
            Assert(!BridgeQueryValue.BarWindow(100, 0, 0, out start, out take), "and a zero count is empty");

            // Fewer bars exist than were asked for: give what there is, starting at 0.
            Assert(BridgeQueryValue.BarWindow(30, 100, 0, out start, out take) && start == 0 && take == 30,
                "asking for more bars than exist returns all of them rather than throwing");
            Assert(BridgeQueryValue.BarWindow(100, 50, 80, out start, out take) && start == 0 && take == 20,
                "and a page straddling the start of history is truncated to what exists");
            Assert(!BridgeQueryValue.BarWindow(100, 50, -1, out start, out take) || start == 50,
                "a negative offset is 0, never an index from the end");
        }

        private static void TestP3_111_TheREQUESTGrowsWithTheOffsetButTheRESPONSEDoesNot()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: the cap bounds the response, not what is knowable");

            // A page at offset needs everything newer than it FETCHED, because the series is
            // windowed from its right edge. If the request were capped at MaxCount too, offset
            // would silently do nothing past the first page -- the defect restored.
            Assert(BridgeBarsQuery.RequestSize(100, 0) == 100, "the first page asks for exactly a page");
            Assert(BridgeBarsQuery.RequestSize(100, 900) == 1000,
                "and a page 900 bars back asks NT8 for 1000, or offset could never reach it");
            Assert(BridgeBarsQuery.RequestSize(5000, 50000) == 55000,
                "so the 5,000 cap bounds one RESPONSE and not the reachable history");

            Assert(BridgeBarsQuery.RequestSize(100, int.MaxValue) == int.MaxValue,
                "and an absurd offset saturates rather than OVERFLOWING to a negative request "
                + "size, which would be this ticket's defect wearing a new hat");
            Assert(BridgeBarsQuery.RequestSize(0, 0) == 1, "a zero count still asks for at least one bar");
            Assert(BridgeBarsQuery.RequestSize(10, -5) == 10, "and a negative offset contributes nothing");
        }

        private static void TestP3_111_TheOrdersAndBarsPathsShareONEDefinition()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: one definition of 'parse a query parameter safely'");

            // Both endpoints route through BridgeQueryValue.ParseInt. Asserted BEHAVIOURALLY --
            // identical rules must produce identical answers -- rather than by grepping for a
            // delegation, because a source gate that a value is COMPUTED is not a gate that it
            // is USED (the survivor that got through twice in the P1-105 and P2-109 batteries).
            Assert(BridgeOrderQuery.ParseLimit("abc") == BridgeQueryValue.ParseInt("abc", BridgeOrderQuery.DefaultLimit, 1, BridgeOrderQuery.MaxLimit),
                "the order limit is the shared arithmetic bound to the order endpoint's numbers");
            Assert(BridgeBarsQuery.ParseCount("abc") == BridgeQueryValue.ParseInt("abc", BridgeBarsQuery.DefaultCount, 1, BridgeBarsQuery.MaxCount),
                "and the bar count is the same arithmetic bound to different numbers");
            Assert(BridgeOrderQuery.ParseOffset("-3") == BridgeBarsQuery.ParseOffset("-3"),
                "the two offsets agree, because there is only one of them");
            Assert(BridgeOrderQuery.PageSize(10, 5, 3) == BridgeQueryValue.PageSize(10, 5, 3),
                "and so does the page arithmetic");

            // The guard on the shared helper's own contract: an inside-out range is a bug at the
            // CALL site, and inventing an answer for it would hide the caller's mistake.
            bool threw = false;
            try { BridgeQueryValue.ParseInt("5", 1, 100, 10); }
            catch (ArgumentException) { threw = true; }
            Assert(threw, "an inside-out [min, max] is refused rather than silently resolved");
        }

        private static void TestP3_111_TheBarsRouteHandsTheRawStringsThrough()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: the shipped defect at the seam (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // The defect verbatim: the route parsed at the seam, so an unparseable value threw
            // before any handler could decide what to do about it.
            Assert(!Regex.IsMatch(code, @"int\.Parse\(query\["),
                "no route calls int.Parse on a query value -- absent and unparseable are "
                + "different inputs and int.Parse only distinguishes one of them");

            var route = Regex.Match(code,
                @"case ""/api/bars"":\s*return\s+GetBars\(([^;]*)\);", RegexOptions.Singleline);
            Assert(route.Success, "the /api/bars route is locatable");
            if (route.Success)
            {
                var args = route.Groups[1].Value;
                foreach (var p in new[] { "symbol", "period", "periodValue", "count", "offset" })
                    Assert(args.Contains("query[\"" + p + "\"]"), string.Format(
                        "the route passes query[\"{0}\"] through to the handler", p));
            }

            // The handler must take strings. An int parameter means SOMEONE parsed it earlier,
            // and the only place earlier is the seam this ticket is about.
            var sig = Regex.Match(code, @"private object GetBars\(([^)]*)\)");
            Assert(sig.Success && !Regex.IsMatch(sig.Groups[1].Value, @"\bint\b"),
                "and GetBars takes no int, so nothing can have parsed before it");
        }

        private static void TestP3_111_NoBarsPathParsesAPeriodWithoutResolvingItFirst()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: BOTH readers of the period parameter, not just the filed one");

            // ⚠️ /api/bars and /api/bars/export take the SAME `period` string and BOTH threw on
            // the same typo. Fixing only the filed one is the pattern that produced P1-100,
            // P2-98/P1-99 and P1-105 -- a second reader that was never told. So this is derived:
            // every Enum.Parse of a BarsPeriodType must operate on a name that ResolvePeriod
            // already vetted, and a third site added tomorrow is covered when it is written.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            var parses = Regex.Matches(code, @"Enum\.Parse\(typeof\(BarsPeriodType\),\s*(\w+)\s*,");
            Assert(parses.Count >= 2, string.Format(
                "both BarsPeriodType parse sites are found (found {0})", parses.Count));

            var unvetted = new List<string>();
            foreach (Match m in parses)
            {
                var arg = m.Groups[1].Value;
                // The variable must be the RESULT of a ResolvePeriod call, and that result must
                // have been checked for null and returned on -- computing it is not using it.
                var vetted = Regex.IsMatch(code,
                        @"var\s+" + Regex.Escape(arg) + @"\s*=\s*BridgeBarsQuery\.ResolvePeriod\b")
                    && Regex.IsMatch(code,
                        Regex.Escape(arg) + @"\s*==\s*null\s*\)\s*return\b");
                if (!vetted) unvetted.Add(arg);
            }
            Assert(unvetted.Count == 0, string.Format(
                "and every one parses a RESOLVED name, refusing before it gets there (offenders: {0})",
                unvetted.Count == 0 ? "none" : string.Join(", ", unvetted.ToArray())));

            Assert(Regex.Matches(code, @"BridgeBarsQuery\.ResolvePeriod\b").Count == 2,
                "exactly two sites resolve a period -- an exact count, because a `>=` gate is a "
                + "slow leak that a seventh site silently weakens");
        }

        private static void TestP3_111_HasMoreIsNotStartGreaterThanZero()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P3-111: the pager's termination signal");

            // ⚠️ This was nearly shipped as `hasMore = start > 0`, which is wrong in the ordinary
            // case: when NT8 returns exactly what was asked for, start is 0 and older history
            // still exists, so an agent stops one page early believing it read everything.
            // Silent truncation -- the same family of lie as this ticket's silent widening.
            // What is knowable is whether the fetch was HISTORY-LIMITED.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // ⚠️ Read EVERY assignment, not the first. Written as Regex.Match this failed on its
            // own first run, because the first `hasMore` in the file is the empty-window branch's
            // constant `false` -- correct there, and nothing to do with the arithmetic being
            // gated. A gate that inspects a region it did not state is the failure mode four of
            // this project's own checks have already been caught in.
            var assignments = Regex.Matches(code, @"hasMore\s*=\s*([^,]+),")
                .Cast<Match>().Select(x => x.Groups[1].Value.Trim()).ToList();
            Assert(assignments.Count == 2, string.Format(
                "both hasMore assignments are found -- the empty-window branch and the real one "
                + "(found {0})", assignments.Count));

            foreach (var expr in assignments)
                Assert(!Regex.IsMatch(expr, @"\bstart\s*>\s*0"), string.Format(
                    "no branch computes it as `start > 0`, which reports 'no more' whenever the "
                    + "request was exactly filled (found: {0})", expr));

            var derived = assignments.Where(e => e != "false").ToList();
            Assert(derived.Count == 1, "exactly one branch derives it rather than stating it");
            Assert(derived.Count == 1 && derived[0].Contains("available") && derived[0].Contains("requestSize"),
                string.Format("and it compares what NT8 returned against what was asked for "
                    + "(found: {0})", derived.Count == 1 ? derived[0] : "n/a"));
            Assert(assignments.Contains("false"),
                "while the empty-window branch states it, because there is nothing to derive from");

            // The arithmetic it rests on, executed: a full request means more may exist, a short
            // one means the series ran out.
            Assert(BridgeBarsQuery.RequestSize(100, 0) == 100,
                "so `available >= requestSize` is a real test -- 1000 available against a 100 "
                + "request means history remains");
        }

        // ================================================================================
        // P1-102. The lockout surface: a route with no tool, and a handler that answered
        // success to anything you sent it.
        // ================================================================================

        private static void TestP1_102_AnUnknownLockoutActionIsREFUSEDNotAnsweredAsAStatus()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-102: `action: \"lock\"` used to return success:true, isLockedOut:false");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // The whitelist must exist as a single named array -- it is what the MCP tool's enum
            // is pinned to (P1-72's remedy, after that enum drifted twice). A whitelist inlined
            // into an `if` cannot be extracted by the wrapper's test.
            var arr = Regex.Match(code, @"LockoutActions\s*=\s*\{([^}]*)\}");
            Assert(arr.Success, "the accepted actions are a single named array the wrapper can pin to");

            var actions = Regex.Matches(arr.Groups[1].Value, "\"([^\"]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert(actions.Contains("status") && actions.Contains("unlock"),
                "it names the read and the clear");
            Assert(!actions.Contains("lock"),
                "and it does NOT name 'lock' -- nothing implements it, and advertising an action "
                + "the receiver refuses is P1-72 itself");

            // ⚠️ THE SHIPPED DEFECT: the handler ended with an unconditional status read, so every
            // unrecognised string fell through to `{ success = true, ... isLockedOut = false }`.
            // `action: "lock"` -- the most obvious thing a caller would send -- was answered
            // "I locked it, and it is not locked", with success:true. P1-88 is this exact shape
            // in the copier; F-9 is the general form.
            var handler = Regex.Match(code,
                @"private object HandleLockout\(string body\)\s*\{(.*?)\n        \}",
                RegexOptions.Singleline);
            Assert(handler.Success, "the lockout handler is locatable");
            if (handler.Success)
            {
                var body = handler.Groups[1].Value;
                Assert(body.Contains("UNKNOWN_LOCKOUT_ACTION"),
                    "an unrecognised action is REFUSED by name, not answered as a status read");
                Assert(Regex.IsMatch(body, @"success\s*=\s*false"),
                    "and the refusal says success = false -- a refusal reported as success is "
                    + "the defect, not the fix");

                // ⚠️ REPORT THE OUTCOME, NOT THE CALL. The unlock branch returned a hard-coded
                // `isLockedOut = false`: a claim the unlock worked, made without asking. That is
                // P1-105 (`positionClosed = true` after an async Flatten) and P0-104 (success on
                // a flatten it had cancelled) at a third site.
                Assert(!Regex.IsMatch(body, @"isLockedOut\s*=\s*false\s*\}"),
                    "the unlock branch does not hard-code isLockedOut = false");
                Assert(Regex.IsMatch(body, @"stillLocked\s*=\s*IsAccountLocked"),
                    "it RE-READS the enforcer after unlocking and reports what it found");
            }
        }

        // ================================================================================
        // P2-127, section 4 of the UI redesign design: the FLEET pane's tree and the ONE
        // ordering it is sorted by. EXECUTED, not grepped -- BridgeFleetView names no NT8
        // type. See addons/BridgeFleetView.cs for why it is a compiled class at all: the
        // page it feeds, ui/index.html, is in no test build and no mutation battery, and
        // P2-127's plan entry says that is the thing to fix FIRST.
        //
        // The hazard being defended is measured, from one live payload: a copier row's
        // `severity` runs 0-is-WORST while the `system` cell's runs Ok=0..Critical=3, which
        // is 0-is-BEST. Sorting a tree that merges them by "the severity number" is wrong
        // for one of the two, silently, and reads as a plausible order either way.
        // ================================================================================

        private static FleetCopierRow Row(string leader, string follower, string group, int severity)
        {
            return new FleetCopierRow
            {
                LeaderAccountName = leader,
                FollowerAccountName = follower,
                GroupName = group,
                Severity = severity,
                Enforcing = false,
                NotEnforcingLabel = "disabled"
            };
        }

        private static FleetNode Named(List<FleetNode> nodes, string name)
        {
            if (nodes == null) return null;
            foreach (var n in nodes) if (n != null && n.Name == name) return n;
            return null;
        }

        private static FleetNode At(List<FleetNode> nodes, int i)
        {
            if (nodes == null || i < 0 || i >= nodes.Count) return null;
            return nodes[i];
        }

        private static void TestP2127_TheTwoIncomingSeverityScalesAreConvertedNotShared()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the two incoming severity scales are converted, not shared");

            Assert(BridgeFleetView.RankOfCopierRow(0) == 0
                   && BridgeFleetView.RankOfCopierRow(5) == 5,
                "P2-127: a copier row's severity keeps 0 as the worst");

            // Fail-closed: a value outside the known range is the WORST, not the best. A
            // clamp toward "healthy" is how an unreadable state reads as fine.
            Assert(BridgeFleetView.RankOfCopierRow(-3) == BridgeFleetView.UnknownRank
                   && BridgeFleetView.RankOfCopierRow(99) == BridgeFleetView.UnknownRank,
                "P2-127: an out-of-range copier severity is the worst rank, not a sorted one");

            Assert(BridgeFleetView.RankOfSystemSeverity("critical") == BridgeFleetView.WorstRank
                   && BridgeFleetView.RankOfSystemSeverity("ok") > BridgeFleetView.RankOfSystemSeverity("warn"),
                "P2-127: the system severity scale is INVERTED on the way in, not shared");

            Assert(BridgeFleetView.RankOfSystemSeverity("banana") == BridgeFleetView.UnknownRank
                   && BridgeFleetView.RankOfSystemSeverity(null) == BridgeFleetView.UnknownRank,
                "P2-127: an unrecognised system severity is the WORST, not the best");

            // THE DISCRIMINATOR. Both scales carry a 3. On the system scale that is
            // `Critical`, the worst thing it can say; on a copier row it is `Shadow`, which
            // is not a fault at all. A single shared scale passes every other assertion here
            // and fails this one.
            Assert(BridgeFleetView.RankOfSystemSeverity("critical") == BridgeFleetView.WorstRank
                   && BridgeFleetView.RankOfCopierRow(3) != BridgeFleetView.WorstRank,
                "P2-127: the same number means opposite things on the two scales");
        }

        private static void TestP2127_TheWorstOfASetIsTheSmallestAndEmptyIsNotHealthy()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the worst of a set, and what an empty set means");

            Assert(BridgeFleetView.WorstOf(new List<int> { 5, 1, 4 }) == 1,
                "P2-127: the worst of a set is the SMALLEST rank");

            Assert(BridgeFleetView.WorstOf(new List<int>()) == BridgeFleetView.UnknownRank,
                "P2-127: a group with no children is not healthy");
        }

        private static void TestP2127_TheTreeGroupsByGroupThenByLeader()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: how the tree is grouped");

            // The live box: two ungrouped relationships sharing one leader. Section 4
            // decision 2 -- groups are the only grouping, a 1:1 pair is a group of one --
            // so these key by LEADER and become one node, not two.
            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5), Row("Sim101", "Sim-ORB", null, 5) },
                new List<string> { "Sim101", "SimCopy2", "Sim-ORB" });
            var g = Named(tree, "Sim101");
            Assert(g != null && g.Kind == "group" && g.Children.Count == 2,
                "P2-127: two ungrouped relationships with one leader are ONE group of two");

            var named = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("NT_9451", "follower_1", "Group A", 4) },
                new List<string> { "NT_9451", "follower_1" });
            var ga = Named(named, "Group A");
            Assert(ga != null && ga.Kind == "group" && ga.Children.Count == 1,
                "P2-127: a named group is keyed by its name, not by its leader");
        }

        private static void TestP2127_EveryAccountAppearsExactlyOnce()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: every account appears exactly once");

            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5) },
                new List<string> { "Sim101", "SimCopy2", "Spare1", "Spare2" });

            var unlinked = Named(tree, BridgeFleetView.UnlinkedName);
            Assert(unlinked != null && unlinked.Children.Count == 2
                   && Named(unlinked.Children, "Spare1") != null
                   && Named(unlinked.Children, "Spare2") != null,
                "P2-127: every account in no relationship lands under Unlinked accounts");

            // A leader is its group's leader and is NOT also unlinked. Collecting every name
            // in the whole tree is the assertion that catches a double-listing anywhere.
            var seen = new List<string>();
            foreach (var n in tree)
            {
                if (n.Kind == "group") seen.Add(n.Name);
                foreach (var c in n.Children) seen.Add(c.Name);
            }
            var dupes = seen.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            Assert(seen.Count > 0 && dupes.Count == 0,
                "P2-127: no account is listed in two places in the tree");
        }

        private static void TestP2127_EverythingSortsWorstFirstAndNothingHidesUnderAParent()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: worst-first, and a parent carries its worst child");

            // Group "Bad" holds an Orphan (0, the worst row this system emits); group "Good"
            // holds only Idle rows (5).
            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow>
                {
                    Row("Good", "g1", "Good", 5),
                    Row("Bad",  "b1", "Bad",  5),
                    Row("Bad",  "b2", "Bad",  0)
                },
                new List<string> { "Good", "g1", "Bad", "b1", "b2", "Spare1" });

            Assert(At(tree, 0) != null && At(tree, 0).Name == "Bad",
                "P2-127: groups sort worst-first");

            var bad = Named(tree, "Bad");
            Assert(bad != null && At(bad.Children, 0) != null && At(bad.Children, 0).Name == "b2",
                "P2-127: followers inside a group sort worst-first");

            // Section 4.2 killed navigation tabs because this page's value is that a bad
            // state is visible WITHOUT being looked for. A collapsed parent showing a
            // healthy rank over a bad child is that hazard by another route.
            Assert(bad != null && bad.Rank == BridgeFleetView.WorstRank,
                "P2-127: a group carries the WORST of its children, so nothing hides under it");

            // The sketch in section 4 puts Unlinked below the groups. It stays there even
            // when it holds the worst thing on the page -- so its own rank has to be visible
            // on the node, because its POSITION no longer tells you.
            var withBadUnlinked = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Good", "g1", "Good", 5) },
                new List<string> { "Good", "g1", "Spare1" });
            var last = At(withBadUnlinked, withBadUnlinked.Count - 1);
            Assert(last != null && last.Name == BridgeFleetView.UnlinkedName,
                "P2-127: Unlinked accounts comes last, so its rank must be read off the node");
        }

        // -------------------------------------------------------------------------------
        // P2-127, the three decisions arbitrated BY HAND after the loop returned
        // NOT_CONVERGING. Two came from findings the panel raised, one of which the arbiter
        // had REJECTED as "stable and correct"; the third is a design call the ticket was
        // silent on, which is how the model came to make it by default.
        // -------------------------------------------------------------------------------

        private static FleetCopierRow RowOn(string leader, string follower, string group,
                                            int severity, string instrument)
        {
            var r = Row(leader, follower, group, severity);
            r.InstrumentFullName = instrument;
            return r;
        }

        private static void TestP2127_OneNodePerFollowerNotOnePerRow()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: one node per follower, not one per row");

            // A leader and a follower may hold more than one relationship, one per instrument.
            // Both live rows are instrument-less, so a tree built per ROW passes every test
            // written against the box as it stands today.
            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow>
                {
                    RowOn("Sim101", "SimCopy2", null, 5, "MNQ 09-26"),
                    RowOn("Sim101", "SimCopy2", null, 0, "MES 09-26")
                },
                new List<string> { "Sim101", "SimCopy2" });

            var g = Named(tree, "Sim101");
            Assert(g != null && g.Children.Count == 1,
                "P2-127: two relationships with the same follower are ONE row in the tree");

            // And it keeps the WORST of them -- the same argument as a group keeping its worst
            // child. Displaying the reassuring one is the whole failure mode.
            Assert(g != null && At(g.Children, 0) != null
                   && At(g.Children, 0).Rank == BridgeFleetView.WorstRank,
                "P2-127: a follower on two relationships keeps its WORST rank");
        }

        private static void TestP2127_TheOrderIsTOTALSoTheSameDataGivesTheSameList()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the ordering is total, not merely by rank");

            // List<T>.Sort is documented UNSTABLE and `groups` is a Dictionary, whose
            // enumeration order is unspecified. Equal ranks are the NORMAL case here -- all 95
            // unlinked accounts on the live box tie -- so without a name tie-break the page
            // re-orders itself between refreshes that saw identical data.
            var rows = new List<FleetCopierRow>
            {
                Row("gamma", "g1", "gamma", 5),
                Row("alpha", "a1", "alpha", 5),
                Row("beta",  "b1", "beta",  5)
            };
            var accounts = new List<string> { "gamma", "g1", "alpha", "a1", "beta", "b1", "zz", "aa", "mm" };

            var first = BridgeFleetView.Build(rows, accounts);

            // Same data, different presentation order in -- the answer must not move.
            var shuffled = new List<FleetCopierRow> { rows[2], rows[0], rows[1] };
            var shuffledAccounts = new List<string> { "mm", "b1", "zz", "alpha", "beta", "a1", "aa", "gamma", "g1" };
            var second = BridgeFleetView.Build(shuffled, shuffledAccounts);

            var firstNames = string.Join(",", first.Select(n => n.Name).ToArray());
            var secondNames = string.Join(",", second.Select(n => n.Name).ToArray());
            Assert(firstNames == secondNames && firstNames.StartsWith("alpha,beta,gamma"),
                "P2-127: equally ranked groups come back in the same order every time");

            var u1 = Named(first, BridgeFleetView.UnlinkedName);
            var u2 = Named(second, BridgeFleetView.UnlinkedName);
            Assert(u1 != null && u2 != null
                   && string.Join(",", u1.Children.Select(n => n.Name).ToArray())
                      == string.Join(",", u2.Children.Select(n => n.Name).ToArray())
                   && At(u1.Children, 0) != null && At(u1.Children, 0).Name == "aa",
                "P2-127: equally ranked unlinked accounts come back in the same order every time");
        }

        private static void TestP2127_AnAccountWithNoCopierRelationshipIsNotRankedWORST()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: an inapplicable state is not an unreadable one");

            // 95 of 97 accounts on the live box are in no copier relationship. Ranking them
            // WORST paints 95 permanent red rows -- an alarm that is always on, which is off.
            // Ranking them "ok" is the opposite lie. So the rank is a third thing, above every
            // real rank, that a renderer can colour as neither.
            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5) },
                new List<string> { "Sim101", "SimCopy2", "Spare1" });

            var u = Named(tree, BridgeFleetView.UnlinkedName);
            Assert(u != null && At(u.Children, 0) != null
                   && At(u.Children, 0).Rank == BridgeFleetView.NotApplicableRank
                   && BridgeFleetView.NotApplicableRank != BridgeFleetView.UnknownRank,
                "P2-127: an account in no relationship is NOT APPLICABLE, not unknown-and-worst");

            // It must also not sort as the worst thing on the page, or the distinction is a
            // name with no behaviour behind it.
            Assert(u != null && u.Rank > BridgeFleetView.WorstRank
                   && BridgeFleetView.NotApplicableRank > BridgeFleetView.RankOfCopierRow(5),
                "P2-127: not-applicable sorts as the least severe thing, below every real rank");
        }

        private static void TestP2127_TheUnlinkedNodeIsPresentEvenWhenItIsEmpty()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the unlinked node is present even when empty");

            // FOUND BY PROBING A 15/15 BATTERY, not by review: dropping the node when it has no
            // children survived the whole suite. Every other test here supplies a spare account,
            // so the empty case was never driven.
            //
            // It matters because an ABSENT node and an EMPTY one read identically to whatever
            // renders this -- the operator cannot tell "no unlinked accounts" from "that section
            // failed to load". Same shape as the loop's own CF-9.
            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5) },
                new List<string> { "Sim101", "SimCopy2" });

            var u = Named(tree, BridgeFleetView.UnlinkedName);
            Assert(u != null && u.Kind == "unlinked" && u.Children.Count == 0,
                "P2-127: the Unlinked node is emitted even with nothing under it");

            Assert(At(tree, tree.Count - 1) != null
                   && At(tree, tree.Count - 1).Name == BridgeFleetView.UnlinkedName,
                "P2-127: and an empty Unlinked node is still LAST");
        }

        // ================================================================================
        // P1-131. "Would severing this connection strand the order?" -- the FOURTH question
        // over NT8's OrderState enum, after the core's OccupiesSlot ("must I not place a
        // second one?"), ProvidesCoverage ("is the position protected?") and
        // AcceptsModification ("can I Change() it now?").
        //
        // Measured live: nt_connection reported workingOrders: 7 on TPT -- four real bracket
        // legs on the funded account and three Sim101 orders that had been CancelPending for
        // about five hours. The old hand-written list omitted SIX non-terminal states, every
        // omission in the direction that PERMITS a disconnect.
        // ================================================================================

        // Every OrderState NT8 8.1 defines. The point of listing them here is that a state
        // added later is NOT in this array, and the unknown-name test covers that case.
        private static readonly string[] AllOrderStates = new[]
        {
            "Initialized", "Submitted", "Accepted", "AcceptedByRisk", "Working", "PartFilled",
            "TriggerPending", "ChangeSubmitted", "ChangePending", "CancelSubmitted",
            "CancelPending", "Suspended", "Filled", "Cancelled", "Rejected", "Unknown"
        };

        private static void TestP1131_EveryNonTerminalStateWouldBeStranded()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-131: every non-terminal state would be stranded");

            // The six the old list omitted, named individually so a regression says WHICH.
            // ChangePending was in the old list and ChangeSubmitted was not; CancelPending was
            // in and CancelSubmitted was not. Each handshake has two halves.
            var omitted = new[] { "Initialized", "AcceptedByRisk", "ChangeSubmitted",
                                  "CancelSubmitted", "Suspended", "Unknown" };
            var missed = omitted.Where(st => !BridgeOrderLiveness.WouldBeStrandedByDisconnect(st)).ToList();
            Assert(missed.Count == 0, string.Format(
                "P1-131: the six states the old list omitted all strand ({0} still do not)",
                missed.Count));

            // And the twins are treated alike, which is the asymmetry that gave the old list away.
            Assert(BridgeOrderLiveness.WouldBeStrandedByDisconnect("ChangeSubmitted")
                   == BridgeOrderLiveness.WouldBeStrandedByDisconnect("ChangePending")
                   && BridgeOrderLiveness.WouldBeStrandedByDisconnect("CancelSubmitted")
                   == BridgeOrderLiveness.WouldBeStrandedByDisconnect("CancelPending"),
                "P1-131: both halves of a handshake answer the same");

            // ⚠️ The measured case. Borrowing the core's OccupiesSlot would answer NO here,
            // because it excludes Departing on purpose -- and these are the orders that had
            // been stuck for five hours.
            Assert(BridgeOrderLiveness.WouldBeStrandedByDisconnect("CancelPending"),
                "P1-131: an order stuck cancelling is the strongest case of stranded, not the weakest");
        }

        private static void TestP1131_TheThreeTerminalStatesAndOnlyThose()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-131: exactly three terminal states");

            var terminal = AllOrderStates.Where(BridgeOrderLiveness.IsTerminal).OrderBy(x => x).ToArray();
            Assert(string.Join(",", terminal) == "Cancelled,Filled,Rejected", string.Format(
                "P1-131: Filled, Cancelled and Rejected are terminal and nothing else is (got {0})",
                string.Join(",", terminal)));

            // Rejected is the one GetOrders' inline filter forgot, which is how a rejected
            // order was served by an endpoint advertising "active/working".
            Assert(BridgeOrderLiveness.IsTerminal("Rejected"),
                "P1-131: Rejected is terminal -- the state the orders filter forgot");

            // The two questions are exact complements. If they ever drift, one of the two
            // call sites is answering a question nobody asked.
            var disagree = AllOrderStates.Where(st =>
                BridgeOrderLiveness.IsTerminal(st) == BridgeOrderLiveness.WouldBeStrandedByDisconnect(st)).ToList();
            Assert(disagree.Count == 0,
                "P1-131: stranded and terminal are exact complements across every state");
        }

        private static void TestP1131_AnUnknownStateNameFailsSAFE()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-131: an unrecognised state fails SAFE, not quiet");

            // The directions do not cost the same. A false YES costs a refused disconnect the
            // operator can override with confirmDisruptive; a false NO costs a protective stop
            // left at a broker this process can no longer reach.
            Assert(BridgeOrderLiveness.WouldBeStrandedByDisconnect("SomeStateNT8AddsIn2027")
                   && !BridgeOrderLiveness.IsTerminal("SomeStateNT8AddsIn2027"),
                "P1-131: a state this build has never heard of is treated as still out there");

            Assert(BridgeOrderLiveness.WouldBeStrandedByDisconnect(null)
                   && BridgeOrderLiveness.WouldBeStrandedByDisconnect(""),
                "P1-131: a null or blank state name is treated as still out there");

            // Casing/whitespace arrive from ToString(), so they should not decide safety --
            // but a MISSPELLING must not silently become terminal either.
            Assert(BridgeOrderLiveness.IsTerminal(" Filled ") && !BridgeOrderLiveness.IsTerminal("filled"),
                "P1-131: whitespace is trimmed and an unexpected casing is not assumed terminal");
        }

        private static void TestP1131_NoBridgePathKeepsItsOwnStateList()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-131: no bridge path keeps its own OrderState list");

            string code = File.ReadAllText(BridgeSourcePath());

            // SOURCE GATE (McpBridgeAddOn.cs is in no test build -- P2-27). It proves wiring,
            // not behaviour; nt_compile and the live read are the evidence for that half.
            Assert(!code.Contains("OccupiesSlotForBridge"),
                "P1-131: the hand-rolled predicate is gone, not merely bypassed");

            // The defect was a SECOND LIST, so the gate is against an inline terminal-state
            // comparison -- but ONLY inside the method this ticket is about. A blanket ban over
            // the file fails on nine legitimate sites: the cancel paths ask a different question
            // again ("what should I try to cancel?"), and 2661 asks "was this REJECTED?", which
            // is a real distinct question rather than a terminal-set test. One of those cancel
            // filters carries a comment saying narrowing it is a behaviour change; this ticket is
            // about the REPORT. So: state the region, and print what was inspected, or this is a
            // gate nobody can tell is looking at nothing.
            // Located by index rather than a multiline regex: the signature gained three
            // parameters in P2-109, and a pattern that silently stops matching is the failure
            // this whole class of gate exists to avoid. The region ends at the next member.
            int gStart = code.IndexOf("private object GetOrders(");
            int gEnd = gStart < 0 ? -1 : code.IndexOf("\n        private ", gStart + 10);
            string getOrders = (gStart >= 0 && gEnd > gStart) ? code.Substring(gStart, gEnd - gStart) : "";
            Assert(getOrders.Length > 200, string.Format(
                "P1-131: the orders route is locatable for inspection ({0} chars read)",
                getOrders.Length));
            var inlineStateTests = Regex.Matches(getOrders, @"OrderState\.(Filled|Cancelled|Rejected)");
            Assert(getOrders.Length > 200 && inlineStateTests.Count == 0, string.Format(
                "P1-131: the orders route keeps no terminal-state list of its own ({0} found in {1} chars)",
                inlineStateTests.Count, getOrders.Length));

            // Both stranding call sites go through the one predicate. P2-127 follow-up added a
            // THIRD legitimate site: the dormant-account classifier asks the same question of
            // every connection-less account ("is there anything at the broker I must not hide?").
            // The count is pinned so a future hand-rolled list is caught, and the classifier's
            // use is asserted by name so the third site is not mistaken for a regression.
            Assert(Regex.Matches(code, @"BridgeOrderLiveness\.WouldBeStrandedByDisconnect\b").Count == 3,
                "P1-131: all workingOrders counts consult the shared predicate (2 report sites "
                + "+ the dormant-account classifier)");
            Assert(Regex.IsMatch(code, @"ClassifyDormantAccounts[\s\S]*?WouldBeStrandedByDisconnect"),
                "P1-131: the dormant classifier uses the same predicate -- an account with a "
                + "working order is never hidden");
            Assert(Regex.Matches(code, @"BridgeOrderLiveness\.IsTerminal\b").Count == 1,
                "P1-131: the orders filter consults it too");
        }

        // ================================================================================
        // P2-178. `nt_extract_trades` (and nt_capture_chart's fillTime) stamped an EASTERN
        // wall-clock with a literal `Z`, so every consumer read it four hours off. BridgeTradeTime
        // names no NT8 type, so the conversion is EXECUTED here, not asserted about. The two DST
        // cases are the discriminators: a fixed 4h subtraction -- the wrong fix the plan warns
        // against -- passes the August case and fails January. [[detector-needs-a-negative-test]].
        // ================================================================================

        private static void TestP2178_TheMeasuredDefectConvertsToTrueUtc()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: the measured Eastern stamp becomes the UTC the ledger had");

            // MEASURED 2026-08-20: the tool reported 09:57:51.985 (Eastern) and stamped it `Z`;
            // interventions.jsonl had the same order at 13:57:52Z. August is EDT (-4).
            var etAug = new DateTime(2026, 8, 20, 9, 57, 51, 985, DateTimeKind.Unspecified);
            string utc = BridgeTradeTime.ToUtcIso(etAug);
            Assert(utc == "2026-08-20T13:57:51.985Z", string.Format(
                "09:57:51.985 ET on 2026-08-20 is 13:57:51.985Z, not the false 09:57:51.985Z the "
                + "tool reported. Got: {0}", utc));
        }

        private static void TestP2178_TheOffsetFollowsTheDateNotAConstant()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: the offset is DST-dependent, not a fixed 4h subtraction");

            // Same wall-clock, two dates on opposite sides of the DST boundary. EDT is -4, EST is
            // -5. A "fix" that subtracts a constant four hours gets exactly one of these right --
            // which is why subtracting four hours was the wrong fix.
            var summer = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Unspecified);   // EDT -4
            var winter = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Unspecified);   // EST -5
            string summerUtc = BridgeTradeTime.ToUtcIso(summer);
            string winterUtc = BridgeTradeTime.ToUtcIso(winter);
            Assert(summerUtc == "2026-08-20T13:00:00.000Z", "summer 09:00 ET -> 13:00Z (EDT -4). Got: " + summerUtc);
            Assert(winterUtc == "2026-01-15T14:00:00.000Z", "winter 09:00 ET -> 14:00Z (EST -5). Got: " + winterUtc);
            Assert(summerUtc.Substring(11, 2) != winterUtc.Substring(11, 2),
                "the two offsets DIFFER, so a constant subtraction cannot be correct for both");
        }

        private static void TestP2178_TheTrailingZIsNowTrueUtc()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: the trailing Z now names real UTC");

            // Convert to UTC by an independent path (TimeZoneInfo on the same zone) and confirm the
            // string agrees. The Z is a claim; this checks the claim rather than trusting it.
            var et = new DateTime(2026, 8, 20, 9, 57, 51, 985, DateTimeKind.Unspecified);
            DateTime expected = TimeZoneInfo.ConvertTimeToUtc(et, BridgeTradeTime.EasternZone);
            string s = BridgeTradeTime.ToUtcIso(et);
            Assert(s.EndsWith("Z"), "still ends with Z: " + s);
            Assert(s == expected.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                "the stamped instant equals the real UTC instant. Got: " + s);
        }

        private static void TestP2178_TheSpringForwardGapDoesNotThrow()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: an impossible wall-clock does not take out the whole response");

            // 2:30am ET on the 2026 spring-forward Sunday (2026-03-08) never happened. ConvertTimeToUtc
            // throws ArgumentException on it, and a throw here would drop the ENTIRE trade list for one
            // impossible stamp. It must return a real instant instead.
            var gap = new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
            bool threw = false;
            string s = null;
            try { s = BridgeTradeTime.ToUtcIso(gap); } catch { threw = true; }
            Assert(!threw, "the spring-forward gap is handled, not thrown");
            Assert(s != null && s.EndsWith("Z"), "and still produces a UTC stamp. Got: " + (s ?? "<threw>"));
        }

        private static void TestP2178_AKindTaggedInputIsTreatedAsAWallClock()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: a Kind-tagged value is read as an Eastern wall-clock, not thrown on");

            // exec.Time arrives Kind Unspecified, but ConvertTimeToUtc THROWS if a value's Kind
            // says Utc/Local and contradicts the zone -- so the SpecifyKind normalisation is
            // load-bearing. Feed a Utc-tagged value carrying the SAME wall-clock reading: it must
            // be interpreted as the ET wall time (13:57Z), not thrown on and not taken literally.
            var tagged = new DateTime(2026, 8, 20, 9, 57, 51, 985, DateTimeKind.Utc);
            bool threw = false;
            string s = null;
            try { s = BridgeTradeTime.ToUtcIso(tagged); } catch { threw = true; }
            Assert(!threw, "a Kind-tagged input does not throw (SpecifyKind normalises it)");
            Assert(s == "2026-08-20T13:57:51.985Z",
                "and the wall-clock reading is interpreted in the source zone. Got: " + (s ?? "<threw>"));
        }

        private static void TestP2178_BothBridgeCallSitesConvertRatherThanStampingZ()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-178: both exec-time call sites convert, and neither stamps a false Z");

            // SOURCE GATE (McpBridgeAddOn.cs is in no test build -- P2-27). It proves the wiring:
            // the executed test above proves the conversion is right, this proves the addon actually
            // uses it. Comments are stripped so the header's mention of the old pattern is not
            // mistaken for a live one.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            Assert(!code.Contains("exec.Time.ToString(\"yyyy-MM-ddTHH:mm:ss.fffZ\")"),
                "P2-178: ExtractTrades no longer stamps a literal Z on an Eastern exec.Time");
            Assert(!code.Contains("targetExec.Time.ToString(\"yyyy-MM-ddTHH:mm:ss.fffZ\")"),
                "P2-178: fillTime no longer stamps a literal Z on an Eastern targetExec.Time");

            // NARROW: the ban is on the false Z over an EXEC time only. DateTime.UtcNow.ToString(
            // "...fffZ") is CORRECT and must survive -- the value already IS UTC. So assert the two
            // conversions are present rather than banning the format string globally.
            Assert(Regex.Matches(code, @"BridgeTradeTime\.ToUtcIso\b").Count == 2,
                "P2-178: both call sites (ExtractTrades, fillTime) route through the converter");
            Assert(Regex.IsMatch(code, @"time\s*=\s*BridgeTradeTime\.ToUtcIso\(exec\.Time\)"),
                "P2-178: ExtractTrades' `time` field is the converted value");
            Assert(Regex.IsMatch(code, @"fillTime""\]\s*=\s*BridgeTradeTime\.ToUtcIso\(targetExec\.Time\)"),
                "P2-178: fillTime is the converted value");
        }

        // ================================================================================
        // P2-181. The bridge twin of P2-150: PlaceOcoOrder read the exit legs' OrderState in the
        // same breath as Submit() and derived "partial_submit" from it -- a status no live input
        // could set, because Submit is async and the legs are Initialized/Submitted at that
        // instant. SOURCE GATE only (McpBridgeAddOn.cs is in no test build, P2-27); the honest
        // behaviour on the core side is executed by nt8-riskguard's P2-150 test.
        // ================================================================================

        private static void TestP2181_TheOcoPathReportsPendingLegsNotADeadVerdict()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-181: PlaceOcoOrder reports pending_legs, not a synchronous partial_submit");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // Locate the OCO method by index (a multiline regex silently stops matching when a
            // signature changes -- the exact failure this class of gate exists to avoid). The
            // region ends at the next member.
            int start = code.IndexOf("private object PlaceOcoOrder(");
            int end = start < 0 ? -1 : code.IndexOf("\n        private ", start + 10);
            string oco = (start >= 0 && end > start) ? code.Substring(start, end - start) : "";
            Assert(oco.Length > 200, string.Format(
                "the OCO method is locatable for inspection ({0} chars read)", oco.Length));

            Assert(oco.Length > 200 && !oco.Contains("partial_submit"),
                "P2-181: the dead 'partial_submit' verdict is gone from PlaceOcoOrder -- a status "
                + "no live input could set is not a status");
            Assert(oco.Length > 200 && !oco.Contains("rejectedOrders"),
                "P2-181: the synchronous rejected-orders read that fed it is gone too -- the bug "
                + "was the TIMING, so the read is removed, not widened");
            Assert(Regex.IsMatch(oco, "status\\s*=\\s*\"pending_legs\""),
                "P2-181: the OCO path reports pending_legs, the honest status");
        }

        // ================================================================================
        // P2-138. BridgeFleetView.Build had NO CALLER outside this file. 199 lines of view
        // logic, 137 lines of tests, a 253-line mutation battery, all green in CI -- and no
        // endpoint served it and ui/index.html was never touched by the commit that added it.
        // P2-127 was scoped to "build the class"; the wiring slice was never filed, and the
        // file's own comment says so at NotApplicableRank: "TEMPORARY, AND THE NEXT SLICE OF
        // P2-127 REPLACES IT."
        //
        // This is dead-safety-machinery in the bridge repo: written, tested, deployed, and
        // wired to nothing. The operator's report -- "I still don't see the UI changes for
        // the copier" -- is the only surface on which it was detectable.
        // ================================================================================

        /// <summary>
        /// The mapping half, EXECUTED against a payload captured from the deployed box on
        /// 2026-08-17 rather than one written from memory. Two relationships, 97 accounts.
        /// </summary>
        /// <summary>
        /// P2-127 slice 3. All THREE tabs, always, in §4's order -- even when a tab has nothing to
        /// show.
        ///
        /// ⚠️ THIS IS SLICE 1'S SIXTEENTH MUTANT, ONE LEVEL UP. That battery went 15/15 and the
        /// sixteenth mutant is the lesson: dropping the `Unlinked` node when it was EMPTY survived
        /// the whole suite, because "an absent node and an empty one read identically to whatever
        /// renders them". A tab strip that omits the risk tab on a box with no rules loaded is the
        /// same defect, and the operator reads three tabs as "I have seen everything".
        /// </summary>
        private static void TestP2127_AllThreeTabsAlwaysExistInSectionFoursOrder()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: all three inspector tabs exist, in section 4's order");

            var tabs = BridgeInspectorTabs.Build(
                new List<FleetCopierRow>(), new List<InspectorRuleRow>(), null, 0);

            Assert(tabs != null && tabs.Count == 3, string.Format(
                "P2-127: three tabs even with nothing loaded (got {0}). Section 4 names exactly "
                + "[copier] [risk] [rare]; a strip that hides an empty one reads as 'nothing to see' "
                + "on precisely the box where nothing has been evaluated.",
                tabs == null ? -1 : tabs.Count));
            if (tabs == null || tabs.Count != 3) return;

            Assert(tabs[0].Id == BridgeInspectorTabs.CopierTab
                   && tabs[1].Id == BridgeInspectorTabs.RiskTab
                   && tabs[2].Id == BridgeInspectorTabs.RareTab,
                string.Format("P2-127: in section 4's order -- copier, risk, rare (got {0}, {1}, {2})",
                    tabs[0].Id, tabs[1].Id, tabs[2].Id));

            // P2-127 follow-up (2026-08-18). The tab's LABEL is "Settings", not "Rare". Commit
            // a983455 moved the guard config editor behind this tab, and a tab labelled "Rare"
            // whose badge read "No conflicts (0)" made the operator conclude the settings had
            // been lost. The label is the reachability surface: it must say what the tab holds.
            var settings = tabs.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RareTab);
            Assert(settings != null && settings.Label == "Settings",
                "P2-127: the config tab is labelled 'Settings' -- the editor it holds is the "
                + "guard config, and a label that does not say so is how the operator concluded "
                + "the settings had been lost (got '" + (settings == null ? "<null>" : settings.Label) + "')");

            foreach (var t in tabs)
                Assert(!string.IsNullOrEmpty(t.Badge) && !string.IsNullOrEmpty(t.Reason),
                    "P2-127: every tab carries a badge AND a reason, even an empty one ('" + t.Id
                    + "' badge='" + t.Badge + "' reason='" + t.Reason + "'). A blank badge over a "
                    + "state nobody has looked at is the hiding hazard §4.2 killed tabs for.");
        }

        /// <summary>
        /// P2-127 slice 3. The risk tab must fold `Inert`.
        ///
        /// ⚠️ MEASURED ON THE DEPLOYED BOX, 2026-08-17, and this is the whole reason the test exists:
        /// `/api/riskguard/inventory` reports `unevaluatedRules` EMPTY while **559 of 2231 rule rows
        /// are `Inert`**. A strip that folds only `ConfiguredNotEvaluated` -- the state the page was
        /// built to make unmissable -- renders a clean risk tab over the condition that actually
        /// exists. The badge has to come from the rule ROWS.
        /// </summary>
        private static void TestP2127_TheRiskTabFoldsInertNotJustUnevaluated()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the risk tab folds Inert, which is what the live box has");

            var rules = new List<InspectorRuleRow>
            {
                new InspectorRuleRow { AccountName = "A", RuleName = "Daily loss limit", State = "EvaluatedNotEnforcing" },
                new InspectorRuleRow { AccountName = "A", RuleName = "Trailing drawdown", State = "Inert" },
                new InspectorRuleRow { AccountName = "A", RuleName = "Peak equity giveback", State = "Inert" },
                new InspectorRuleRow { AccountName = "A", RuleName = "Max size", State = "Disabled" },
            };

            var tabs = BridgeInspectorTabs.Build(new List<FleetCopierRow>(), rules, null, 0);
            var risk = tabs == null ? null : tabs.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RiskTab);
            Assert(risk != null, "P2-127: precondition -- a risk tab exists to fold Inert into");
            if (risk == null) return;

            Assert(risk.Rank == BridgeInspectorTabs.RankOfRuleState("Inert"),
                string.Format("P2-127: the tab's rank is the WORST of its rows, and Inert is the "
                    + "worst here (tab rank {0}, Inert ranks {1})",
                    risk.Rank, BridgeInspectorTabs.RankOfRuleState("Inert")));

            Assert(risk.Badge.IndexOf("2", StringComparison.Ordinal) >= 0
                   && risk.Badge.IndexOf("INERT", StringComparison.OrdinalIgnoreCase) >= 0,
                "P2-127: and the badge NAMES the state and COUNTS the rows in it -- 2 inert -- "
                + "recomputed from the rows, never from a counter the producer supplied (F-9, "
                + "P2-103). Got '" + risk.Badge + "'");

            Assert(BridgeInspectorTabs.RankOfRuleState("Inert")
                   < BridgeInspectorTabs.RankOfRuleState("EvaluatedNotEnforcing"),
                "P2-127: Inert is WORSE than EvaluatedNotEnforcing -- a rule that cannot fire is a "
                + "worse answer to 'is the guard protecting me' than one that is evaluating and has "
                + "not tripped. Lower rank is worse, matching the fleet tree's scale.");
        }

        /// <summary>
        /// P2-127 slice 3. A count folded from the detail rows, not taken from a counter -- and the
        /// SELECTION narrows it.
        ///
        /// `F-9`'s class in the optimistic direction is the one that has cost real sessions here:
        /// `P2-116` was measured on the hour the broker was reconnected, with 89 prop accounts
        /// subscribed, 1 reporting equity and all 89 reading `EvaluatedNotEnforcing`. A tab whose
        /// badge is a producer's number cannot disagree with the producer, which is the point of
        /// recomputing.
        /// </summary>
        private static void TestP2127_TheBadgeIsRecountedFromTheRowsAndFollowsTheSelection()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the badge is recounted from the rows, and follows the selection");

            var rules = new List<InspectorRuleRow>
            {
                new InspectorRuleRow { AccountName = "A", RuleName = "r1", State = "Inert" },
                new InspectorRuleRow { AccountName = "A", RuleName = "r2", State = "Inert" },
                new InspectorRuleRow { AccountName = "B", RuleName = "r1", State = "Inert" },
                new InspectorRuleRow { AccountName = "B", RuleName = "r2", State = "EvaluatedNotEnforcing" },
            };

            var all = BridgeInspectorTabs.Build(new List<FleetCopierRow>(), rules, null, 0);
            var allRisk = all == null ? null : all.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RiskTab);
            // ⚠️ FirstOrDefault + an early return, NOT First(). First() on an empty result throws
            // InvalidOperationException, which takes the whole runner down and prints no RESULTS
            // line -- and a suite with no result line is not a red test, it is an unusable baseline.
            Assert(allRisk != null, "P2-127: precondition -- the risk tab is present");
            if (allRisk == null) return;

            Assert(allRisk.Badge.IndexOf("3", StringComparison.Ordinal) >= 0,
                "P2-127: with nothing selected the badge counts the whole fleet -- 3 inert rows "
                + "across two accounts (got '" + allRisk.Badge + "')");

            var oneAccount = BridgeInspectorTabs.Build(new List<FleetCopierRow>(), rules, "B", 0);
            var bRisk = oneAccount == null ? null : oneAccount.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RiskTab);
            Assert(bRisk != null, "P2-127: precondition -- the risk tab is present when an account is selected");
            if (bRisk == null) return;

            Assert(bRisk.Badge.IndexOf("1", StringComparison.Ordinal) >= 0
                   && bRisk.Badge.IndexOf("3", StringComparison.Ordinal) < 0,
                "P2-127: selecting account B narrows it to B's own rows -- 1 inert, not 3. The "
                + "inspector follows the selection; a strip that keeps showing fleet-wide totals "
                + "beside one account's config is answering a question nobody asked (got '"
                + bRisk.Badge + "')");
        }

        /// <summary>
        /// P2-127 slice 3. The live inventory payload maps onto Build's input.
        ///
        /// The same reason `TestP2138_TheLivePayloadMapsOntoTheTreesInput` exists: a field rename in
        /// core has to land HERE, in a failing test, rather than as a blank badge on the page. Field
        /// names are the ones measured off `GET /api/riskguard/inventory` on 2026-08-17.
        /// </summary>
        private static void TestP2127_TheLiveInventoryMapsOntoTheTabsInput()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the live guard inventory maps onto the tab strip's input");

            // Shape measured on the deployed box: accounts[] each with accountName and rules[],
            // and every rule carrying name / state among its nine fields.
            var accounts = JArray.Parse(@"[
              { ""accountName"": ""APEX10121500000148"", ""rules"": [
                  { ""name"": ""Daily loss limit"", ""state"": ""EvaluatedNotEnforcing"" },
                  { ""name"": ""Trailing drawdown"", ""state"": ""Inert"" } ] },
              { ""accountName"": ""Sim101"", ""rules"": [
                  { ""name"": ""Max size"", ""state"": ""Disabled"" } ] }
            ]");

            var rows = BridgeInspectorTabs.RuleRowsFromInventory(accounts);
            Assert(rows != null && rows.Count == 3, string.Format(
                "P2-127: every rule of every account becomes one row ({0} of 3)",
                rows == null ? -1 : rows.Count));
            if (rows == null || rows.Count != 3) return;

            Assert(rows[0].AccountName == "APEX10121500000148" && rows[0].State == "EvaluatedNotEnforcing"
                   && rows[0].RuleName == "Daily loss limit",
                "P2-127: the mapping reads the field names the deployed box actually sends -- "
                + "accountName, rules[].name, rules[].state");

            Assert(rows.Count(r => r.AccountName == "Sim101") == 1,
                "P2-127: and each row carries the account it belongs to, which is what makes the "
                + "selection filter possible at all");
        }

        /// <summary>
        /// P2-127 slice 3. A KNOWN-CLEAN state must not rank as the worst thing on the strip.
        ///
        /// ⚠️ WRITTEN FROM ARBITRATING THE LOOP'S PANEL, and it was the one finding of five that
        /// held. The implementation folded `BridgeFleetView.WorstOf(new int[0])` for a rare tab with
        /// no config conflicts. That returns `UnknownRank`, which IS `WorstRank` — so the tab
        /// rendered as the worst item on the always-visible strip while its own badge read
        /// "No conflicts". What a surface REPORTS disagreeing with what it RANKS is the defect, and
        /// this direction is the one that trains an operator to discount the strip.
        ///
        /// `WorstOf`'s pessimistic empty answer is right for a set it could not read — slice 1's
        /// "a group with no children is not healthy". Zero conflicts is a set that WAS read and was
        /// empty. [[an-inapplicable-state-is-not-unreadable]], which painted 95 of 97 accounts worst
        /// for the same reason.
        /// </summary>
        /// <summary>
        /// P2-127 slice 3. The route SERVES the tabs it builds, and decides nothing itself.
        ///
        /// ⚠️ THIS GATE EXISTS BECAUSE P2-138 WAS EXACTLY THE OPPOSITE. `BridgeFleetView.Build` had
        /// 199 lines of logic, 137 lines of acceptance tests, a 253-line mutation battery, CI-green —
        /// and NO CALLER outside its own test file. Coverage measures whether a thing is CORRECT,
        /// never whether anything CALLS it, and the only surface it was detectable on was the
        /// operator saying they still could not see it. [[dead-safety-machinery-gate]].
        ///
        /// A SOURCE gate rather than a behavioural one because `McpBridgeAddOn.cs` is the one bridge
        /// source `BridgeTests.csproj` cannot compile. The negative controls matter more than the
        /// presence checks: the route must not invent a rank or an order, because anything it decides
        /// is untestable by construction.
        /// </summary>
        private static void TestP2127_TheRouteServesTheTabsAndDecidesNothing()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the inspector route serves the tabs, and decides nothing (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));
            int s = code.IndexOf("case \"/api/ui/inspector\":", StringComparison.Ordinal);
            Assert(s >= 0, "P2-127: the /api/ui/inspector route exists at all -- a decision class "
                + "with no route is P2-138 repeated");
            if (s < 0) return;

            int e = code.IndexOf("\n                case \"", s + 10);
            string handler = e > s ? code.Substring(s, e - s) : code.Substring(s);
            Assert(handler.Length > 200, string.Format(
                "P2-127: the handler is locatable for inspection ({0} chars read)", handler.Length));

            Assert(handler.Contains("BridgeInspectorTabs.Build("), string.Format(
                "P2-127: the route SERVES the tabs -- Build must have a caller outside its test "
                + "file, which is the whole content of P2-138 ({0} chars inspected)", handler.Length));
            Assert(handler.Contains("BridgeInspectorTabs.RuleRowsFromInventory("), string.Format(
                "P2-127: and the rule rows come from the mapper, not from a second hand-rolled read "
                + "of the guard snapshot ({0} chars inspected)", handler.Length));
            Assert(handler.Contains("BridgeFleetView.RowsFromSnapshot("), string.Format(
                "P2-127: and the copier rows come from the SAME mapping the fleet tree uses, so the "
                + "strip and the tree cannot disagree ({0} chars inspected)", handler.Length));

            // ⚠️ THE NEGATIVE CONTROLS. Anything this file decides can only ever be gated by a
            // regex, so the rule is that it decides nothing: no rank, no badge text, no ordering.
            Assert(!Regex.IsMatch(handler, @"Rank\s*=\s*\d"), string.Format(
                "P2-127: the route assigns no rank of its own -- ranks come from the class the "
                + "harness compiles ({0} chars inspected)", handler.Length));
            Assert(!Regex.IsMatch(handler, @"""(copier|risk|rare)"""), string.Format(
                "P2-127: and it does not name a tab id, which would be a second definition of the "
                + "set of tabs and could drift from the class's ({0} chars inspected)",
                handler.Length));
            Assert(!handler.Contains(".Sort(") && !handler.Contains("OrderBy("), string.Format(
                "P2-127: and it does not re-order them -- section 4's order is the class's decision "
                + "and one copy of it is enough ({0} chars inspected)", handler.Length));
        }

        private static void TestP2127_AKnownCleanTabDoesNotRankAsTheWorst()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: a known-clean tab does not rank as the worst");

            var clean = BridgeInspectorTabs.Build(
                new List<FleetCopierRow>(), new List<InspectorRuleRow>(), null, 0);
            var rare = clean == null ? null : clean.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RareTab);
            Assert(rare != null, "P2-127: precondition -- the rare tab is present");
            if (rare == null) return;

            Assert(rare.Rank != BridgeFleetView.UnknownRank,
                string.Format("P2-127: zero config conflicts is CLEAN, not unknown, so the rare tab "
                    + "must not carry UnknownRank ({0}) -- which is WorstRank, and would paint the "
                    + "strip's worst item over a tab whose own badge says there is nothing wrong "
                    + "(got {1})", BridgeFleetView.UnknownRank, rare.Rank));

            Assert(rare.Rank == BridgeInspectorTabs.CleanRank
                   && rare.Rank < BridgeFleetView.NotApplicableRank,
                string.Format("P2-127: and it is CleanRank ({0}), below NotApplicableRank ({1}) -- "
                    + "the question DOES apply to this box and the answer is 'nothing wrong', which "
                    + "is not the same as 'does not apply' (got {2})",
                    BridgeInspectorTabs.CleanRank, BridgeFleetView.NotApplicableRank, rare.Rank));

            var conflicted = BridgeInspectorTabs.Build(
                new List<FleetCopierRow>(), new List<InspectorRuleRow>(), null, 2);
            var badRare = conflicted.First(t => t.Id == BridgeInspectorTabs.RareTab);
            Assert(badRare.Rank == BridgeFleetView.WorstRank
                   && badRare.Rank < rare.Rank,
                string.Format("P2-127: and a real conflict IS the worst, strictly worse than clean "
                    + "({0} vs {1}) -- the negative control, because a fix that ranked everything "
                    + "clean would pass every assertion above", badRare.Rank, rare.Rank));
        }

        /// <summary>
        /// P2-127 slice 3. The account filter is case-insensitive, matching how the core compares
        /// account names everywhere (`OrdinalIgnoreCase`). A case-sensitive compare returns a
        /// well-formed "No data" tab for an account that is present — a quiet wrong answer rather
        /// than a visible one. [[weigh-the-quiet-failure-above-the-loud]].
        /// </summary>
        /// <summary>
        /// P2-127 slice 3. A rule state this code CANNOT READ must rank worst, not best.
        ///
        /// ⚠️ WRITTEN FROM TWO BATTERY SURVIVORS, and the gap was real: nothing here exercised an
        /// unrecognised or empty state, so both mutants that made them read as CLEAN survived a green
        /// suite. That is the fail-OPEN direction — a rule state this class has never heard of is the
        /// one case where it genuinely cannot know, and calling it healthy is an alarm that stays off.
        ///
        /// This is the DISTINCTION `BridgeFleetView` already draws, and it draws it twice:
        /// `RankOfSystemSeverity` returns `UnknownRank` for a name it does not recognise, while
        /// `NotApplicableRank` is deliberately NOT `UnknownRank` because "does not apply" is a
        /// different fact from "could not be read". A guard rule whose state is a string from the
        /// future is unreadable, not inapplicable. [[an-inapplicable-state-is-not-unreadable]] is the
        /// other half — do not confuse them, or you get the 95-of-97-accounts-painted-worst defect.
        /// </summary>
        private static void TestP2127_AnUnreadableRuleStateRanksWorstNotBest()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: an unreadable rule state ranks worst, not best");

            Assert(BridgeInspectorTabs.RankOfRuleState("SomethingCoreAddedLater")
                   == BridgeFleetView.UnknownRank,
                string.Format("P2-127: a state this class does not recognise is UnknownRank ({0}), "
                    + "not clean. A rule whose state is a string from the future is the one case it "
                    + "cannot know, and reading it as healthy is the fail-OPEN direction (got {1})",
                    BridgeFleetView.UnknownRank,
                    BridgeInspectorTabs.RankOfRuleState("SomethingCoreAddedLater")));

            Assert(BridgeInspectorTabs.RankOfRuleState(null) == BridgeFleetView.UnknownRank
                   && BridgeInspectorTabs.RankOfRuleState("") == BridgeFleetView.UnknownRank,
                "P2-127: and so are null and empty -- a row the producer sent without a state is a "
                + "row nothing is known about, not a healthy one");

            Assert(BridgeInspectorTabs.RankOfRuleState("SomethingCoreAddedLater")
                   < BridgeInspectorTabs.RankOfRuleState("EvaluatedNotEnforcing"),
                "P2-127: so an unreadable state is strictly WORSE than the healthiest real one, and "
                + "sorts above it on a worst-first strip");

            // It must reach the TAB, not just the ranker: a strip is only as honest as what it folds.
            var tabs = BridgeInspectorTabs.Build(
                new List<FleetCopierRow>(),
                new List<InspectorRuleRow>
                {
                    new InspectorRuleRow { AccountName = "A", RuleName = "r1", State = "EvaluatedNotEnforcing" },
                    new InspectorRuleRow { AccountName = "A", RuleName = "r2", State = "WhoKnows" },
                },
                null, 0);
            var risk = tabs == null ? null : tabs.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RiskTab);
            Assert(risk != null && risk.Rank == BridgeFleetView.UnknownRank,
                string.Format("P2-127: and one unreadable row drags the whole tab to UnknownRank, "
                    + "because the fold is worst-wins (got {0})", risk == null ? -99 : risk.Rank));
            Assert(risk != null && !string.IsNullOrEmpty(risk.Badge),
                "P2-127: and it still carries a badge -- the worst case is exactly when the strip "
                + "must not go blank");
        }

        private static void TestP2127_TheAccountFilterIgnoresCaseLikeTheCoreDoes()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the account filter ignores case, as the core does");

            var rules = new List<InspectorRuleRow>
            {
                new InspectorRuleRow { AccountName = "Sim101", RuleName = "r1", State = "Inert" },
                new InspectorRuleRow { AccountName = "Sim101", RuleName = "r2", State = "Inert" },
            };

            var tabs = BridgeInspectorTabs.Build(new List<FleetCopierRow>(), rules, "SIM101", 0);
            var risk = tabs == null ? null : tabs.FirstOrDefault(t => t.Id == BridgeInspectorTabs.RiskTab);
            Assert(risk != null, "P2-127: precondition -- the risk tab is present for a cased selection");
            if (risk == null) return;

            Assert(risk.Rank == BridgeInspectorTabs.RankOfRuleState("Inert"),
                string.Format("P2-127: 'SIM101' selects 'Sim101''s rows, so the tab carries Inert "
                    + "({0}) rather than an empty-set answer (got {1})",
                    BridgeInspectorTabs.RankOfRuleState("Inert"), risk.Rank));
            Assert(risk.Badge.IndexOf("2", StringComparison.Ordinal) >= 0,
                "P2-127: and it counts both of that account's rows (got '" + risk.Badge + "')");
        }

        private static void TestP2138_TheLivePayloadMapsOntoTheTreesInput(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-138: the live copier payload maps onto the fleet tree's input");

            string fixture = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "fixtures", "copier_snapshot_live_20260817.json"));
            Assert(File.Exists(fixture), "the captured live payload is readable at " + fixture);
            if (!File.Exists(fixture)) return;

            var payload = JObject.Parse(File.ReadAllText(fixture));
            var rows = payload["rows"] as JArray;
            Assert(rows != null && rows.Count == 2,
                string.Format("the fixture holds the two relationships the box reported ({0})",
                    rows == null ? -1 : rows.Count));

            var mapped = BridgeFleetView.RowsFromSnapshot(rows);
            Assert(mapped != null && mapped.Count == 2, string.Format(
                "P2-138: every relationship becomes exactly one fleet row ({0} of {1})",
                mapped == null ? -1 : mapped.Count, rows == null ? -1 : rows.Count));
            if (mapped == null || mapped.Count != 2) return;

            // The names the deployed box actually sends. A rename in core lands HERE and not
            // as a blank column on the page.
            Assert(mapped[0].LeaderAccountName == "Sim101" && mapped[0].FollowerAccountName == "Sim-ORB",
                "P2-138: the mapping reads the field names the deployed box actually sends");

            // 0 is WORST on the way in and must stay 0-is-worst: this is the scale the file
            // header warns disagrees with the `system` block's.
            Assert(mapped[0].Severity == 5,
                "P2-138: a row's severity crosses the mapping unchanged, 0 still worst");

            // The two fields the BRIDGE adds after core serialises -- the reason this takes the
            // enriched array and not a re-read of the engine.
            Assert(mapped[0].Enforcing == false && mapped[0].NotEnforcingLabel == "disabled",
                "P2-138: the bridge's own enrichment survives the mapping, so the tree can say "
                + "WHY a row is not enforcing without a second derivation");

            // FleetNode.Badge is DECLARED AND ASSIGNED NOWHERE -- the same never-set field as
            // P3-137's ActiveBracket.IsComplete, in the fleet view itself. Section 4's rows read
            // "follower_1 1.0x ✔MATCH", so a tree of bare names is not that pane. The label is
            // already on the row and needs no second derivation to become the badge.
            var tree = BridgeFleetView.Build(mapped, new List<string> { "Sim101", "Sim-ORB" });
            FleetNode follower = null;
            foreach (var g in tree)
                foreach (var c in g.Children)
                    if (c.Name == "Sim-ORB") follower = c;

            Assert(follower != null && !string.IsNullOrEmpty(follower.Badge), string.Format(
                "P2-138: a follower node carries the badge section 4's row shows (badge={0})",
                follower == null ? "<no node>" : (follower.Badge ?? "<null>")));
            Assert(follower != null && follower.Badge == "disabled",
                "P2-138: and the badge is the label the row already carried, not a fourth "
                + "vocabulary invented for the page");
        }

        /// <summary>
        /// The badge merge, for a follower holding MORE THAN ONE relationship -- one per
        /// instrument, the case FleetCopierRow.InstrumentFullName exists to make representable.
        /// Both live rows on the box are instrument-less, so production exercises none of this
        /// and only a test can.
        ///
        /// The rule: a refusal already seen is never displaced by an enforcing row. "This
        /// follower is refusing on one of its instruments" is the fact the operator needs, and
        /// showing the reassuring badge beside the worse rank is precisely the defect the RANK
        /// merge beside it exists to prevent.
        /// </summary>
        private static void TestP2138_AFollowerWithTwoRelationshipsShowsTheRefusal()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-138: a follower holding two relationships shows the refusing one");

            Func<string, bool, string, int, FleetCopierRow> row =
                (instrument, enforcing, label, severity) => new FleetCopierRow
                {
                    LeaderAccountName = "Sim101",
                    FollowerAccountName = "Sim-ORB",
                    GroupName = "G",
                    InstrumentFullName = instrument,
                    Severity = severity,
                    Enforcing = enforcing,
                    NotEnforcingLabel = label
                };

            Func<List<FleetCopierRow>, FleetNode> followerIn = rs =>
            {
                foreach (var g in BridgeFleetView.Build(rs, new List<string> { "Sim101", "Sim-ORB" }))
                    foreach (var c in g.Children)
                        if (c.Name == "Sim-ORB") return c;
                return null;
            };

            // Enforcing first, then a refusal at the SAME rank. Arrival order must not decide.
            var enforcingThenRefusal = followerIn(new List<FleetCopierRow> {
                row("NQ 12-26", true,  null,       4),
                row("ES 12-26", false, "disabled", 4) });
            Assert(enforcingThenRefusal != null && enforcingThenRefusal.Badge == "disabled", string.Format(
                "P2-138: at an equal rank the refusal is shown, not the enforcing row that "
                + "happened to arrive first (badge={0})",
                enforcingThenRefusal == null ? "<no node>" : (enforcingThenRefusal.Badge ?? "<null>")));

            // The same two rows the other way round must give the SAME answer, or the page
            // renders differently on two polls of identical data.
            var refusalThenEnforcing = followerIn(new List<FleetCopierRow> {
                row("ES 12-26", false, "disabled", 4),
                row("NQ 12-26", true,  null,       4) });
            Assert(refusalThenEnforcing != null && refusalThenEnforcing.Badge == "disabled",
                "P2-138: and the answer does not depend on the order the rows arrive in");

            // An enforcing row at a WORSE rank still must not erase a refusal already seen.
            var refusalThenWorseEnforcing = followerIn(new List<FleetCopierRow> {
                row("ES 12-26", false, "quarantined", 4),
                row("NQ 12-26", true,  null,          1) });
            Assert(refusalThenWorseEnforcing != null
                   && refusalThenWorseEnforcing.Badge == "quarantined"
                   && refusalThenWorseEnforcing.Rank == 1, string.Format(
                "P2-138: a worse-ranked ENFORCING row takes the rank but never erases a refusal "
                + "already seen (badge={0}, rank={1})",
                refusalThenWorseEnforcing == null ? "<no node>" : (refusalThenWorseEnforcing.Badge ?? "<null>"),
                refusalThenWorseEnforcing == null ? -1 : refusalThenWorseEnforcing.Rank));

            // Two refusals: the worse-ranked one is the one to show.
            var twoRefusals = followerIn(new List<FleetCopierRow> {
                row("ES 12-26", false, "disabled",    4),
                row("NQ 12-26", false, "quarantined", 1) });
            Assert(twoRefusals != null && twoRefusals.Badge == "quarantined", string.Format(
                "P2-138: between two refusals the WORSE-ranked one is displayed (badge={0})",
                twoRefusals == null ? "<no node>" : (twoRefusals.Badge ?? "<null>")));

            // Negative control: an all-enforcing follower still gets a badge, or the pane shows
            // a bare name and the operator cannot tell it from a node with no state at all.
            var bothEnforcing = followerIn(new List<FleetCopierRow> {
                row("ES 12-26", true, null, 4),
                row("NQ 12-26", true, null, 4) });
            Assert(bothEnforcing != null && bothEnforcing.Badge == BridgeFleetView.EnforcingBadge,
                "P2-138: and a follower enforcing on every relationship says so, rather than "
                + "rendering as a bare name");
        }

        /// <summary>
        /// The route half. A source gate, because McpBridgeAddOn.cs is the one bridge source
        /// BridgeTests.csproj cannot compile -- stated, with the region and its size printed,
        /// so this cannot pass by inspecting nothing.
        /// </summary>
        private static void TestP2138_TheRouteServesTheTreeItBuilds()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-138: the copier route serves the fleet tree (SOURCE gate)");

            // Comments stripped, or a sentence ABOUT the wiring would satisfy a gate that
            // exists to prove the wiring. Region located by index and its size printed, for
            // the reason P1-131 records: a pattern that silently stops matching is the exact
            // failure this class of gate exists to avoid.
            string code = StripComments(File.ReadAllText(BridgeSourcePath()));
            int s = code.IndexOf("private object GetCopierSnapshot()");
            int e = s < 0 ? -1 : code.IndexOf("\n        private ", s + 10);
            string method = (s >= 0 && e > s) ? code.Substring(s, e - s) : "";
            Assert(method.Length > 200, string.Format(
                "P2-138: the copier snapshot route is locatable for inspection ({0} chars read)",
                method.Length));

            Assert(method.Contains("BridgeFleetView.Build("), string.Format(
                "P2-138: the route SERVES the tree -- Build had no caller outside its test file "
                + "({0} chars inspected)", method.Length));
            Assert(Regex.IsMatch(method, @"payload\[""fleet""\]"), string.Format(
                "P2-138: and the tree reaches the wire on the payload the page already polls "
                + "({0} chars inspected)", method.Length));

            // Derivation, not a second copy: the tree is built from the SAME rows the detail
            // view returns, after the bridge enriched them. `measure-the-deployed-system`.
            Assert(method.Contains("BridgeFleetView.RowsFromSnapshot(rows)"), string.Format(
                "P2-138: the tree is built from the rows the route already enriched, so the "
                + "summary and the detail cannot disagree ({0} chars inspected)", method.Length));

            // Negative control: the route must not re-derive a rank it was given.
            Assert(!Regex.IsMatch(method, @"Rank\s*=\s*\d"), string.Format(
                "P2-138: and the route never assigns a rank itself -- BridgeFleetView owns the "
                + "ONE ordering ({0} chars inspected)", method.Length));
        }

        /// <summary>
        /// The page half. Same weak gate as P1-125's and for the same reason -- ui/index.html
        /// is in no test build -- but THE DEFECT WAS EXACTLY THIS: the tree existed and the
        /// page read none of it. Measured before the fix: `data.fleet` appears 0 times.
        /// </summary>
        private static void TestP2138_ThePageRendersTheFleetTree(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-138: the page renders the fleet tree it is now sent (SOURCE gate)");

            string page = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "ui", "index.html"));
            Assert(File.Exists(page), "the served page is readable at " + page);
            if (!File.Exists(page)) return;

            string html = File.ReadAllText(page);

            Assert(Regex.IsMatch(html, @"data\.fleet"), string.Format(
                "P2-138: the page reads the fleet tree the route now sends ({0} chars read)",
                html.Length));
            Assert(Regex.IsMatch(html, @"\.children") && Regex.IsMatch(html, @"\.kind"),
                "P2-138: and it renders the tree as a TREE -- groups with their followers "
                + "nested, which is UI_REDESIGN_DESIGN.md section 4's left pane");
            Assert(Regex.IsMatch(html, @"\.badge"),
                "P2-138: and each node shows the badge section 4's row shows");

            // ⚠️ THIS ASSERTION FIRST DEMANDED THE PAGE READ `.rank`, WHICH IS THE OPPOSITE OF
            // THE DESIGN. Core sorts the tree -- that is what BridgeFleetView's 199 lines and
            // its mutation battery are FOR -- so a page that touched the rank at all would be
            // the second copy of an ordering this file exists to keep single. The page renders
            // the list in the order it was handed. Corrected before the loop was asked to
            // satisfy it, or it would have implemented a defect to turn a gate green.
            int fs = html.IndexOf("function renderFleetTree(");
            int fe = fs < 0 ? -1 : html.IndexOf("\nfunction ", fs + 10);
            string renderer = (fs >= 0 && fe > fs) ? html.Substring(fs, fe - fs) : "";
            Assert(renderer.Length > 100, string.Format(
                "P2-138: the fleet renderer is locatable for inspection ({0} chars read)",
                renderer.Length));
            Assert(renderer.Length > 100 && !renderer.Contains(".sort("), string.Format(
                "P2-138: and it renders the tree in the ORDER core gave it, sorting nothing "
                + "itself ({0} chars inspected)", renderer.Length));

            // The negative control, and the reason the class exists at all. §4's ordering is
            // the whole point and two incoming severity scales disagree about which end is bad.
            Assert(!Regex.IsMatch(html, @"rank\s*===?\s*\d"),
                "P2-138: and the page NEVER compares a rank to a literal -- the ONE ordering is "
                + "computed in a class the harness executes, not re-decided in JavaScript");
        }

        /// <summary>
        /// P2-126. The page dispatches the copier actions the backend already accepts.
        ///
        /// `POST /api/copier/config` accepts 14 actions; the page used to dispatch 2
        /// (`set {isEnabled}` and `set_group {isEnabled}`), so a relationship could be
        /// toggled and nothing else. This is a SOURCE gate over `ui/index.html` -- the
        /// page is in no test build -- and it proves the wiring exists, not that it
        /// behaves. The behaviour is the live check in the ticket.
        ///
        /// ⚠️ THE TWO RULES ARE ASSERTED AS NEGATIVES. A newly created relationship lands
        /// `IsEnabled: false`, and arming is a separate deliberate act -- so the create
        /// path must never send `armedForLive` and must send `isEnabled: false`. A create
        /// that armed as a side effect would pass every positive assertion.
        /// </summary>
        private static void TestP2126_ThePageDispatchesTheCopierActions(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-126: the page dispatches the copier actions (SOURCE gate)");

            string page = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "ui", "index.html"));
            Assert(File.Exists(page), "the served page is readable at " + page);
            if (!File.Exists(page)) return;
            string html = File.ReadAllText(page);

            // The actions the backend accepts (McpBridgeAddOn.cs:4373-4392) that the page
            // now dispatches. Each is asserted as the action NAME in a request body, so a
            // rename in the page breaks the gate.
            Assert(Regex.IsMatch(html, @"action:\s*""set""") && Regex.IsMatch(html, @"action:\s*""remove"""),
                "P2-126: the page can create and delete a relationship (set/remove)");
            Assert(Regex.IsMatch(html, @"action:\s*""set_group""")
                   && Regex.IsMatch(html, @"action:\s*""remove_group"""),
                "P2-126: and it can enable/disable and delete a group");
            Assert(Regex.IsMatch(html, @"action:\s*""add_follower_to_group""")
                   && Regex.IsMatch(html, @"action:\s*""remove_follower_from_group"""),
                "P2-126: and it can add and remove a follower from a group");
            Assert(Regex.IsMatch(html, @"action:\s*""set_mode"""),
                "P2-126: and it can set the copier's global mode");

            // P2-126. Arm/disarm is section 4 decision 3's first frequent action, and the
            // ONLY write that sends confirmLive:true -- arming is what turns a relationship
            // from "copies to simulation" into "places real orders", so it must be a separate
            // deliberate act with the engine's arming gate satisfied, never a silent no-op.
            Assert(html.Contains("data-do=\"arm\""),
                "P2-126: each row carries an arm/disarm button");
            Assert(Regex.IsMatch(html, @"armedForLive:\s*nextArmed"),
                "P2-126: the arm path sends armedForLive as a DIFF, not a whole row");
            Assert(Regex.IsMatch(html, @"confirmLive:\s*true"),
                "P2-126: and arming is the one write that sends confirmLive:true -- without it "
                + "the engine's ApplyArmingGate refuses the arm and the button is a silent no-op");

            // The two rules, as negatives. The create path must not arm and must not
            // enable: a new relationship lands disabled and not armed, and arming is a
            // separate deliberate act.
            int create = html.IndexOf("function createRelationship(", StringComparison.Ordinal);
            int createEnd = create < 0 ? -1 : html.IndexOf("\nfunction ", create + 10, StringComparison.Ordinal);
            string createFn = (create >= 0 && createEnd > create)
                ? StripJsLineComments(html.Substring(create, createEnd - create)) : "";
            Assert(createFn.Length > 100, string.Format(
                "P2-126: the create-relationship function is locatable for inspection ({0} chars read)",
                createFn.Length));
            Assert(createFn.Length > 100 && !createFn.Contains("armedForLive"),
                "P2-126: the create path NEVER sends armedForLive -- arming is a separate "
                + "deliberate act, never a side effect of creation");
            Assert(createFn.Length > 100 && createFn.Contains("isEnabled: false"),
                "P2-126: and a new relationship lands IsEnabled:false -- the two rules of "
                + "the ticket, asserted as the absence of the unsafe direction");

            // The inline row actions: ratio and sizing mode are section 4 decision 3's
            // frequent actions, so they live on the row, not in the inspector.
            Assert(html.Contains("data-do=\"delete\"") && html.Contains("data-do=\"edit\"")
                   && html.Contains("data-do=\"saveedit\""),
                "P2-126: each row carries delete and the inline ratio/sizing editor");
            Assert(html.Contains("quantityRatio") && html.Contains("sizingMode"),
                "P2-126: and the inline editor writes ratio and sizing mode as a DIFF, "
                + "naming only the fields the operator changed");
            Assert(html.Contains("maxPositionSize") && html.Contains("maxSlippageTicks")
                   && html.Contains("autoSymbolConversion"),
                "P2-126: and the inline editor writes the set-rarely scalars (max position, "
                + "slippage threshold, auto symbol conversion) as DIFF fields, so "
                + "PerTickerRatios and CustomSymbolMappings survive (P?-65's rule)");
            Assert(html.Contains("perTickerRatios") && html.Contains("customSymbolMappings"),
                "P2-126: and the inline editor writes the two dictionary fields (per-ticker "
                + "ratios, symbol mappings) as parsed KEY=VALUE diffs, so a ticker rule can be "
                + "added or removed without deleting the relationship");
        }

        /// <summary>
        /// P2-127 follow-up (2026-08-18). The dormant-account filter.
        ///
        /// The tree showed all 97 accounts because the caller passed every `Account.All` name
        /// into `BridgeFleetView.Build` and the class had no filter. The discriminator is
        /// `a.Connection == null` -- measured, not assumed: 91 of 97 accounts have no
        /// connection object on this login, and the 6 that do are exactly the 6 with a
        /// non-zero balance. The classification happens at the call site (it reads an NT8
        /// type); this class receives the RESULT as data and stays testable.
        ///
        /// ⚠️ THE INVARIABLE: an account with an open position, a working order or a live
        /// guard finding is NEVER dormant. And an account in a copy relationship is never
        /// flagged either -- the flag is only ever set on UNLINKED children.
        /// </summary>
        private static void TestP2127_DormantAccountsAreFlaggedNotHidden()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: dormant accounts are flagged as data, never hidden by the class");

            var tree = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5) },
                new List<string> { "Sim101", "SimCopy2", "Dormant1", "Dormant2" },
                new HashSet<string>(new[] { "Dormant1", "Dormant2" }, StringComparer.OrdinalIgnoreCase));

            var unlinked = Named(tree, BridgeFleetView.UnlinkedName);
            Assert(unlinked != null, "P2-127: precondition -- the unlinked node is present");
            if (unlinked == null) return;

            var d1 = Named(unlinked.Children, "Dormant1");
            var d2 = Named(unlinked.Children, "Dormant2");
            Assert(d1 != null && d1.Dormant && d2 != null && d2.Dormant,
                "P2-127: an account the caller classified as dormant carries Dormant=true");

            // The negative control: an account in a copy relationship is NEVER dormant,
            // even if the caller's set names it. The tree exists to show configured copies.
            var simCopy2 = Named(unlinked.Children, "SimCopy2");
            Assert(simCopy2 == null,
                "P2-127: SimCopy2 is in a relationship, so it is not under Unlinked at all");
            var sim101 = Named(tree, "Sim101");
            Assert(sim101 != null && sim101.Children.Count == 1
                   && !sim101.Children[0].Dormant,
                "P2-127: a follower in a copy relationship is never flagged dormant");

            // The default (no dormant set) leaves every account visible -- the filter is
            // opt-in at the call site, so a caller that does not classify changes nothing.
            var plain = BridgeFleetView.Build(
                new List<FleetCopierRow> { Row("Sim101", "SimCopy2", null, 5) },
                new List<string> { "Sim101", "SimCopy2", "Dormant1" });
            var plainUnlinked = Named(plain, BridgeFleetView.UnlinkedName);
            Assert(plainUnlinked != null && Named(plainUnlinked.Children, "Dormant1") != null
                   && !Named(plainUnlinked.Children, "Dormant1").Dormant,
                "P2-127: without a dormant set, no account is flagged -- the filter is opt-in");
        }

        /// <summary>
        /// P2-127 follow-up. The page renders the dormant filter: a stated count, a toggle,
        /// and a hidden set that is never silent. SOURCE gate over ui/index.html.
        /// </summary>
        private static void TestP2127_ThePageRendersTheDormantFilter(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the page renders the dormant filter (SOURCE gate)");

            string page = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "ui", "index.html"));
            Assert(File.Exists(page), "the served page is readable at " + page);
            if (!File.Exists(page)) return;
            string html = File.ReadAllText(page);

            Assert(html.Contains("data.dormantCount") && html.Contains("data.accountCount"),
                "P2-127: the page reads the dormant and total counts the route now sends");
            // Operator decision 2026-08-22: dormant accounts are blown evals, always hidden.
            // The toggle (dormtog/showDormant) is removed; the count is still stated.
            Assert(html.Contains("n.dormant"),
                "P2-127: and each node carries the dormant flag the route classified");
            Assert(html.Contains("blown evals"),
                "P2-127: and the filter is labelled as what the operator ruled -- dormant means blown");
        }

        // ==============================================================================
        // P2-127 slice 4: the EVENTS pane and §4 decision 4's SYSTEM ROW -- §4's last two
        // unbuilt regions.
        //
        // ⚠️ EVERY NUMBER BELOW IS MEASURED ON THE DEPLOYED BOX, 2026-08-17, and the design
        // follows the measurement rather than the shape of the code:
        //
        //   interventions.jsonl     43 766 928 bytes for ONE DAY
        //     last 3 MB             10 877 lines, 8 148 of them SUBSCRIBE (75%)
        //                           + 1 199 ORDER_UPDATE  ->  86% telemetry
        //                           97 INTERVENTION, 20 NAKED_POSITION, 63 ATM_STOP_ORDER_NOT_FOUND
        //                           84 ARMED_ON_START, 171 CONNECTION_CHANGE (same sentence each)
        //   guard summary           mode "shadow", isArmed true, unevaluatedRules EMPTY
        //   copier system           mode "live", isActing TRUE, severity "warn", conflicts 0
        //   connections             97 accounts: 91 on connection null, 6 on "TPT" all Connected
        //
        // Three of those four measurements changed a decision, and the fourth is P3-34 proved:
        // the guard enforces NOTHING while the copier is live and acting, in one process, at one
        // instant. A single "armed" light would have been a lie on this box today.
        // ==============================================================================

        /// <summary>
        /// P2-127 slice 4. 86% of the log is telemetry, so a pane that renders the tail verbatim
        /// shows an operator nothing but `SUBSCRIBE`. That is the §4.2 hazard by another route: this
        /// page exists so a bad state is seen WITHOUT BEING LOOKED FOR, and 8 148 subscribe lines
        /// hide 20 naked positions as effectively as a nav tab would.
        /// </summary>
        private static void TestP2127_TheEventsPaneDropsTheMeasuredTelemetry()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the events pane drops the measured 86% telemetry");

            var lines = new List<string>();
            for (int i = 0; i < 50; i++)
                lines.Add(EventLine("2026-08-17T23:0" + (i % 10) + ":00Z", "ACC", "SUBSCRIBE", "subscribed"));
            lines.Add(EventLine("2026-08-17T23:10:00Z", "ACC", "NAKED_POSITION", "a position with no stop"));

            var rows = BridgeEventsView.Build(lines, null, 60);

            Assert(rows.Count == 1 && rows[0].EventType == "NAKED_POSITION", string.Format(
                "P2-127: 50 SUBSCRIBE lines and one NAKED_POSITION yield ONE row (got {0}). "
                + "SUBSCRIBE was 75% of the measured tail on its own; a pane that shows it shows "
                + "nothing else.", rows.Count));

            Assert(BridgeEventsView.IsTelemetry("ORDER_UPDATE")
                   && BridgeEventsView.IsTelemetry("POSITION_UPDATE")
                   && BridgeEventsView.IsTelemetry("FSM_TRANSITION"),
                "P2-127: the other measured high-volume types are dropped too -- ORDER_UPDATE was "
                + "1 199 lines of the 10 877, and all three are facts the fleet tree and the "
                + "inspector already show as STATE, which is where a position's size belongs.");

            // ⚠️ THE NEGATIVE CONTROL FOR THE DENYLIST, and the direction is the decision.
            Assert(!BridgeEventsView.IsTelemetry("FSM_UNDERCOVERED"),
                "P2-127: FSM_UNDERCOVERED is NOT telemetry even though FSM_TRANSITION is. A "
                + "transition is the machine working; undercovered is the machine reporting a "
                + "position with less protection than it should have. They differ by one word.");
            Assert(!BridgeEventsView.IsTelemetry("SOMETHING_A_FUTURE_FIX_ADDS"),
                "P2-127: an UNKNOWN event type is NOT dropped. This is the load-bearing direction: "
                + "an allowlist would hide the events added by the most recent fix, which is exactly "
                + "when nobody remembers to register them -- an alarm wired to a surface that will "
                + "not show it.");
        }

        /// <summary>
        /// P2-127 slice 4. A denylist alone is not enough: `ARMED_ON_START` appeared 84 times in the
        /// measured tail and `CONNECTION_CHANGE` 171, none of it telemetry and all of it the same
        /// sentence. Eighty-four identical rows is an unusable pane by a different route.
        /// </summary>
        private static void TestP2127_RepeatedEventsCollapseAndKeepTheirCount()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: repeated events collapse, and the count survives");

            var lines = new List<string>();
            for (int i = 0; i < 84; i++)
                lines.Add(EventLine("2026-08-17T22:00:0" + (i % 10) + "Z", "SYSTEM", "ARMED_ON_START", "armed on start"));

            var rows = BridgeEventsView.Build(lines, null, 60);

            Assert(rows.Count == 1, string.Format(
                "P2-127: 84 identical ARMED_ON_START lines become ONE row (got {0}) -- the measured "
                + "count on this box, in one tail.", rows.Count));
            Assert(rows.Count == 1 && rows[0].Count == 84, string.Format(
                "P2-127: and the row CARRIES the 84. 'The connection cycled 84 times' is a "
                + "materially different fact from 'the connection cycled' -- each of those cycles "
                + "wiped the ATM bracket registry, which is P2-136 (got {0})",
                rows.Count == 1 ? rows[0].Count : -1));

            // ⚠️ WRITTEN FROM THE BATTERY: a mutant keeping the run's OLDEST timestamp survived,
            // because nothing here read the timestamp at all. It matters -- a condition still firing
            // now, stamped at the time it started, reads as historical and gets ignored.
            var run = new List<string>
            {
                EventLine("2026-08-17T20:00:00Z", "SYSTEM", "ARMED_ON_START", "armed"),
                EventLine("2026-08-17T21:00:00Z", "SYSTEM", "ARMED_ON_START", "armed"),
                EventLine("2026-08-17T22:30:00Z", "SYSTEM", "ARMED_ON_START", "armed"),
            };
            var collapsed = BridgeEventsView.Build(run, null, 60);
            Assert(collapsed.Count == 1 && collapsed[0].Utc == "2026-08-17T22:30:00Z", string.Format(
                "P2-127: a collapsed run carries its LATEST timestamp, not its first (got '{0}'). "
                + "'When did this last happen' is the question an operator asks of a repeating "
                + "event, and the earliest time answers a different one.",
                collapsed.Count == 1 ? collapsed[0].Utc : "(no single row)"));

            // ⚠️ CONSECUTIVE, NOT GLOBAL. A global collapse would merge this morning's naked
            // position with one from a minute ago and stamp a single row at the wrong time, which is
            // worse than either -- the timestamp is the actionable half.
            var interleaved = new List<string>
            {
                EventLine("2026-08-17T20:00:00Z", "ACC", "NAKED_POSITION", "morning"),
                EventLine("2026-08-17T21:00:00Z", "ACC", "INTERVENTION", "something happened"),
                EventLine("2026-08-17T22:00:00Z", "ACC", "NAKED_POSITION", "an hour ago"),
            };
            var split = BridgeEventsView.Build(interleaved, null, 60);
            Assert(split.Count == 3, string.Format(
                "P2-127: two runs of the same event separated by anything else stay TWO rows (got "
                + "{0} rows). Collapsing globally would report one event with count 2 at one of the "
                + "two times, and an operator cannot act on that.", split.Count));
        }

        /// <summary>
        /// P2-127 slice 4. THE LOAD-BEARING DECISION IN THE FILTER.
        ///
        /// §4 says "EVENTS -- filtered to selection", and a literal reading HIDES the worst events on
        /// the box the moment an operator clicks an account -- because `ATM_MONITOR_NO_DISPATCHER`,
        /// `ATM_BRACKET_RESTORE_FAILED` and every `ERROR` are logged with NO account. Clicking an
        /// account is the normal way to use this page. §4.2 killed top-level tabs over exactly this:
        /// nothing may put a bad state behind an interaction.
        /// </summary>
        private static void TestP2127_ASelectionNeverHidesABoxWideEvent()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: selecting an account never hides a box-wide event");

            var lines = new List<string>
            {
                EventLine("2026-08-17T23:00:00Z", "", "ATM_MONITOR_NO_DISPATCHER", "the sweep has no dispatcher"),
                EventLine("2026-08-17T23:01:00Z", "SYSTEM", "ERROR", "something threw"),
                EventLine("2026-08-17T23:02:00Z", "ACCOUNT_A", "INTERVENTION", "A was flattened"),
                EventLine("2026-08-17T23:03:00Z", "ACCOUNT_B", "INTERVENTION", "B was flattened"),
            };

            var forA = BridgeEventsView.Build(lines, "ACCOUNT_A", 60);
            var types = forA.Select(r => r.EventType).ToList();

            Assert(types.Contains("ATM_MONITOR_NO_DISPATCHER") && types.Contains("ERROR"), string.Format(
                "P2-127: both box-wide events survive a selection (got [{0}]). One is logged with an "
                + "EMPTY account by LogFromComponent and the other with the literal string 'SYSTEM' "
                + "by LogEvent -- both mean box-wide, and treating only one as such hides half of "
                + "them.", string.Join(", ", types)));

            Assert(!types.Contains("INTERVENTION") || forA.Count(r => r.EventType == "INTERVENTION") == 1,
                "P2-127: and the OTHER account's events are gone -- the pane is still filtered.");
            Assert(forA.All(r => r.IsSystemScoped || r.Account == "ACCOUNT_A"), string.Format(
                "P2-127: nothing from ACCOUNT_B leaks through (got [{0}])",
                string.Join(", ", forA.Select(r => r.Account))));

            Assert(BridgeEventsView.IsSystemScope("") && BridgeEventsView.IsSystemScope(null)
                   && BridgeEventsView.IsSystemScope("SYSTEM") && BridgeEventsView.IsSystemScope("system"),
                "P2-127: blank, null and 'SYSTEM' in either case all mean box-wide.");
            Assert(!BridgeEventsView.IsSystemScope("Sim101"),
                "P2-127: and a real account name does not -- a scope predicate that says yes to "
                + "everything makes the filter vacuous and every assertion above pass anyway.");
        }

        /// <summary>
        /// P2-127 slice 4. Newest first, and the CAP APPLIED LAST. Capping before collapsing would
        /// spend the whole budget on one run of 84 identical lines, which is the measured case.
        /// </summary>
        private static void TestP2127_TheNewestEventsSurviveTheCap()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the newest events survive the cap, and repeats do not eat it");

            var lines = new List<string>();
            for (int i = 0; i < 84; i++)
                lines.Add(EventLine("2026-08-17T22:00:00Z", "SYSTEM", "ARMED_ON_START", "armed"));
            lines.Add(EventLine("2026-08-17T23:00:00Z", "ACC", "NAKED_POSITION", "the thing that matters"));

            var rows = BridgeEventsView.Build(lines, null, 2);

            Assert(rows.Count == 2 && rows[0].EventType == "NAKED_POSITION", string.Format(
                "P2-127: the newest row is first and the 84-line run did not consume the cap (got "
                + "[{0}]). Capping before collapsing leaves a pane holding one repeated startup "
                + "message and nothing else.", string.Join(", ", rows.Select(r => r.EventType))));
        }

        /// <summary>
        /// P2-127 slice 4. An unrecognised event type must NOT rank clean.
        ///
        /// Same shape as the two battery survivors that produced
        /// `TestP2127_AnUnreadableRuleStateRanksWorstNotBest`: the events most worth surfacing are the
        /// ones a recent fix added, so ranking those routine puts the newest alarm at the bottom.
        /// </summary>
        private static void TestP2127_AnUnknownEventTypeDoesNotRankRoutine()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: an unknown event type does not rank routine");

            int unknown = BridgeEventsView.RankOfEventType("SOMETHING_A_FUTURE_FIX_ADDS");
            int routine = BridgeEventsView.RankOfEventType("INITIALIZE");
            int critical = BridgeEventsView.RankOfEventType("INTERVENTION");

            Assert(critical == BridgeFleetView.WorstRank, string.Format(
                "P2-127: an INTERVENTION -- the guard acting -- is the worst rank ({0}, got {1})",
                BridgeFleetView.WorstRank, critical));
            Assert(unknown < routine, string.Format(
                "P2-127: an UNKNOWN type ranks worse than a recognised routine one ({0} vs {1}). "
                + "Lower is worse on this page, and an event nobody has classified is not evidence "
                + "that nothing happened.", unknown, routine));

            // ⚠️ AND THE OTHER DIRECTION, which is what stops this crying wolf on every boot: the
            // ordinary startup lines must be routine, or 84 ARMED_ON_START rows all read as warnings.
            Assert(BridgeEventsView.RankOfEventType("ARMED_ON_START") == routine
                   && BridgeEventsView.RankOfEventType("ATM_BRACKET_RELEASED") == routine,
                "P2-127: ordinary startup and a released bracket are ROUTINE. Without a routine set "
                + "the loud default would make every boot look like a problem, and an alarm that is "
                + "always on is off.");

            Assert(BridgeEventsView.WorstRankOf(new List<EventRow>()) != BridgeFleetView.UnknownRank,
                "P2-127: an EMPTY pane is not UNKNOWN -- UnknownRank IS WorstRank, and a log that "
                + "was read and held nothing worth showing is not an unread log. This is the same "
                + "call CleanRank exists for on the rare tab.");
        }

        /// <summary>
        /// P2-127 slice 4, §4 decision 4. THREE CELLS, AND P3-34 IS WHY.
        ///
        /// §2.1's table: *"`P3-34` (open) | the copier is ENFORCING regardless of guard mode | a
        /// single 'armed' indicator would be a lie."* This test drives the state MEASURED on the
        /// deployed box on 2026-08-17 -- guard `shadow`/armed, copier `live`/acting -- and asserts the
        /// two cells disagree, which is the only way that box can be described truthfully.
        /// </summary>
        private static void TestP2127_TheSystemRowNeverMergesGuardAndCopier()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the system row never merges the guard and the copier");

            var cells = BridgeSystemRow.Build(
                Connections(("TPT", "Connected"), (null, "Disconnected")),
                new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                new JObject
                {
                    ["loaded"] = true, ["mode"] = "live", ["isActing"] = true,
                    ["severity"] = "warn", ["headline"] = "[ COPIER LIVE - NOTHING ENABLED ]",
                    ["detail"] = "Every relationship is switched off.", ["configConflicts"] = 0
                });

            Assert(cells.Count == 3, string.Format(
                "P2-127: always THREE cells (got {0}). An absent cell and an empty one read "
                + "identically to whatever renders them -- that was the sixteenth mutant of slice "
                + "1's battery and it survived a green suite.", cells.Count));
            Assert(cells[0].Id == BridgeSystemRow.FeedCell
                   && cells[1].Id == BridgeSystemRow.GuardCell
                   && cells[2].Id == BridgeSystemRow.CopierCell,
                "P2-127: in §4's order -- feed, guard, copier.");

            var guard = cells[1];
            var copier = cells[2];

            Assert(guard.Rank != copier.Rank, string.Format(
                "P2-127: on the state MEASURED on this box today -- guard 'shadow' and armed, copier "
                + "'live' and ACTING -- the two cells rank differently (guard {0}, copier {1}). A "
                + "single 'armed' light is a lie here whichever way it folds, which is P3-34.",
                guard.Rank, copier.Rank));

            Assert(guard.Badge.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0, string.Format(
                "P2-127: the guard cell says SHADOW -- evaluated, enforcing nothing. §2.1 calls this "
                + "correct and deliberate and adds 'but must be unmistakable' (got '{0}')",
                guard.Badge));
            Assert(copier.Badge.IndexOf("live", StringComparison.OrdinalIgnoreCase) >= 0
                   && copier.Badge.IndexOf("acting", StringComparison.OrdinalIgnoreCase) >= 0, string.Format(
                "P2-127: and the copier cell says live AND acting, in the same row (got '{0}'). "
                + "That is the fact an operator seeing one indicator would have got wrong.",
                copier.Badge));

            Assert(guard.Rank != BridgeInspectorTabs.CleanRank && guard.Rank != BridgeFleetView.WorstRank,
                string.Format("P2-127: shadow is neither CLEAN nor WORST (got {0}). Clean is how an "
                    + "operator comes to believe a limit protects them; worst is how the page cries "
                    + "wolf through the entire shadow-validation period this project is in.",
                    guard.Rank));
        }

        /// <summary>
        /// P2-127 slice 4. §2.1's rule, stated without qualification: **"CONFIGURED and not EVALUATED
        /// renders red, everywhere, always."** Four shipped defects were that state. It must bind over
        /// the mode, because a rule nothing reads cannot be rescued by arming.
        /// [[rank-refusal-reasons-by-what-binds]], [[configured-evaluated-enforcing]].
        /// </summary>
        private static void TestP2127_AnUnevaluatedRuleBeatsEveryOtherGuardState()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: an unevaluated rule outranks every other guard state");

            var copierSystem = new JObject { ["loaded"] = true, ["mode"] = "disabled", ["isActing"] = false, ["severity"] = "ok" };

            // Armed and LIVE -- the best a guard can be -- with one rule nothing reads.
            var withUnevaluated = BridgeSystemRow.Build(
                Connections(("TPT", "Connected")),
                new JObject
                {
                    ["mode"] = "live", ["isArmed"] = true,
                    ["unevaluatedRules"] = new JArray { "EnableNewsShield" }
                },
                copierSystem)[1];

            Assert(withUnevaluated.Rank == BridgeFleetView.WorstRank, string.Format(
                "P2-127: CONFIGURED-and-not-EVALUATED is the WORST rank even on an armed, LIVE guard "
                + "(got {0}). It is the most dangerous state this system can be in because the "
                + "config file reads as protection -- P1-77, P2-25 and the firm-mirror rules were "
                + "all this, shipped.", withUnevaluated.Rank));
            Assert(withUnevaluated.Badge.IndexOf("Not evaluated", StringComparison.OrdinalIgnoreCase) >= 0,
                string.Format("P2-127: and it says so, rather than reporting the mode (got '{0}')",
                    withUnevaluated.Badge));

            // The negative control: the same guard with an EMPTY unevaluated list must not be red,
            // or the cell is red always and says nothing. On the measured box the list IS empty.
            var clean = BridgeSystemRow.Build(
                Connections(("TPT", "Connected")),
                new JObject { ["mode"] = "live", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                copierSystem)[1];
            Assert(clean.Rank == BridgeInspectorTabs.CleanRank, string.Format(
                "P2-127: an armed LIVE guard with nothing unevaluated is CLEAN (got {0}). "
                + "`unevaluatedRules` was EMPTY on the measured box, so without this the cell would "
                + "be the same colour there as on a box in the worst state it can reach.",
                clean.Rank));

            var disarmed = BridgeSystemRow.Build(
                Connections(("TPT", "Connected")),
                new JObject { ["mode"] = "live", ["isArmed"] = false, ["unevaluatedRules"] = new JArray() },
                copierSystem)[1];
            Assert(disarmed.Rank < clean.Rank && disarmed.Rank > BridgeFleetView.WorstRank, string.Format(
                "P2-127: a DISARMED live guard is worse than an armed one and not as bad as an "
                + "unevaluated rule (got {0}); nothing can act, but nothing is misdescribed either.",
                disarmed.Rank));
        }

        /// <summary>
        /// P2-127 slice 4. THE FEED CELL IS WHERE FAIL-CLOSED WOULD LIE, and the numbers are measured:
        /// 97 accounts, 91 of them attached to NO connection and reported `Disconnected`, 6 on "TPT"
        /// all `Connected`. Those 91 are expired prop accounts. "Any disconnected account means the
        /// feed is down" paints this box permanently red -- the same defect that painted 95 of 97
        /// accounts as the worst thing on the fleet tree.
        /// [[an-inapplicable-state-is-not-unreadable]].
        /// </summary>
        private static void TestP2127_AnAccountOnNoConnectionIsNotAFeedFailure()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: 91 accounts on no connection are not a feed failure");

            var pairs = new List<(string, string)>();
            for (int i = 0; i < 91; i++) pairs.Add((null, "Disconnected"));
            for (int i = 0; i < 6; i++) pairs.Add(("TPT", "Connected"));

            var feed = BridgeSystemRow.Build(Connections(pairs.ToArray()),
                new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                new JObject { ["loaded"] = true, ["severity"] = "ok" })[0];

            Assert(feed.Rank == BridgeInspectorTabs.CleanRank, string.Format(
                "P2-127: the exact live shape -- 91 accounts on no connection, 6 on TPT all "
                + "connected -- reads CLEAN (got {0}). Counting the 91 makes this cell red on every "
                + "poll for the life of the box, and a cell that is always red is a cell nobody "
                + "reads.", feed.Rank));

            // A NAMED connection that is down IS a failure, or the cell can never go red.
            var down = BridgeSystemRow.Build(
                Connections((null, "Disconnected"), ("TPT", "Disconnected")),
                new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                new JObject { ["loaded"] = true, ["severity"] = "ok" })[0];
            Assert(down.Rank == BridgeFleetView.WorstRank, string.Format(
                "P2-127: a NAMED connection that is not connected IS the worst rank (got {0}). "
                + "Without this the cell has no input that makes it red, and a status that cannot "
                + "go red should not exist.", down.Rank));

            // ⚠️ WRITTEN FROM THE BATTERY. Every case above puts ONE account on each connection, so
            // `existing && connected` and `existing || connected` are indistinguishable and the
            // mutant swapping them survived. A connection is only up if EVERY account on it is: five
            // healthy accounts and one disconnected on the same connection is a partial feed, and
            // reporting it as fully up is the direction that hides it.
            var mixed = BridgeSystemRow.Build(
                Connections(("TPT", "Connected"), ("TPT", "Connected"), ("TPT", "Disconnected")),
                new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                new JObject { ["loaded"] = true, ["severity"] = "ok" })[0];
            Assert(mixed.Rank == BridgeFleetView.WorstRank, string.Format(
                "P2-127: one disconnected account TAINTS its connection -- two up and one down on "
                + "'TPT' is not a healthy feed (got {0}). With one account per connection in every "
                + "other case here, && and || are the same function and this is unobservable.",
                mixed.Rank));

            // And NO named connection at all is unknown, not clean -- this is the genuinely
            // unreadable case, which is the one fail-closed is for. §3: liveness is not optional
            // because a stalled feed and an idle one look identical.
            var none = BridgeSystemRow.Build(
                Connections((null, "Disconnected"), (null, "Disconnected")),
                new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() },
                new JObject { ["loaded"] = true, ["severity"] = "ok" })[0];
            Assert(none.Rank == BridgeFleetView.UnknownRank, string.Format(
                "P2-127: NO named connection is UNKNOWN, not clean (got {0}). Zero readable "
                + "connections means the cell cannot answer its own question, which is a different "
                + "fact from 'the accounts that could answer it are not attached to anything'.",
                none.Rank));
        }

        /// <summary>
        /// P2-127 slice 4. The copier cell reads the `system` object the copier's OWN producer emits
        /// and does not re-derive the verdict. `F-9`'s standing lesson is that a REPORTED state drifted
        /// from the ENFORCED state in both directions, and `P3-122` had to correct
        /// `CopierEnforcementView`'s refusal precedence once already -- a second reader of one question
        /// is how these drift. [[a-second-reader-of-the-same-state]].
        /// </summary>
        private static void TestP2127_TheCopierCellDefersToItsOwnProducer()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the copier cell defers to the copier's own producer");

            var guardSummary = new JObject { ["mode"] = "shadow", ["isArmed"] = true, ["unevaluatedRules"] = new JArray() };
            var conns = Connections(("TPT", "Connected"));

            var critical = BridgeSystemRow.Build(conns, guardSummary,
                new JObject { ["loaded"] = true, ["mode"] = "live", ["isActing"] = true, ["severity"] = "critical" })[2];
            var ok = BridgeSystemRow.Build(conns, guardSummary,
                new JObject { ["loaded"] = true, ["mode"] = "live", ["isActing"] = true, ["severity"] = "ok" })[2];

            Assert(critical.Rank == BridgeFleetView.RankOfSystemSeverity("critical")
                   && ok.Rank == BridgeFleetView.RankOfSystemSeverity("ok"),
                string.Format("P2-127: the rank comes from BridgeFleetView.RankOfSystemSeverity, "
                    + "which the fleet tree already speaks, rather than from a second scale here "
                    + "(critical {0}, ok {1})", critical.Rank, ok.Rank));
            Assert(critical.Rank < ok.Rank,
                "P2-127: and it points the right way -- the copier's severity scale runs the OTHER "
                + "way (Ok=0..Critical=3) and RankOfSystemSeverity inverts it. Passing the raw "
                + "integer through would sort a critical copier as the healthiest thing on the row.");

            // A config conflict IS the worst rank, whatever severity the copier reports.
            //
            // ⚠️ THIS ASSERTION USED TO BE VACUOUS AND THE BATTERY SAID SO. It read
            // `conflictOnCritical.Rank <= critical.Rank`, defending a claim that a conflict "never
            // improves" a rank -- but `WorstRank` is 0 and nothing is below it, so `0 <= 0` passed
            // under every implementation including the one it existed to reject. The code was
            // simplified rather than the test strengthened, because the property being defended does
            // not exist. [[a-green-that-can-never-be-red]].
            var conflictOnClean = BridgeSystemRow.Build(conns, guardSummary,
                new JObject { ["loaded"] = true, ["mode"] = "live", ["isActing"] = true, ["severity"] = "ok", ["configConflicts"] = 2 })[2];

            Assert(conflictOnClean.Rank == BridgeFleetView.WorstRank && ok.Rank != BridgeFleetView.WorstRank,
                string.Format("P2-127: a config conflict makes an OTHERWISE-OK copier the worst rank "
                    + "({0}), and the same copier without one is not ({1}) -- so the conflict is what "
                    + "moved it.", conflictOnClean.Rank, ok.Rank));
            Assert(conflictOnClean.Badge.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0,
                string.Format("P2-127: and the badge names it, so the rank and the text agree (got "
                    + "'{0}')", conflictOnClean.Badge));

            var notLoaded = BridgeSystemRow.Build(conns, guardSummary,
                new JObject { ["loaded"] = false, ["severity"] = "ok" })[2];
            Assert(notLoaded.Rank == BridgeFleetView.WorstRank,
                "P2-127: a copier whose config did not load is worst, whatever severity it reports "
                + "-- no relationship exists, so nothing would be mirrored.");

            var missing = BridgeSystemRow.Build(conns, guardSummary, null)[2];
            Assert(missing.Rank == BridgeFleetView.UnknownRank && !string.IsNullOrEmpty(missing.Badge),
                "P2-127: and an ABSENT system object is unknown with a non-blank badge -- a blank "
                + "badge over a bad state is the hiding hazard §4.2 killed top-level tabs for.");
        }

        /// <summary>
        /// P2-127 slice 4. The route serves both new regions and decides NEITHER.
        ///
        /// `McpBridgeAddOn.cs` is the one bridge source `BridgeTests.csproj` cannot compile, so
        /// anything it decides can be gated only by a regex -- which is not evidence. The rule is that
        /// it acquires and hands over. The negative controls are the substance of this test.
        /// </summary>
        private static void TestP2127_TheRouteServesEventsAndSystemAndDecidesNeither()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the route serves the events pane and system row, deciding neither (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));
            int s = code.IndexOf("case \"/api/ui/inspector\":", StringComparison.Ordinal);
            Assert(s >= 0, "P2-127: the /api/ui/inspector route exists at all");
            if (s < 0) return;

            int e = code.IndexOf("\n                case \"", s + 10);
            string handler = e > s ? code.Substring(s, e - s) : code.Substring(s);

            Assert(handler.Contains("BridgeEventsView.Build("), string.Format(
                "P2-127: the route SERVES the events pane -- a decision class with no route is "
                + "P2-138 repeated, and that one was written, tested, mutation-covered, deployed "
                + "and served to nobody ({0} chars inspected)", handler.Length));
            Assert(handler.Contains("BridgeSystemRow.Build("), string.Format(
                "P2-127: and the system row ({0} chars inspected)", handler.Length));
            Assert(handler.Contains("ReadInterventionTail("), string.Format(
                "P2-127: and it reads a bounded TAIL of the log, not the file. It measured 43 766 "
                + "928 bytes for one day, on a route the page polls every 5 seconds ({0} chars "
                + "inspected)", handler.Length));

            // ⚠️ NEGATIVE CONTROLS. Everything this file decides is untestable by construction.
            Assert(!Regex.IsMatch(handler, @"""(SUBSCRIBE|ORDER_UPDATE|INTERVENTION|NAKED_POSITION)"""),
                string.Format("P2-127: the route names NO event type. A second denylist here could "
                    + "drift from the class's, and the class's is the measured one ({0} chars "
                    + "inspected)", handler.Length));
            Assert(!Regex.IsMatch(handler, @"""(feed|guard|copier)""\s*[,\]\}]"), string.Format(
                "P2-127: and no system cell id, which would be a second definition of §4's row "
                + "({0} chars inspected)", handler.Length));
            Assert(!handler.Contains(".Reverse()") && !handler.Contains(".OrderByDescending("),
                string.Format("P2-127: and it does not order the events -- newest-first is the "
                    + "class's decision, taken after collapsing, and one copy of it is enough ({0} "
                    + "chars inspected)", handler.Length));

            // The tail reader itself: sharing WRITE is load-bearing, not habit.
            Assert(code.Contains("FileShare.ReadWrite"),
                "P2-127: the tail is opened with FileShare.ReadWrite. The guard's own five-second "
                + "sweep appends to this file, and a page poll that blocks or breaks the AUDIT "
                + "RECORD is a strictly worse trade than an empty pane.");
        }

        /// <summary>
        /// P2-127 slice 4. The page renders both regions and re-decides nothing.
        ///
        /// `ui/index.html` is in no test build and no mutation battery. `P2-138`'s whole content was
        /// view logic that existed and was served to nobody; the inverse -- a served payload no page
        /// renders -- is the same defect from the other end.
        /// </summary>
        private static void TestP2127_ThePageRendersEventsAndTheSystemRow(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P2-127: the page renders the events pane and the system row");

            string page = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile), "..", "ui", "index.html"));
            Assert(File.Exists(page), "the served page is readable at " + page);
            if (!File.Exists(page)) return;
            string html = File.ReadAllText(page);

            Assert(html.Contains("id=\"events\""), "P2-127: the events pane exists in the DOM.");
            Assert(html.Contains("id=\"systemrow\""), "P2-127: and §4 decision 4's system row.");
            // ⚠️ THE DEFINITION AND THE CALL, SEPARATELY, AND THE BATTERY IS WHY. A mutant renaming
            // `function renderSystemRow(` to `renderSystemRowNotWired(` SURVIVED, because the CALL in
            // loadTabs still contains the text `renderSystemRow(` -- so a single Contains() check
            // passed over a page that would throw on its first poll. Same family as an `if (false)`
            // that leaves the call text in place. The events pane's equivalent mutant died only by
            // luck: its region-locatable assertion below happens to look for the definition.
            Assert(html.Contains("function renderEvents(") && html.Contains("function renderSystemRow("),
                "P2-127: each renderer is DEFINED.");
            Assert(html.Contains("renderEvents(data)") && html.Contains("renderSystemRow(data)"),
                "P2-127: and each is CALLED with the payload. A definition nobody calls and a call "
                + "with no definition are different defects and one check cannot see both.");
            Assert(html.Contains("data.events") && html.Contains("data.system"),
                "P2-127: and both read the fields the route serves, rather than fetching separately "
                + "-- one payload cannot disagree with itself about which account is selected.");

            // ⚠️ THE NEGATIVE CONTROLS, same as slice 3's. Every ordering and every severity is
            // decided in a class the harness executes; JavaScript that compares a rank to a literal
            // is a second copy of an ordering, and two incoming scales disagree about which end is
            // bad. Ranks reach CSS as a class name, never as a comparison.
            // ⚠️ `"\nfunction "` -- these are TOP-LEVEL declarations, and the first draft looked for
            // an indented one. It found the next match thousands of characters away, so the "region"
            // spanned several unrelated functions and the negative controls below were asserted over
            // code they have no business inspecting. A gate must state the region it reads, and the
            // length is printed for exactly that reason. [[state-the-region-a-gate-inspects]].
            int eventsAt = html.IndexOf("function renderEvents(", StringComparison.Ordinal);
            int endAt = eventsAt < 0 ? -1 : html.IndexOf("\nfunction ", eventsAt + 10, StringComparison.Ordinal);
            // ⚠️ AND COMMENTS ARE STRIPPED, because this gate FAILED on its own subject's comment:
            // the renderer carries a note reading "No .sort() and no .reverse()", and searching for
            // `.sort(` found it. A comment ABOUT a token is not the token -- the same mistake was
            // made the same day in `check_no_dead_safety_machinery.py`, where an `if (false)` inside a
            // doc comment made the gate call a wired method dead.
            // [[a-comment-recording-a-defect-goes-stale]] is the neighbouring hazard; this one is
            // simpler and it bites the gate rather than the reader.
            string renderer = eventsAt >= 0 && endAt > eventsAt
                ? StripJsLineComments(html.Substring(eventsAt, endAt - eventsAt))
                : "";

            Assert(renderer.Length > 100, string.Format(
                "P2-127: the events renderer is locatable for inspection ({0} chars read)",
                renderer.Length));
            Assert(renderer.Length > 100 && !renderer.Contains(".sort(") && !renderer.Contains(".reverse("),
                string.Format("P2-127: and it renders in the ORDER the class gave it ({0} chars "
                    + "inspected). Newest-first is applied AFTER collapsing, so a re-sort in the "
                    + "page would undo the one decision that had to happen in a particular place.",
                    renderer.Length));
            Assert(renderer.Length > 100 && !Regex.IsMatch(renderer, @"rank\s*===?\s*\d"),
                "P2-127: and it never compares a rank to a literal.");
        }

        /// <summary>
        /// Whole-line `//` comments removed, and ONLY whole-line ones.
        ///
        /// Deliberately not a general JS comment stripper: a trailing `//` can sit inside a string, a
        /// regex or a URL, and a stripper that guessed would silently delete real code and make every
        /// negative control below pass over less and less text. A line whose first non-space
        /// characters are `//` cannot be any of those things.
        /// </summary>
        private static string StripJsLineComments(string js)
        {
            var kept = new List<string>();
            foreach (var line in js.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                kept.Add(line);
            }
            return string.Join("\n", kept);
        }

        /// <summary>One `interventions.jsonl` line in the shape the guard really writes.</summary>
        private static string EventLine(string utc, string account, string eventType, string message)
        {
            return new JObject
            {
                ["timestamp_utc"] = utc,
                ["account"] = account,
                ["eventType"] = eventType,
                ["mode"] = "shadow",
                ["isArmed"] = true,
                ["data"] = new JObject { ["message"] = message }
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>`/api/connections`' shape: one entry per account, `connection` null when attached to none.</summary>
        private static JObject Connections(params (string connection, string status)[] accounts)
        {
            var arr = new JArray();
            int n = 0;
            foreach (var a in accounts)
            {
                arr.Add(new JObject
                {
                    ["account"] = "ACC" + (n++),
                    ["connection"] = a.connection == null ? JValue.CreateNull() : new JValue(a.connection),
                    ["connectionStatus"] = a.status
                });
            }
            return new JObject { ["success"] = true, ["count"] = arr.Count, ["accounts"] = arr };
        }


        // ==================================================================================
        // P1-149. The configured contract cap, applied before the order exists.
        // ==================================================================================

        /// <summary>
        /// The defect exactly as measured: `Sizing.MaxContractsPerAccount: 10` live in the config,
        /// `sell 1000 MES` on a flat account, FILLED, -$1,213 of slippage on the fill alone. The
        /// only thing that ever refused an oversized order was the prop firm's own desk, at 60.
        /// </summary>
        private static void TestP1149_TheMeasuredDefectIsRefused()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: 1000 contracts against a cap of 10 is refused");

            var d = BridgeSizingGate.Evaluate(10, 1000, "sell", "Flat", 0, "TAKEPROFITPRO524207503", "MES SEP26");

            Assert(!d.Allowed,
                "P1-149: refused. This exact call filled on 2026-08-18 -- 100x the configured cap, "
                + "no guard event, no warning. MaxContractsPerAccount had four readers and the "
                + "component that places the order was not one of them.");

            Assert(d.Reason != null && d.Reason.Contains("10") && d.Reason.Contains("1000"),
                "P1-149: and the refusal names BOTH the cap and what was asked. 'Order refused' "
                + "alone makes an agent guess, and the caller here is an agent (got '"
                + (d.Reason ?? "null") + "')");

            Assert(d.ResultingQuantity == 1000,
                "P1-149: the resulting position is reported as 1000, so a test can check the "
                + "arithmetic separately from the verdict -- a gate that refuses for the wrong "
                + "reason still refuses (got " + d.ResultingQuantity + ")");
        }

        /// <summary>
        /// `GuardRules` reports `MaxContractsPerAccount &lt;= 0` as `Off("no per-account contract
        /// cap")`. This must agree: a cap the inventory shows as OFF while the bridge enforces it
        /// would be worse than either behaviour alone, because the operator would be reading a
        /// screen that says the opposite of what happens.
        /// </summary>
        private static void TestP1149_NoCapConfiguredAllowsEverything()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: no cap configured allows everything, matching GuardRules' Off()");

            Assert(BridgeSizingGate.Evaluate(0, 1000, "buy", "Flat", 0, "A", "MES").Allowed,
                "P1-149: cap 0 allows 1000 -- GuardRules calls 0 'no per-account contract cap', and "
                + "two readers of one setting must not disagree about whether it is on.");

            Assert(BridgeSizingGate.Evaluate(-1, 1000, "buy", "Flat", 0, "A", "MES").Allowed,
                "P1-149: and a negative cap is also off, not a cap of -1 that refuses everything.");
        }

        /// <summary>
        /// ⚠️ THE LOAD-BEARING TEST. If you are long 50 against a cap of 10, a `Sell 50` is the FIX,
        /// not the offence. `P1-106` is the same lesson one file over: a lockout refused the order
        /// that would CLOSE a position and trapped the operator inside the exact risk the rule
        /// existed to limit. A cap that refuses exits is a cap that manufactures the state it bans.
        /// </summary>
        private static void TestP1149_ACapNeverRefusesTheOrderThatClosesThePosition()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: a cap NEVER refuses the order that closes an over-cap position");

            var flatten = BridgeSizingGate.Evaluate(10, 50, "sell", "Long", 50, "A", "MES");
            Assert(flatten.Allowed && flatten.ResultingQuantity == 0,
                "P1-149: long 50 against a cap of 10, Sell 50 is ALLOWED and leaves 0. Refusing it "
                + "would trap the operator in the exposure the cap exists to prevent -- P1-106 "
                + "verbatim, one file over (allowed=" + flatten.Allowed + ", leaves "
                + flatten.ResultingQuantity + ")");

            var partial = BridgeSizingGate.Evaluate(10, 30, "sell", "Long", 50, "A", "MES");
            Assert(partial.Allowed && partial.ResultingQuantity == 20,
                "P1-149: and a PARTIAL reduction is allowed too, even though it leaves 20 -- still "
                + "over the cap of 10. Requiring a reduction to reach compliance in one order would "
                + "refuse every scale-out (allowed=" + partial.Allowed + ", leaves "
                + partial.ResultingQuantity + ")");

            var shortSide = BridgeSizingGate.Evaluate(10, 50, "buy", "Short", 50, "A", "MES");
            Assert(shortSide.Allowed,
                "P1-149: and the same from the short side -- the rule is about direction against "
                + "the position, not about buy or sell.");
        }

        /// <summary>
        /// The reason the check is on the RESULTING position rather than the order quantity. The
        /// guard's reactive rule asks `pos.Quantity > limit`; if this asked only about the order,
        /// the two halves would measure different things and the reactive rule would flatten a
        /// position the pre-trade gate had just approved.
        /// </summary>
        private static void TestP1149_AnUnderCapOrderThatLeavesAnOverCapPositionIsRefused()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: an under-cap ORDER that leaves an over-cap POSITION is refused");

            var d = BridgeSizingGate.Evaluate(10, 5, "buy", "Long", 8, "A", "MES");

            Assert(!d.Allowed && d.ResultingQuantity == 13,
                "P1-149: long 8, cap 10, Buy 5 -- the order is 5, comfortably under the cap, and it "
                + "leaves 13. Checking the order quantity alone passes this, and then MAX_SIZE_BREACH "
                + "flattens all 13 within the audit interval, which reads to an operator as the "
                + "guard flattening a legal order (allowed=" + d.Allowed + ", leaves "
                + d.ResultingQuantity + ")");

            Assert(d.Reason != null && d.Reason.Contains("long 8"),
                "P1-149: and the refusal states the position it judged against, because '5 is too "
                + "many' is false on its face and would read as a bug (got '" + (d.Reason ?? "null") + "')");
        }

        /// <summary>
        /// An off-by-one here refuses an order that exactly meets the cap, which an operator reads
        /// as the cap being one lower than it says.
        /// </summary>
        private static void TestP1149_TheBoundaryIsInclusive()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: a position exactly AT the cap is allowed, one over is not");

            Assert(BridgeSizingGate.Evaluate(10, 10, "buy", "Flat", 0, "A", "MES").Allowed,
                "P1-149: flat, cap 10, Buy 10 is allowed -- the cap is a maximum, not a limit you "
                + "must stay under.");

            Assert(!BridgeSizingGate.Evaluate(10, 11, "buy", "Flat", 0, "A", "MES").Allowed,
                "P1-149: and 11 is refused. This pair is what pins the comparison operator; either "
                + "test alone passes under both <= and <.");

            Assert(BridgeSizingGate.Evaluate(10, 2, "buy", "Long", 8, "A", "MES").Allowed,
                "P1-149: and reaching the cap exactly by ADDING is allowed too (8 + 2 = 10).");
        }

        /// <summary>
        /// A `Sell 20` against a long 8 is an exit AND a new short 12 -- NT8 nets it into one order.
        /// It is not strictly reducing, so it is judged on what it leaves. This is the case
        /// `BridgeLockoutGate`'s quantity clamp exists for, and the refusal has to offer the exit or
        /// it looks like the trap the previous test bans.
        /// </summary>
        private static void TestP1149_AReversalIsJudgedOnWhatItLeavesAndOffersTheExit()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: a reversal is judged on what it leaves, and the refusal offers the exit");

            var d = BridgeSizingGate.Evaluate(10, 20, "sell", "Long", 8, "A", "MES");

            Assert(!d.Allowed && d.ResultingQuantity == 12,
                "P1-149: long 8, Sell 20 nets to short 12, over the cap of 10. Admitting it because "
                + "'it reduces the long' opens 12 contracts of fresh risk -- the same arithmetic "
                + "P1-106's clamp exists for (allowed=" + d.Allowed + ", leaves " + d.ResultingQuantity + ")");

            Assert(d.Reason != null && d.Reason.Contains("18"),
                "P1-149: and it names the largest sell that WOULD be accepted -- 8 to flatten plus "
                + "10 of new short. Without it the operator cannot tell a size refusal from the "
                + "exit-trap this gate promises never to be (got '" + (d.Reason ?? "null") + "')");

            var exit = BridgeSizingGate.Evaluate(10, 8, "sell", "Long", 8, "A", "MES");
            Assert(exit.Allowed,
                "P1-149: positive control -- the plain exit it just suggested is in fact allowed. A "
                + "remedy the gate would itself refuse is worse than no remedy.");
        }

        /// <summary>
        /// `Position.Quantity` is ABSOLUTE on NT8; the side is `MarketPosition`. `P0-96` is the
        /// copier reading the SIGN and DOUBLING a follower's short behind 1311 green tests. Nothing
        /// here may depend on a sign, so a negative arriving from a caller that got it wrong must
        /// still be read as a magnitude rather than flipping the arithmetic.
        /// </summary>
        private static void TestP1149_ThePositionQuantityIsReadAsAMagnitude()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: the position quantity is a magnitude, never a signed value");

            var signed = BridgeSizingGate.Evaluate(10, 5, "buy", "Long", -8, "A", "MES");
            Assert(!signed.Allowed && signed.ResultingQuantity == 13,
                "P1-149: a -8 is read as 8 held, so Buy 5 still leaves 13 and is still refused. "
                + "Arithmetic on the raw value gives -3, which is under the cap and would ADMIT the "
                + "order -- P0-96's shape, where a sign read as data doubled a real position "
                + "(allowed=" + signed.Allowed + ", leaves " + signed.ResultingQuantity + ")");
        }

        /// <summary>
        /// ⚠️ THE POINT OF THE WHOLE TICKET. `PlaceAtmOrder` was the path the defect was measured on,
        /// and fixing only it would leave `/api/order` and `/api/order/oco` exactly as open --
        /// [[a-second-reader-of-the-same-state]], this repo's most repeated shape, where a predicate
        /// learns a clause and the other readers never do.
        ///
        /// A SOURCE gate, because `McpBridgeAddOn.cs` is in no test build. Comments are stripped:
        /// three times in one session a text gate here matched a comment about its own subject.
        /// </summary>
        private static void TestP1149_EveryOrderPathConsultsTheGate()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: EVERY order path consults the sizing gate (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            int calls = 0;
            int at = code.IndexOf("BridgeSizingGate.Evaluate", StringComparison.Ordinal);
            while (at >= 0)
            {
                calls++;
                at = code.IndexOf("BridgeSizingGate.Evaluate", at + 1, StringComparison.Ordinal);
            }

            Assert(calls == 4,
                "P1-149: all FOUR order-placing paths consult the gate -- PlaceOrder, PlaceOcoOrder, "
                + "PlaceAtmOrder and MultiAccountOrchestrator. The orchestrator was found in the "
                + "2026-08-30 audit submitting to every account with NONE of the gates; the count "
                + "is asserted rather than 'at least one' precisely because the defect was found "
                + "on one path and fixing that path alone is the failure this repo keeps "
                + "repeating (found " + calls + ")");

            // ⚠️ MATCH THE DEFINITION, NOT ANY OCCURRENCE. The first draft searched for
            // " PlaceOrder(" and found the ROUTE TABLE -- `b => PlaceOrder(b)` -- so it inspected a
            // few characters of a switch statement and reported three real call sites as missing.
            // A region gate that does not say what it read is the shape this repo has shipped four
            // times. [[state-the-region-a-gate-inspects]].
            foreach (string method in new[] { "PlaceOrder", "PlaceOcoOrder", "PlaceAtmOrder" })
            {
                string signature = "private object " + method + "(string body)";
                int start = code.IndexOf(signature, StringComparison.Ordinal);
                Assert(start >= 0, "P1-149: " + method + "'s DEFINITION is found by its signature");
                if (start < 0) continue;

                // ⚠️ BRACE-MATCHED, and the first version of this was WRONG in the
                // direction that passes. It ran from one definition to the NEXT OF THE THREE, which
                // for PlaceOcoOrder is ~3000 lines and 119,756 characters of other methods -- so a
                // call anywhere in that span satisfied it, which is exactly the "one wired path
                // vouches for an unwired one" this test exists to prevent. The sliver guard caught
                // a region too SMALL and said nothing about one 25x too LARGE.
                int open = code.IndexOf('{', start);
                Assert(open > start, "P1-149: " + method + "'s body has an opening brace");
                if (open <= start) continue;

                int depth = 0, end = -1;
                for (int i = open; i < code.Length; i++)
                {
                    if (code[i] == '{') depth++;
                    else if (code[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; break; }
                    }
                }
                Assert(end > open, "P1-149: " + method + "'s body has a matching closing brace");
                if (end <= open) continue;

                string body = code.Substring(open, end - open);

                Assert(body.Length > 400 && body.Length < 20000,
                    "P1-149: the region for " + method + " is one method body -- " + body.Length
                    + " chars, inside a stated range. Too small makes the assertion below vacuous; "
                    + "too large lets a neighbouring method's call answer for this one. Both "
                    + "directions are bounded because only one of them was, and it was the wrong one.");

                Assert(body.Contains("BridgeSizingGate.Evaluate"),
                    "P1-149: " + method + "'s OWN " + body.Length + "-char body consults the gate.");

                // ⚠️ AND THE VERDICT IS CONSUMED. Asserting the CALL alone left a live mutant: wrap
                // the verdict test in `if (false)` and the call is still there for any scan to
                // find, while the order is placed regardless. That is an alarm wired to an output
                // nobody reads -- [[an-alarm-wired-to-a-dead-output]] -- and it survived the first
                // run of this battery. Assert DELIVERY, not presence.
                string verdict = method == "PlaceOrder" ? "sizing"
                    : method == "PlaceOcoOrder" ? "ocoSizing" : "atmSizing";
                Assert(body.Contains("if (!" + verdict + ".Allowed)"),
                    "P1-149: " + method + " TESTS the verdict it just computed (looking for `if (!"
                    + verdict + ".Allowed)`). A call whose result is never read refuses nothing.");
                Assert(body.Contains("return new { error = " + verdict + ".Reason };"),
                    "P1-149: and " + method + " RETURNS the refusal rather than logging it and "
                    + "carrying on. Reporting a refusal while placing the order anyway is the worst "
                    + "of the three possible behaviours, because the log says it was stopped.");
            }
        }

        /// <summary>
        /// The cap must come from the guard, not from a number typed into the bridge. Four readers
        /// of `MaxContractsPerAccount` already exist; a fifth that could drift from the config the
        /// operator edits is the defect, not the fix.
        /// </summary>
        private static void TestP1149_TheBridgeDoesNotCarryItsOwnCopyOfTheCap()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] P1-149: the cap is asked of the guard, not hardcoded in the bridge (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            Assert(code.Contains("RiskGuardAddOn.Instance.EffectiveMaxContracts"),
                "P1-149: the bridge asks the GUARD for the cap, so the operator's config is the one "
                + "source. A literal here would be a fifth reader of a number four things already "
                + "read, and the drift would be silent.");

            string gate = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(BridgeSourcePath()), "BridgeSizingGate.cs"));
            // ⚠️ THE FAILURE DIRECTION IS A JUDGEMENT, so it is asserted rather than assumed. With
            // the guard unloaded the bridge must keep working, so an absent guard means NO CAP, not
            // a cap of 1. A mutant returning 1 here survived the first run of the battery: nothing
            // covered the fallback, and "the bridge refuses every order when the guard is absent"
            // is a plausible-looking behaviour nobody would have questioned.
            Assert(code.Contains("if (RiskGuardAddOn.Instance == null) return 0;"),
                "P1-149: an absent guard yields a cap of 0, which BridgeSizingGate reads as NO CAP. "
                + "Fail-OPEN is deliberate here: the bridge predates the guard and must run without "
                + "it, and a locally-invented cap would be a second source of truth for a number the "
                + "operator configures in exactly one place.");

            Assert(!gate.Contains("MaxContractsPerAccount ="),
                "P1-149: and the decision class assigns no default of its own -- it takes the cap as "
                + "a parameter. A fallback constant in here is the same second-reader defect wearing "
                + "a different hat.");
        }

        /// <summary>
        /// A backtest trade row must carry what win/loss attribution needs: the excursions,
        /// the per-leg quantities, the entry signal name, and the entry group key.
        ///
        /// WHY THESE AND NOT OTHERS. `Trade.MaeCurrency` / `MfeCurrency` are available ONLY
        /// from a backtest SystemPerformance -- this file's account-level sibling carries a
        /// note in the source saying exactly that -- and they were the fields that separate a
        /// bad ENTRY (the loser never went your way) from a bad EXIT (it did, and was given
        /// back). No P&L column distinguishes those, so the analysis was impossible rather
        /// than merely inconvenient.
        ///
        /// `entryGroup` is the sharper omission: ExtractBacktest ALREADY groups by an
        /// entry key to compute `entries`, `winEntries`, `avgWinEntry` and `maxLossEntry`,
        /// and never emitted the key. A consumer could read "entry win rate 41%" with no way
        /// to reproduce it, and no way to tell which rows are legs of one bracket -- which is
        /// the whole content of the leg convention.
        ///
        /// A SOURCE GATE, because the method reflects over a StrategyAnalyzerGridEntry and
        /// cannot be constructed off-platform. So it gets negative controls: a field name
        /// present in an anonymous object proves nothing if nothing reads the Trade for it.
        /// </summary>
        private static void TestBacktestTradesCarryExcursionsAndPerLegFields()
        {
            _testsRun++;
            Console.WriteLine("\n[TEST] backtest trade rows carry MAE/MFE, per-leg quantities and the entry key (SOURCE gate)");

            string code = StripComments(File.ReadAllText(BridgeSourcePath()));

            // Positive: the field is emitted AND its value is read off the Trade. Asserting
            // only the field name would pass for `maeCurrency = 0`, which is the shape that
            // makes an absent measurement look like a measured zero.
            foreach (var pair in new[] {
                new[] { "maeCurrency", "MaeCurrency" },
                new[] { "maePoints",   "MaePoints"   },
                new[] { "mfeCurrency", "MfeCurrency" },
                new[] { "mfePoints",   "MfePoints"   },
                new[] { "tradeNumber", "TradeNumber" },
                new[] { "commission",  "Commission"  } })
            {
                Assert(Regex.IsMatch(code, pair[0] + @"\s*=\s*GetP\(\s*tr\s*,\s*""" + pair[1] + @"""\s*\)"),
                    "the row emits `" + pair[0] + "` read from Trade." + pair[1]
                    + " -- not a literal, which would read as a measured value");
            }

            // PER-LEG quantities come off the EXECUTIONS, not off Trade.Quantity. On a
            // scale-out the two executions differ from the trade's quantity and from each
            // other, and that difference is why a queen/runner bracket is two rows.
            Assert(Regex.IsMatch(code, @"entryQuantity\s*=\s*GetP\(\s*entryExec\s*,\s*""Quantity""\s*\)"),
                "`entryQuantity` comes from the ENTRY execution, not from Trade.Quantity");
            Assert(Regex.IsMatch(code, @"exitQuantity\s*=\s*GetP\(\s*exitExec\s*,\s*""Quantity""\s*\)"),
                "and `exitQuantity` from the EXIT execution");

            // The entry group key: emitted, and built from the SAME two components the
            // aggregation above already groups by. A key computed differently from the
            // aggregate it is supposed to explain would be worse than none.
            Assert(code.Contains("entryGroup"),
                "the row emits `entryGroup`, the key that joins the legs of one bracket");
            Assert(Regex.IsMatch(code, @"entryGroup\s*=\s*GetP\(\s*entryExec\s*,\s*""Time""\s*\)\s*is\s+DateTime"),
                "and it is derived from the entry execution's Time, matching the `ekey` the "
                + "aggregation groups by -- a key built differently from the aggregate it "
                + "explains would be worse than no key");

            // The entry signal name is the JOIN KEY to the strategy's own decision log, which
            // is the only possible source of WHY a trade was taken: the criteria live in the
            // strategy and never reach the platform, so no bridge field can ever supply them.
            Assert(code.Contains("entryName"),
                "the row emits `entryName` -- the join key to the strategy's decision log");
            Assert(Regex.IsMatch(code, @"entryName\s*=\s*SafeToString\(\s*GetP\(\s*GetP\(\s*entryExec\s*,\s*""Order""\s*\)\s*,\s*""Name""\s*\)\s*\)"),
                "and it falls back to the entry ORDER's Name when Execution.Name is empty -- "
                + "the same two-step the exit-reason tally already needed, because "
                + "Execution.Name is empty on some fills");

            // NEGATIVE CONTROL. The pre-existing fields must survive: a projection rewritten
            // to add columns is exactly where a column silently disappears, and every
            // downstream consumer reads these by name.
            foreach (var kept in new[] { "instrument", "marketPosition", "quantity",
                                         "entryPrice", "exitPrice", "entryTime", "exitTime",
                                         "profitCurrency", "profitPoints", "exitName" })
                Assert(Regex.IsMatch(code, @"\b" + kept + @"\s*="),
                    "and the pre-existing field `" + kept + "` is still emitted");
        }

        public static int Main(string[] args)
        {
            return Run();
        }
    }
}
