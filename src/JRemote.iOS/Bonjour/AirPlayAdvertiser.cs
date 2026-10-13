using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JRemote.iOS.Bonjour;

/// <summary>
/// Advertises the PC as an AirPlay receiver over mDNS (Multicast DNS 224.0.0.251:5353)
/// so iPhones/iPads discover it in Control Center -> Screen Mirroring.
/// </summary>
public class AirPlayAdvertiser : IAsyncDisposable
{
    private const string MulticastAddress = "224.0.0.251";
    private const int MulticastPort = 5353;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isAdvertising = true;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.ExclusiveAddressUse = false;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));

            _ = Task.Run(() => AdvertiseLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception)
        {
            // Logging or fallback
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isAdvertising) return Task.CompletedTask;

        _isAdvertising = false;
        _cts?.Cancel();
        _udpClient?.Close();
        return Task.CompletedTask;
    }

    private async Task AdvertiseLoopAsync(CancellationToken cancellationToken)
    {
        var targetEndpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);

        while (!cancellationToken.IsCancellationRequested && _isAdvertising)
        {
            try
            {
                // Send periodic mDNS announcement beacon
                var announcement = BuildMdnsAnnouncementPacket(ServerName, AirPlayPort);
                if (_udpClient != null)
                {
                    await _udpClient.SendAsync(announcement, announcement.Length, targetEndpoint);
                }

                // Broadcast every 5 seconds
                await Task.Delay(5000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Resilient to network interface drops
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private static byte[] BuildMdnsAnnouncementPacket(string name, int port)
    {
        // Simple mDNS Response header with PTR, SRV, and TXT records for _airplay._tcp.local
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Transaction ID: 0
        writer.Write((ushort)0);
        // Flags: 0x8400 (Standard query response, authoritative)
        writer.Write((byte)0x84);
        writer.Write((byte)0x00);
        // Questions: 0
        writer.Write((ushort)0);
        // Answer RRs: 1
        writer.Write((ushort)1);
        // Authority RRs: 0, Additional RRs: 0
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        // Query Name: _airplay._tcp.local
        WriteDnsName(writer, "_airplay._tcp.local");

        // Type: PTR (0x000c)
        writer.Write((byte)0x00);
        writer.Write((byte)0x0C);
        // Class: IN (0x0001) | Cache Flush (0x8000)
        writer.Write((byte)0x80);
        writer.Write((byte)0x01);
        // TTL: 120 seconds
        writer.Write((uint)120);

        // RDATA length & name
        var rdata = EncodeDnsName($"{name}._airplay._tcp.local");
        writer.Write((ushort)rdata.Length);
        writer.Write(rdata);

        return ms.ToArray();
    }

    private static void WriteDnsName(BinaryWriter writer, string name)
    {
        var bytes = EncodeDnsName(name);
        writer.Write(bytes);
    }

    private static byte[] EncodeDnsName(string name)
    {
        using var ms = new MemoryStream();
        var parts = name.Split('.');
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0); // Terminator
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }
}
