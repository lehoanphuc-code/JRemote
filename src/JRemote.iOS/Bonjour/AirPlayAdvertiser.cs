using Makaretu.Dns;
using System.Net.NetworkInformation;

namespace JRemote.iOS.Bonjour;

/// <summary>
/// Fully-compliant Apple AirPlay & RAOP DNS-SD / mDNS service advertiser using Makaretu.Dns.
/// Broadcasts PTR, SRV, TXT, and A records, and actively responds to iPhone mDNS queries.
/// </summary>
public class AirPlayAdvertiser : IAsyncDisposable
{
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _airPlayProfile;
    private ServiceProfile? _raopProfile;
    private bool _isAdvertising;

    public string ServerName { get; }
    public int AirPlayPort { get; }

    public AirPlayAdvertiser(string serverName = "JRemote-PC", int airPlayPort = 7000)
    {
        ServerName = serverName;
        AirPlayPort = airPlayPort;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isAdvertising) return Task.CompletedTask;

        try
        {
            var macWithColons = GetMacAddress(withColons: true);
            var macNoColons = GetMacAddress(withColons: false);

            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);

            // 1. AirPlay Screen Mirroring Profile (_airplay._tcp)
            _airPlayProfile = new ServiceProfile(ServerName, "_airplay._tcp", (ushort)AirPlayPort);
            _airPlayProfile.AddProperty("deviceid", macWithColons);
            _airPlayProfile.AddProperty("features", "0x5A7FFFF7,0x1E");
            _airPlayProfile.AddProperty("model", "AppleTV3,2");
            _airPlayProfile.AddProperty("srcvers", "220.68");
            _airPlayProfile.AddProperty("flags", "0x44");
            _airPlayProfile.AddProperty("vv", "2");
            _airPlayProfile.AddProperty("pk", "b07727d6f6cd5e08b58ed52252c1d680f404040d30024e88ce265f04e1b60700");
            _airPlayProfile.AddProperty("pi", "b07727d6-f6cd-5e08-b58e-d52252c1d680");

            // 2. RAOP Audio / Screen Streaming Profile (_raop._tcp)
            var raopInstanceName = $"{macNoColons}@{ServerName}";
            _raopProfile = new ServiceProfile(raopInstanceName, "_raop._tcp", (ushort)AirPlayPort);
            _raopProfile.AddProperty("ch", "2");
            _raopProfile.AddProperty("cn", "0,1,2,3");
            _raopProfile.AddProperty("da", "true");
            _raopProfile.AddProperty("et", "0,3,5");
            _raopProfile.AddProperty("vv", "2");
            _raopProfile.AddProperty("ft", "0x5A7FFFF7,0x1E");
            _raopProfile.AddProperty("am", "AppleTV3,2");
            _raopProfile.AddProperty("md", "0,1,2");
            _raopProfile.AddProperty("rhd", "5.6.0.0");
            _raopProfile.AddProperty("pw", "false");
            _raopProfile.AddProperty("sr", "44100");
            _raopProfile.AddProperty("ss", "16");
            _raopProfile.AddProperty("sv", "false");
            _raopProfile.AddProperty("tp", "UDP");
            _raopProfile.AddProperty("txtvers", "1");
            _raopProfile.AddProperty("sf", "0x4");
            _raopProfile.AddProperty("vs", "220.68");
            _raopProfile.AddProperty("vn", "65537");
            _raopProfile.AddProperty("pk", "b07727d6f6cd5e08b58ed52252c1d680f404040d30024e88ce265f04e1b60700");

            // Start mDNS listener & advertise services
            _mdns.Start();
            _sd.Advertise(_airPlayProfile);
            _sd.Advertise(_raopProfile);

            // Announce to network immediately
            _sd.Announce(_airPlayProfile);
            _sd.Announce(_raopProfile);

            _isAdvertising = true;
        }
        catch (Exception)
        {
            // Resilient fallback
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isAdvertising) return Task.CompletedTask;

        try
        {
            if (_sd != null)
            {
                if (_airPlayProfile != null) _sd.Unadvertise(_airPlayProfile);
                if (_raopProfile != null) _sd.Unadvertise(_raopProfile);
                _sd.Dispose();
            }

            _mdns?.Stop();
            _mdns?.Dispose();
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            _isAdvertising = false;
            _sd = null;
            _mdns = null;
        }

        return Task.CompletedTask;
    }

    private static string GetMacAddress(bool withColons = true)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                     n.GetPhysicalAddress().GetAddressBytes().Length == 6);

            if (nic != null)
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                return withColons
                    ? string.Join(":", bytes.Select(b => b.ToString("X2")))
                    : string.Concat(bytes.Select(b => b.ToString("X2")));
            }
        }
        catch
        {
            // Ignore
        }

        return withColons ? "00:11:22:33:44:55" : "001122334455";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
