using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace JRemote.iOS.AirPlay;

public class AirPlayClientConnectedEventArgs : EventArgs
{
    public required string ClientIp { get; init; }
    public required string DeviceName { get; init; }
    public TcpClient ClientSocket { get; init; } = default!;
}

public class AirPlayReceiverServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public int Port { get; }
    public bool IsRunning => _isRunning;

    public event EventHandler<AirPlayClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<string>? ClientDisconnected;
    public event EventHandler<string>? LogMessage;

    public AirPlayReceiverServer(int port = 7000)
    {
        Port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _isRunning = true;

        LogMessage?.Invoke(this, $"AirPlay Server đang lắng nghe trên cổng {Port}...");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        LogMessage?.Invoke(this, "AirPlay Server đã dừng.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Lỗi chấp nhận kết nối: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        var buffer = new byte[16384];
        var deviceName = $"iPhone ({clientIp})";

        LogMessage?.Invoke(this, $"Thiết bị kết nối từ {clientIp}...");

        try
        {
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;

                var requestText = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var lines = requestText.Split("\r\n");
                if (lines.Length == 0) continue;

                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2) continue;

                var method = requestLine[0].Trim();
                var uri = requestLine[1].Trim();
                var protocol = requestLine.Length > 2 ? requestLine[2].Trim() : "HTTP/1.1";

                string cseq = "1";
                int contentLength = 0;

                foreach (var line in lines)
                {
                    if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                        cseq = line.Substring(5).Trim();
                    else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.Substring(15).Trim(), out contentLength);
                    else if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    {
                        var ua = line.Substring(11).Trim();
                        if (ua.Contains("AirPlay") || ua.Contains("iOS"))
                            deviceName = $"iPhone [{clientIp}]";
                    }
                }

                LogMessage?.Invoke(this, $"Nhận yêu cầu: {method} {uri}");

                byte[] responseBytes;

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && uri.StartsWith("/info", StringComparison.OrdinalIgnoreCase))
                {
                    responseBytes = BuildInfoPlistResponse();
                }
                else if (uri.StartsWith("/pair-verify", StringComparison.OrdinalIgnoreCase))
                {
                    responseBytes = BuildPairVerifyResponse(buffer, bytesRead);
                }
                else if (uri.StartsWith("/fp-setup", StringComparison.OrdinalIgnoreCase))
                {
                    responseBytes = BuildFpSetupResponse(buffer, bytesRead);
                }
                else if (protocol.StartsWith("RTSP", StringComparison.OrdinalIgnoreCase))
                {
                    responseBytes = BuildRtspResponse(method, cseq);
                }
                else
                {
                    // Generic HTTP 200 OK for other endpoints
                    responseBytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n");
                }

                await stream.WriteAsync(responseBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase) || uri.Contains("stream"))
                {
                    LogMessage?.Invoke(this, $"Bắt đầu phiên phản chiếu với {deviceName}!");
                    ClientConnected?.Invoke(this, new AirPlayClientConnectedEventArgs
                    {
                        ClientIp = clientIp,
                        DeviceName = deviceName,
                        ClientSocket = client
                    });
                }
                else if (method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke(this, $"{deviceName} ngắt kết nối phản chiếu.");
                    ClientDisconnected?.Invoke(this, clientIp);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, $"Đóng kết nối {clientIp}: {ex.Message}");
            ClientDisconnected?.Invoke(this, clientIp);
        }
    }

    private static byte[] BuildInfoPlistResponse()
    {
        var mac = GetMacAddress();
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>deviceid</key>
                <string>{mac}</string>
                <key>features</key>
                <integer>1483956343</integer>
                <key>model</key>
                <string>AppleTV3,2</string>
                <key>name</key>
                <string>JRemote-PC</string>
                <key>protovers</key>
                <string>1.1</string>
                <key>srcvers</key>
                <string>220.68</string>
                <key>vv</key>
                <integer>2</integer>
            </dict>
            </plist>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(plist);
        var headers = $"HTTP/1.1 200 OK\r\n" +
                      $"Content-Type: text/x-apple-plist+xml\r\n" +
                      $"Content-Length: {bodyBytes.Length}\r\n" +
                      $"Server: AirTunes/220.68\r\n" +
                      $"\r\n";

        var headerBytes = Encoding.UTF8.GetBytes(headers);
        var result = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, result, headerBytes.Length, bodyBytes.Length);
        return result;
    }

    private static byte[] BuildPairVerifyResponse(byte[] requestBuffer, int length)
    {
        // AirPlay Pair-Verify Stage 1 response requires 96 bytes:
        // [32 bytes: server ephemeral public key] + [64 bytes: signature]
        var payload = new byte[96];
        // Populate dummy non-zero Curve25519 public key / signature
        for (int i = 0; i < 32; i++) payload[i] = (byte)(i + 1);
        for (int i = 32; i < 96; i++) payload[i] = (byte)(0xAA);

        var headers = $"HTTP/1.1 200 OK\r\n" +
                      $"Content-Type: application/octet-stream\r\n" +
                      $"Content-Length: {payload.Length}\r\n" +
                      $"Server: AirTunes/220.68\r\n" +
                      $"\r\n";

        var headerBytes = Encoding.UTF8.GetBytes(headers);
        var result = new byte[headerBytes.Length + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(payload, 0, result, headerBytes.Length, payload.Length);
        return result;
    }

    private static byte[] BuildFpSetupResponse(byte[] requestBuffer, int length)
    {
        // FairPlay setup response: 16-byte token or 142-byte response
        var payload = new byte[16];
        for (int i = 0; i < 16; i++) payload[i] = (byte)i;

        var headers = $"HTTP/1.1 200 OK\r\n" +
                      $"Content-Type: application/octet-stream\r\n" +
                      $"Content-Length: {payload.Length}\r\n" +
                      $"Server: AirTunes/220.68\r\n" +
                      $"\r\n";

        var headerBytes = Encoding.UTF8.GetBytes(headers);
        var result = new byte[headerBytes.Length + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(payload, 0, result, headerBytes.Length, payload.Length);
        return result;
    }

    private static byte[] BuildRtspResponse(string method, string cseq)
    {
        var sb = new StringBuilder();
        sb.Append("RTSP/1.0 200 OK\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        sb.Append("Server: AirTunes/220.68\r\n");

        if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET\r\n");
        }
        else if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n");
            sb.Append("Session: 1\r\n");
        }

        sb.Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string GetMacAddress()
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
                return string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
        }
        catch { }

        return "00:11:22:33:44:55";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
