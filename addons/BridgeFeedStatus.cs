// P2-115: /api/health's feedConnected used to be Account.All.Count > 0.
// That expression could never be false on a running NT8 because Simulator accounts are always present.
// Measured live 2026-08-15: a dormant Playback connection with no replay running and no tradeable market
// still reported feedConnected: true while quotes were frozen eight days old and orders sat at Initialized.
// This class evaluates connection status and provider type so the flag tracks an actual live feed.

using System;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class BridgeFeedStatus
    {
        public static bool IsMarketDataConnected(string[] names, string[] providers, string[] statuses)
        {
            if (names == null || providers == null || statuses == null)
                return false;

            int length = names.Length;
            if (providers.Length < length)
                length = providers.Length;
            if (statuses.Length < length)
                length = statuses.Length;

            for (int i = 0; i < length; i++)
            {
                if (!IsConnected(statuses[i]))
                    continue;
                if (string.IsNullOrWhiteSpace(providers[i]))
                    continue;
                if (IsSimulated(providers[i]))
                    continue;

                return true;
            }

            return false;
        }

        private static bool IsConnected(string status)
        {
            return string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSimulated(string provider)
        {
            return string.Equals(provider, "Simulator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider, "Playback", StringComparison.OrdinalIgnoreCase);
        }
    }
}
