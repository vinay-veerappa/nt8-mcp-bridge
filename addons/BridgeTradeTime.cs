// P2-178. `nt_extract_trades` stamped an EASTERN time with a literal `Z`, so every consumer
// read it as UTC and placed the operator's morning session in the middle of the night.
//
// MEASURED 2026-08-20: the tool reported an execution at `2026-08-20T09:57:51.985Z`;
// `interventions.jsonl` had the same order at `13:57:52Z`. Four hours apart, which is ET->UTC
// on that date. The failure is quiet and self-consistent -- every stamp is wrong by the same
// offset, so nothing INSIDE the output looks odd; it is visible only by joining to another
// source. `Z` means UTC. It is not decoration.
//
// ⚠️ THE OFFSET IS NOT A CONSTANT. It is 4h in August (EDT) and 5h in January (EST), a
// DST-dependent property of the DATE. Subtracting a fixed four hours -- the tempting "fix" the
// plan entry explicitly warns against -- would be right for half the year and an hour wrong for
// the other half. `TimeZoneInfo.ConvertTimeToUtc` is what makes the offset follow the date.
//
// Both bridge call sites read `Execution.Time`, whose wall-clock is in the NT8 display zone;
// the ExtractTrades macro-window test (10:50-11:10) already depends on that being ET. So the
// source zone here is Eastern, resolved the same way `RiskGuardAddOn._etZone` is.
//
// WHY ITS OWN FILE: `McpBridgeAddOn.cs` is in no test build (`P2-27`), so a conversion written
// inside it could only be checked by reading the text. This names no NT8 type, so
// `BridgeTests.csproj` compiles and EXECUTES it -- the two DST cases are asserted, not asserted
// about. [[test-doubles-are-not-evidence]].
using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeTradeTime
    {
        // The NT8 display zone the execution/order wall-clock times are in. ET on this box, and
        // resolved exactly as RiskGuardAddOn resolves its own `_etZone`, so a box running on
        // Linux (agent-loop CI, a hosted runner) still gets a real zone rather than throwing on
        // the Windows-only id.
        public static readonly TimeZoneInfo EasternZone = TimeZoneInfo.FindSystemTimeZoneById(
            Environment.OSVersion.Platform == PlatformID.Win32NT
                ? "Eastern Standard Time"
                : "America/New_York");

        /// <summary>
        /// Convert an NT8 wall-clock time (in <paramref name="sourceZone"/>) to a real UTC
        /// ISO-8601 string with a TRUE trailing Z. DST is handled by <paramref name="sourceZone"/>,
        /// so the offset follows the date rather than being a fixed subtraction.
        /// </summary>
        public static string ToUtcIso(DateTime wallClock, TimeZoneInfo sourceZone)
        {
            if (sourceZone == null) sourceZone = EasternZone;

            // `Execution.Time` arrives with Kind Unspecified (a bare wall-clock reading), but
            // `ConvertTimeToUtc` THROWS if a value's Kind contradicts the zone it is told to read
            // it in -- so pin the Kind to Unspecified before converting, whatever it came in as.
            DateTime wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

            // The spring-forward hour is a wall-clock reading that never happened (2:00-2:59 ET on
            // the second Sunday of March). `ConvertTimeToUtc` throws ArgumentException on it, and a
            // throw here would take out the WHOLE trade-list response for one impossible stamp.
            // Nudge it forward by the DST delta so it lands on a real instant just past the gap.
            if (sourceZone.IsInvalidTime(wall))
                wall = wall.Add(sourceZone.GetAdjustmentRules().Length > 0
                    ? sourceZone.GetAdjustmentRules()[0].DaylightDelta
                    : TimeSpan.FromHours(1));

            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(wall, sourceZone);
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        /// <summary>
        /// Convenience overload using the Eastern display zone, which is what both bridge call
        /// sites (`ExtractTrades` and `nt_capture_chart`'s fillTime) pass.
        /// </summary>
        public static string ToUtcIso(DateTime wallClock)
        {
            return ToUtcIso(wallClock, EasternZone);
        }
    }
}
