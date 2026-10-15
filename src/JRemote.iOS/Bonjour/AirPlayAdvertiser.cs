using JRemote.Core.Logging;
using JRemote.iOS.AirPlay;
using Makaretu.Dns;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JRemote.iOS.Bonjour;

/// <summary>
/// Fully-compliant Apple AirPlay & RAOP DNS-SD / mDNS service advertiser using Makaretu.Dns.
/// Selects physical Wi-Fi/LAN interfaces and ignores virtual/Hyper-V/TAP adapters.
/// </summary>
public class AirPlayAdvertiser : IAsyncDisposable
{
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _airPlayProfile;
    private ServiceProfile? _raopProfile;
    private bool _isAdvertising;

    public AirPlaySecurity Security { get; }
    public string ServerName { get; private set; }
    public int AirPlayPort { get; }

    public AirPlayAdvertiser(AirPlaySecurity? security = null, string serverName = "JRemote-PC", int airPlayPort = 7000)
    {
        Security = security ?? new AirPlaySecurity();
        ServerName = string.IsNullOrWhiteSpace(serverName) ? "JRemote-PC" : serverName;
        AirPlayPort = airPlayPort;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isAdvertising) return Task.CompletedTask;

        try
        {
            var (activeIps, macWithColons, macNoColons, nicName) = GetPrimaryNetworkDetails();

            AppLogger.Log($"[mDNS] Sử dụng card mạng: '{nicName}' | IP: {string.Join(", ", activeIps)} | MAC: {macWithColons}");

            _mdns = new MulticastService();
            _sd = new ServiceDiscovery(_mdns);

            var pkHex = Security.EdPublicKeyHex;

            // 1. AirPlay Screen Mirroring Profile (_airplay._tcp) bound to real physical IPs
            _airPlayProfile = new ServiceProfile(ServerName, "_airplay._tcp", (ushort)AirPlayPort, activeIps);
            _airPlayProfile.AddProperty("deviceid", macWithColons);
            _airPlayProfile.AddProperty("features", "0x5A7FFEE6,0x1E");
            _airPlayProfile.AddProperty("model", "AppleTV3,2");
            _airPlayProfile.AddProperty("srcvers", "220.68");
            _airPlayProfile.AddProperty("flags", "0x44");
            _airPlayProfile.AddProperty("vv", "2");
            _airPlayProfile.AddProperty("pk", pkHex);
            _airPlayProfile.AddProperty("pi", "2e388006-13ba-4041-9a67-25dd4a43d536");

            // 2. RAOP Audio / Screen Streaming Profile (_raop._tcp) bound to real physical IPs
            var raopInstanceName = $"{macNoColons}@{ServerName}";
            _raopProfile = new ServiceProfile(raopInstanceName, "_raop._tcp", (ushort)AirPlayPort, activeIps);
            _raopProfile.AddProperty("ch", "2");
            _raopProfile.AddProperty("cn", "0,1,2,3");
            _raopProfile.AddProperty("da", "true");
            _raopProfile.AddProperty("et", "0,3,5");
            _raopProfile.AddProperty("vv", "2");
            _raopProfile.AddProperty("ft", "0x5A7FFEE6,0x1E");
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
            _raopProfile.AddProperty("pk", pkHex);

            // Start mDNS listener & advertise services
            _mdns.Start();
            _sd.Advertise(_airPlayProfile);
            _sd.Advertise(_raopProfile);

            // Announce to network immediately
            _sd.Announce(_airPlayProfile);
            _sd.Announce(_raopProfile);

            _isAdvertising = true;
            AppLogger.Log($"[mDNS] Đã phát sóng thành công dịch vụ AirPlay ('{ServerName}') và RAOP ('{raopInstanceName}') trên cổng {AirPlayPort}.");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[mDNS-LỖI] Không thể phát sóng mDNS: {ex.Message}");
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
            AppLogger.Log("[mDNS] Đã dừng phát sóng AirPlay.");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[mDNS] Lỗi dừng: {ex.Message}");
        }
        finally
        {
            _isAdvertising = false;
            _sd = null;
            _mdns = null;
        }

        return Task.CompletedTask;
    }

    public async Task UpdateServerNameAsync(string newServerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newServerName)) return;
        newServerName = newServerName.Trim();
        if (ServerName == newServerName && _isAdvertising) return;

        var wasAdvertising = _isAdvertising;
        if (wasAdvertising)
        {
            await StopAsync();
        }

        ServerName = newServerName;

        if (wasAdvertising)
        {
            await StartAsync(cancellationToken);
        }
    }

    private static (List<IPAddress> Ips, string MacWithColons, string MacNoColons, string NicName) GetPrimaryNetworkDetails()
    {
        var candidateNics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) &&
                        !n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                        !n.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Ưu tiên card Wi-Fi hoặc card có Gateway IPv4
        var primaryNic = candidateNics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                         ?? candidateNics.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                         ?? candidateNics.FirstOrDefault()
                         ?? NetworkInterface.GetAllNetworkInterfaces().First();

        var ipList = new List<IPAddress>();
        foreach (var unicast in primaryNic.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                !unicast.Address.ToString().StartsWith("169.254")) // Bỏ qua link-local APIPA
            {
                ipList.Add(unicast.Address);
            }
        }

        if (ipList.Count == 0)
        {
            ipList.Add(IPAddress.Loopback);
        }

        var macBytes = primaryNic.GetPhysicalAddress().GetAddressBytes();
        var macWithColons = macBytes.Length == 6
            ? string.Join(":", macBytes.Select(b => b.ToString("X2")))
            : "9C:2F:9D:A5:95:7B";

        var macNoColons = macBytes.Length == 6
            ? string.Concat(macBytes.Select(b => b.ToString("X2")))
            : "9C2F9DA5957B";

        return (ipList, macWithColons, macNoColons, primaryNic.Name);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
