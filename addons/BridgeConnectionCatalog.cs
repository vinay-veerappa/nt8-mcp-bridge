// F-17 extension, 2026-08-15. The box has EIGHT configured connections in
// Config.xml <ConnectOptions> (Apex, Kinetick EOD, LUCID, My Schwab, Playback,
// Simulated Data Feed, TPT, Tradeify) and the snapshot derived from Account.All
// shows only the two that carry accounts. `Connection.Connections` enumerates
// ZERO from this AddOn's HTTP thread, so the configured-but-instantiated-only
// brokers were invisible and unconnectable -- the "specify which broker" gap.
//
// This class turns Config.xml into a VISIBILITY catalog. Two hard rules, both
// load-bearing:
//
//  1. NO CREDENTIAL FIELD IS READ. Password, User, AccessToken, MdAccessToken
//     and the token blobs stay untouched in the XML. This component is reachable
//     over HTTP:7890, and re-materialising secrets inside it is precisely what
//     the deferred ConnectOptions-reconstruction route is not allowed to become.
//
//  2. It names no NinjaTrader type, so the test build (BridgeTests.csproj glob)
//     executes it -- the repo's P2-27 rule. The two named connect routes are
//     (a) account-derived targets, which already work once the call is
//     dispatched to the UI thread, and (b) the deferred credential route, which
//     is deliberately not implemented here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    public sealed class BridgeConfiguredConnection
    {
        public string Name;
        public string TypeName;    // e.g. "NinjaTrader.Cbi.TradovateOptions"
        public string Provider;    // e.g. "Provider31", "Simulator", "Playback"
        public string Mode;        // e.g. "Live", "Playback"
        public string AccountType; // e.g. "Simulation"
    }

    public static class BridgeConnectionCatalog
    {
        /// <summary>
        /// Parses Config.xml's <ConnectOptions> section into configured-connection rows.
        /// Empty on missing/blank/corrupt input -- the endpoint must never throw because
        /// its inventory file is unreadable; the account-derived snapshot is unchanged then.
        /// </summary>
        public static List<BridgeConfiguredConnection> Parse(string configXml)
        {
            var result = new List<BridgeConfiguredConnection>();
            if (string.IsNullOrWhiteSpace(configXml))
                return result;

            try
            {
                var doc = XDocument.Parse(configXml);
                var connectOptions = doc.Root?.Element("ConnectOptions");
                if (connectOptions == null)
                    return result;

                foreach (var el in connectOptions.Elements())
                {
                    var name = (string)el.Element("Name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var typeName = (string)el.Element("TypeName");
                    result.Add(new BridgeConfiguredConnection
                    {
                        Name = name.Trim(),
                        TypeName = string.IsNullOrWhiteSpace(typeName)
                            ? el.Name.LocalName
                            : typeName.Trim(),
                        Provider = ((string)el.Element("Provider") ?? "").Trim(),
                        Mode = ((string)el.Element("Mode") ?? "").Trim(),
                        AccountType = ((string)el.Element("AccountType") ?? "").Trim(),
                    });
                }
            }
            catch
            {
                // Corrupt XML yields an empty catalog, never a throw from the endpoint.
            }

            return result;
        }

        /// <summary>
        /// The configured connections that have NO live presence -- the account-derived
        /// names are the source of "present", so only these get a configured-only row.
        /// </summary>
        public static List<BridgeConfiguredConnection> Absent(
            IEnumerable<BridgeConfiguredConnection> catalog,
            IEnumerable<string> presentNames)
        {
            var present = new HashSet<string>(
                presentNames ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            return (catalog ?? Enumerable.Empty<BridgeConfiguredConnection>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !present.Contains(c.Name))
                .ToList();
        }
    }
}