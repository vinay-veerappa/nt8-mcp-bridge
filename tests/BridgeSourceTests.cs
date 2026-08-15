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
            int routed = Regex.Matches(code, @"ResolveOrRefuse\(").Count;
            Assert(routed == 8, string.Format(
                "and all EIGHT account-resolution sites route through the tested resolver "
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
            TestP2_115_TheHealthFlagCanBeFalse();
            TestP2_115_ADormantPlaybackConnectionIsNotAMarketFeed();
            TestP2_115_ALiveBrokerConnectionStillReportsTrue();
            TestP2_115_AnUnknownConnectionStateFailsClosed();
            TestP2_115_TheRouteNoLongerDerivesTheFlagFromTheAccountCount();
            TestF17_AnUnknownConnectionIsRefusedNamingTheRealOnes();
            TestF17_ABlankConnectionNameIsNotAWildcard();
            TestF17_DisconnectingIsRefusedWhileAnythingIsLive();
            TestF17_TheDisconnectRouteActsOnTheRefusalRatherThanComputingIt();
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

            // Harness self-check, mirroring the core suite's. A runner that silently skips
            // tests is worse than no runner, so the count is asserted rather than assumed.
            Console.WriteLine("\n[TEST] HARNESS: every declared test ran");
            const int declared = 57;
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

        public static int Main(string[] args)
        {
            return Run();
        }
    }
}
