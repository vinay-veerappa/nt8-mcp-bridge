// P3-34, read surface. What the bridge REPORTS about a copier relationship, derived from
// what actually gates the copy.
//
// `GET /api/copier/config` answered `enforcing = rel.IsEnabled && rel.ArmedForLive`. That
// was true until the copier gained a global mode in core v1.15.0, and false the moment it
// did: a relationship can be enabled AND armed while the copier sits in `shadow`, in which
// case it enforces nothing and the page says it enforces.
//
// This is F-9's finding in a second place. There, a guard rule's REPORTED state had
// drifted from its ENFORCED state in both directions -- a rule called `Disabled` that the
// guard ran, and one called live that could not fire. The remedy was to derive the display
// FROM the enforcer rather than re-deriving it beside the enforcer, and that is what this
// file is: the copy path's gate, expressed once, consumed by the reporter.
//
// WHY THIS IS ITS OWN FILE, and not a private method in McpBridgeAddOn.cs: that file is in
// no test build (`P2-27`), so anything inside it can be pinned only by source-text regex,
// which is not evidence. This file names no NinjaTrader type -- it takes primitives -- so
// `tests/BridgeTests.csproj` compiles and EXECUTES it. Same trade as
// `BridgeAccountResolver.cs`, and section 5.26 records it as the cheap P2-27 step worth
// repeating.
//
// It deliberately does NOT decide what an acting mode is. `TradeCopierEngine.IsCopierActingMode`
// owns that, the caller passes the answer in, and there is exactly one definition. A second
// copy of that predicate here is how the report would drift from the gate again.
//
// `tools/deploy.py` globs `addons/*.cs`, so this file needs no registration to ship.
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class CopierEnforcementView
    {
        /// <summary>
        /// True only when a leader fill on this relationship would actually reach the broker.
        /// Every term is a gate the copy path really applies, and all three must hold.
        /// </summary>
        public static bool IsEnforcing(bool isEnabled, bool armedForLive, bool copierModeIsActing)
        {
            return isEnabled && armedForLive && copierModeIsActing;
        }

        /// <summary>
        /// Why it is not enforcing, in the operator's terms. Null when it IS enforcing, so a
        /// caller renders a reason only when there is one.
        ///
        /// Kept as the string-only face of <see cref="WhyNotEnforcing"/>, because two callers
        /// want only the sentence and a second ordering is the defect this file exists to
        /// avoid.
        /// </summary>
        public static string NotEnforcingReason(
            bool isEnabled, bool armedForLive, bool copierModeIsActing, string copierMode)
        {
            var refusal = WhyNotEnforcing(isEnabled, armedForLive, copierModeIsActing, copierMode);
            return refusal == null ? null : refusal.Sentence;
        }

        /// <summary>
        /// Why it is not enforcing, as a short LABEL for a table cell and the full SENTENCE
        /// for the operator. Null when it is enforcing.
        ///
        /// Two renderings, ONE ordering. The label is not a second decision -- a page that
        /// picked its own short form would be free to disagree with the sentence beside it,
        /// which is this file's whole subject at a smaller scale.
        ///
        /// ⚠️ P3-122 -- THE ORDERING IS THE DEFECT, AND IT WAS HERE. The mode used to be
        /// tested LAST, with the stated reason that it "is the newest reason and the one an
        /// operator will not think to check." That is right about which reason is most
        /// SURPRISING and wrong about which one BINDS: an enabled, unarmed relationship under
        /// a `shadow` copier was told it "copies to SIMULATION followers only", while the copy
        /// path blocks at TradeCopierEngine's COPY_BLOCKED_COPIER_SHADOW *before any follower
        /// is reached* -- so it copies to simulation followers too. A true-sounding sentence
        /// describing a behaviour that is not happening is worse than no sentence.
        ///
        /// RANK REFUSAL REASONS BY WHAT BINDS, NOT BY WHAT SURPRISES. Hence:
        ///
        ///   disabled  >  global mode  >  not armed
        ///
        /// `isEnabled` stays first because it is the only term that is BOTH binding and
        /// actionable on the relationship itself, and because its sentence claims no
        /// behaviour that a shadow copier contradicts. The mode moves above `armedForLive`
        /// because it is the wider gate: it stops every relationship, however armed.
        ///
        /// This is now the same precedence CopierStatusView.RelationshipLine uses for the WPF
        /// window (quarantine > disabled > mode > not armed; quarantine is not a term here,
        /// since a quarantined relationship arrives with its own reason text). The two
        /// surfaces previously disagreed, which is how P3-122 was found: by comparing the two
        /// readers of one question after building a third.
        /// </summary>
        public static CopierRefusal WhyNotEnforcing(
            bool isEnabled, bool armedForLive, bool copierModeIsActing, string copierMode)
        {
            if (IsEnforcing(isEnabled, armedForLive, copierModeIsActing))
                return null;

            if (!isEnabled)
                return new CopierRefusal("disabled", "the relationship is disabled.");

            // Enabled and not acting: the global mode binds, whatever the relationship says
            // about arming. The sentence states the arming rather than assuming it -- with the
            // mode tested first this branch is reached by unarmed relationships too, and the
            // old text asserted "enabled and armed" for all of them.
            if (!copierModeIsActing)
            {
                string named = string.IsNullOrWhiteSpace(copierMode) ? "(unset)" : copierMode;
                string armedClause = armedForLive ? " and armed for live" : "";

                if (string.Equals(named, "shadow", StringComparison.OrdinalIgnoreCase))
                    return new CopierRefusal("copier shadow",
                        "the relationship is enabled" + armedClause + ", but the COPIER is in "
                        + "'shadow': it logs the order it would have sent and submits nothing "
                        + "at all -- to a live follower or a simulated one alike. This is a "
                        + "global switch, not a property of the relationship.");

                if (string.Equals(named, "disabled", StringComparison.OrdinalIgnoreCase))
                    return new CopierRefusal("copier disabled",
                        "the relationship is enabled" + armedClause + ", but the COPIER is "
                        + "'disabled', so no order is submitted for any relationship. This is a "
                        + "global switch, not a property of the relationship.");

                return new CopierRefusal("copier mode '" + named + "'",
                    "the relationship is enabled" + armedClause + ", but the copier mode '"
                    + named + "' is not one of live/shadow/disabled. Unrecognised modes do NOT "
                    + "trade -- the gate fails closed -- so fix the mode rather than the "
                    + "relationship.");
            }

            return new CopierRefusal("sim only",
                "the relationship is not ArmedForLive, so it copies to SIMULATION "
                + "followers only -- a live follower is refused.");
        }

        // ── the system row's copier cell (P1-125) ────────────────────────────────────────
        //
        // The browser UI at /ui showed the GUARD's mode in its header and said nothing at all
        // about the copier's. That is worse than showing neither: an operator who has been
        // told about "mode shadow - armed" reasonably concludes the copier's state was covered
        // too, and a `disabled` copier -- submitting nothing, anywhere -- rendered exactly like
        // a working one. P1-121 fixed this for the WPF window; the browser page is the surface
        // the operator actually uses, and it was never touched.
        //
        // WHERE THE DECISION LIVES, AND WHY IT IS NOT HERE: the severity, the headline and the
        // detail are CopierStatusView.Describe's, in the core repo, folded out of the same
        // relationships and groups the page is about to render. This method does NOT re-derive
        // them. A second opinion about "is the copier copying?" is precisely P3-122 above, and
        // building one for a second surface, in the same file, in the same session, would be a
        // joke at my own expense.
        //
        // What is decided here is only what core cannot know: the wire shape, and the state
        // where there is no copier to ask.

        /// <summary>
        /// Wire name for a CopierStatusSeverity rank (Ok=0, Info=1, Warn=2, Critical=3).
        ///
        /// ⚠️ A NAME, NEVER A NUMBER, and that is load-bearing: the copier rows in the SAME
        /// payload carry `severity` from CopierSnapshotJson.SeverityRank, where **0 is worst**.
        /// Two numeric severities with opposite polarity in one JSON document is a trap for
        /// whoever writes the next consumer, so the system cell exposes a name and the page
        /// keys its colour off that.
        ///
        /// An unknown rank reads as "critical" rather than "ok" for the reason SeverityRank
        /// puts an unrecognised verdict at the top: an enum member added upstream and not
        /// mapped here is not evidence of health.
        /// </summary>
        public static string SeverityName(int rank)
        {
            switch (rank)
            {
                case 0: return "ok";
                case 1: return "info";
                case 2: return "warn";
                case 3: return "critical";
            }
            return "critical";
        }

        /// <summary>
        /// The copier third of the system row, from what the engine already decided.
        /// </summary>
        public static CopierSystemCell SystemCell(
            string copierMode, bool copierModeIsActing, int severityRank,
            string headline, string detail, int configConflicts)
        {
            return new CopierSystemCell
            {
                Loaded = true,
                Mode = string.IsNullOrWhiteSpace(copierMode) ? "(unset)" : copierMode,
                IsActing = copierModeIsActing,
                Severity = SeverityName(severityRank),
                Headline = headline,
                Detail = detail,
                ConfigConflicts = configConflicts
            };
        }

        /// <summary>
        /// The cell for a box where TradeCopierEngine.Instance is null.
        ///
        /// "The copier is loaded and mirrors nothing" and "there is no copier" are different
        /// answers and the page already distinguishes them for the rows; the header indicator
        /// must not collapse them into a reassuring blank. `IsActing` is false, so every
        /// caller that reads the cell for "can it copy?" gets the safe answer without having
        /// to know about this case.
        /// </summary>
        public static CopierSystemCell NotLoadedCell()
        {
            return new CopierSystemCell
            {
                Loaded = false,
                Mode = "(not loaded)",
                IsActing = false,
                Severity = "critical",
                Headline = "COPIER NOT LOADED",
                Detail = "The trade copier addon is not loaded in this NinjaTrader session, so "
                       + "nothing is copied and no relationship can be reported. This is not the "
                       + "same as a copier with no relationships.",
                ConfigConflicts = 0
            };
        }
    }

    /// <summary>One refusal, in two lengths. See WhyNotEnforcing.</summary>
    public class CopierRefusal
    {
        public CopierRefusal(string label, string sentence)
        {
            Label = label;
            Sentence = sentence;
        }

        /// <summary>Short enough for a table cell: "copier shadow", "sim only".</summary>
        public string Label { get; private set; }

        /// <summary>The full explanation, which is what the operator acts on.</summary>
        public string Sentence { get; private set; }
    }

    /// <summary>
    /// What the page shows about the copier as a whole, beside the guard's mode.
    /// Serialized straight onto /api/copier/snapshot as `system`.
    /// </summary>
    public class CopierSystemCell
    {
        public bool Loaded { get; set; }
        public string Mode { get; set; }
        public bool IsActing { get; set; }
        public string Severity { get; set; }
        public string Headline { get; set; }
        public string Detail { get; set; }
        public int ConfigConflicts { get; set; }
    }
}
