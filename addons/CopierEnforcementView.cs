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
        /// Why it is not enforcing, in the operator's terms.
        ///
        /// A boolean `false` beside a relationship that looks correctly configured is the
        /// question, not the answer -- UI7's finding, that a refusal which does not say why
        /// is correct and useless. The mode is named LAST because it is the newest reason and
        /// the one an operator will not think to check.
        ///
        /// Returns null when it IS enforcing, so a caller can render the reason only when
        /// there is one.
        /// </summary>
        public static string NotEnforcingReason(
            bool isEnabled, bool armedForLive, bool copierModeIsActing, string copierMode)
        {
            if (IsEnforcing(isEnabled, armedForLive, copierModeIsActing))
                return null;

            if (!isEnabled)
                return "the relationship is disabled.";

            if (!armedForLive)
                return "the relationship is not ArmedForLive, so it copies to SIMULATION "
                     + "followers only -- a live follower is refused.";

            // Enabled and armed, and still not enforcing: the global mode is the only term
            // left, and it is the one that is invisible on the relationship itself.
            string named = string.IsNullOrWhiteSpace(copierMode) ? "(unset)" : copierMode;
            if (string.Equals(named, "shadow", StringComparison.OrdinalIgnoreCase))
                return "the relationship is enabled and armed, but the COPIER is in 'shadow': "
                     + "it logs the order it would have sent and submits nothing. This is a "
                     + "global switch, not a property of the relationship.";
            if (string.Equals(named, "disabled", StringComparison.OrdinalIgnoreCase))
                return "the relationship is enabled and armed, but the COPIER is 'disabled'. "
                     + "This is a global switch, not a property of the relationship.";

            return "the relationship is enabled and armed, but the copier mode '" + named
                 + "' is not one of live/shadow/disabled. Unrecognised modes do NOT trade -- "
                 + "the gate fails closed -- so fix the mode rather than the relationship.";
        }
    }
}
