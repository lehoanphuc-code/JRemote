using JRemote.Core.Logging;
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
    private readonly AirPlaySecurity _security;

    public int Port { get; }
    public bool IsRunning => _isRunning;
    public AirPlaySecurity Security => _security;

    public event EventHandler<AirPlayClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<string>? ClientDisconnected;

    public AirPlayReceiverServer(AirPlaySecurity? security = null, int port = 7000)
    {
        _security = security ?? new AirPlaySecurity();
        Port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return Task.CompletedTask;

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            _isRunning = true;

            AppLogger.Log($"[AirPlay Server] Đang lắng nghe kết nối TCP trên cổng {Port}...");
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Server-LỖI] Không thể mở cổng {Port}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        AppLogger.Log("[AirPlay Server] Đã dừng server.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                AppLogger.Log($"[AirPlay TCP] >>> Thiết bị đã kết nối thành công từ: {remoteEp}");

                _ = Task.Run(() => HandleClientAsync(client, remoteEp, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AirPlay TCP-LỖI] Chấp nhận kết nối thất bại: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string remoteEp, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        var deviceName = $"iPhone [{clientIp}]";

        // Buffer tích lũy dữ liệu nhận từ socket
        var receiveBuffer = new byte[65536];
        int bufferCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                int bytesRead = await stream.ReadAsync(receiveBuffer.AsMemory(bufferCount, receiveBuffer.Length - bufferCount), cancellationToken);
                if (bytesRead == 0)
                {
                    AppLogger.Log($"[AirPlay TCP] <<< Thiết bị {remoteEp} đã ngắt kết nối.");
                    break;
                }
                bufferCount += bytesRead;

                // Xử lý các request có trong buffer (có thể nhiều request đến liên tiếp)
                while (bufferCount > 0)
                {
                    // 1. Tìm vị trí kết thúc Header (\r\n\r\n)
                    int headerEndIndex = FindHeaderEnd(receiveBuffer, bufferCount);
                    if (headerEndIndex < 0)
                    {
                        // Chưa nhận đủ header, tiếp tục đọc socket
                        break;
                    }

                    int headerLength = headerEndIndex + 4;
                    var headerText = Encoding.UTF8.GetString(receiveBuffer, 0, headerEndIndex);
                    var lines = headerText.Split("\r\n");
                    if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) break;

                    var requestLine = lines[0].Split(' ');
                    if (requestLine.Length < 2) break;

                    var method = requestLine[0].Trim();
                    var uri = requestLine[1].Trim();
                    var protocol = requestLine.Length > 2 ? requestLine[2].Trim() : "HTTP/1.1";

                    string cseq = "1";
                    int contentLength = 0;
                    string userAgent = "";
                    string contentType = "";

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                            cseq = line.Substring(5).Trim();
                        else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                        else if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                            userAgent = line.Substring(11).Trim();
                        else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                            contentType = line.Substring(13).Trim();
                    }

                    int totalRequestLength = headerLength + contentLength;
                    if (bufferCount < totalRequestLength)
                    {
                        // Chưa nhận đủ body, tiếp tục đọc socket
                        break;
                    }

                    // Trích xuất binary body
                    var bodyBytes = new byte[contentLength];
                    if (contentLength > 0)
                    {
                        Buffer.BlockCopy(receiveBuffer, headerLength, bodyBytes, 0, contentLength);
                    }

                    // Dịch chuyển buffer còn lại lên đầu
                    int remainingBytes = bufferCount - totalRequestLength;
                    if (remainingBytes > 0)
                    {
                        Buffer.BlockCopy(receiveBuffer, totalRequestLength, receiveBuffer, 0, remainingBytes);
                    }
                    bufferCount = remainingBytes;

                    AppLogger.Log($"[YÊU CẦU NHẬN] {method} {uri} (Giao thức: {protocol}, CSeq: {cseq}, Body: {contentLength} bytes, UA: {userAgent})");

                    // 2. Xử lý logic từng endpoint AirPlay
                    byte[] responseBytes;

                    if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && uri.StartsWith("/info", StringComparison.OrdinalIgnoreCase))
                    {
                        responseBytes = BuildInfoPlistResponse(protocol, cseq);
                        AppLogger.Log($"[PHẢN HỒI] 200 OK cho /info (Gửi XML Property List, {responseBytes.Length} bytes)");
                    }
                    else if (uri.Contains("pair-setup", StringComparison.OrdinalIgnoreCase))
                    {
                        var pubKey = _security.HandlePairSetup(bodyBytes);
                        responseBytes = BuildOctetStreamResponse(protocol, cseq, pubKey);
                        AppLogger.Log($"[PHẢN HỒI] 200 OK cho /pair-setup (Gửi 32 bytes Server Ed25519 Public Key)");
                    }
                    else if (uri.Contains("pair-verify", StringComparison.OrdinalIgnoreCase))
                    {
                        var verifyResp = _security.HandlePairVerify(bodyBytes);
                        responseBytes = BuildOctetStreamResponse(protocol, cseq, verifyResp ?? Array.Empty<byte>());
                        AppLogger.Log($"[PHẢN HỒI] 200 OK cho /pair-verify (Gửi {(verifyResp?.Length ?? 0)} bytes payload)");
                    }
                    else if (uri.Contains("fp-setup", StringComparison.OrdinalIgnoreCase))
                    {
                        var fpResp = _security.HandleFpSetup(bodyBytes);
                        responseBytes = BuildOctetStreamResponse(protocol, cseq, fpResp ?? Array.Empty<byte>());
                        AppLogger.Log($"[PHẢN HỒI] 200 OK cho /fp-setup (Gửi {(fpResp?.Length ?? 0)} bytes FairPlay payload)");
                    }
                    else if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        responseBytes = BuildRtspOptionsResponse(protocol, cseq);
                        AppLogger.Log($"[PHẢN HỒI] RTSP 200 OK cho OPTIONS (CSeq: {cseq})");
                    }
                    else if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
                    {
                        responseBytes = BuildRtspSetupResponse(protocol, cseq);
                        AppLogger.Log($"[AIRPLAY] === THIẾT BỊ BẮT ĐẦU PHIÊN PHẢN CHIẾU: {deviceName} ===");
                        ClientConnected?.Invoke(this, new AirPlayClientConnectedEventArgs
                        {
                            ClientIp = clientIp,
                            DeviceName = deviceName,
                            ClientSocket = client
                        });
                    }
                    else if (method.Equals("RECORD", StringComparison.OrdinalIgnoreCase))
                    {
                        responseBytes = BuildRtspRecordResponse(protocol, cseq);
                        AppLogger.Log($"[PHẢN HỒI] RTSP 200 OK cho RECORD (CSeq: {cseq})");
                    }
                    else if (method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase))
                    {
                        responseBytes = Encoding.UTF8.GetBytes($"{protocol} 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n\r\n");
                        await stream.WriteAsync(responseBytes, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        AppLogger.Log($"[AIRPLAY] Thiết bị yêu cầu TEARDOWN (Dừng phiên phản chiếu).");
                        ClientDisconnected?.Invoke(this, clientIp);
                        return;
                    }
                    else
                    {
                        responseBytes = Encoding.UTF8.GetBytes($"{protocol} 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n");
                        AppLogger.Log($"[PHẢN HỒI] {protocol} 200 OK mặc định cho {method} {uri}");
                    }

                    await stream.WriteAsync(responseBytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay TCP-LỖI] Kết nối {remoteEp} gặp sự cố: {ex.Message}");
            ClientDisconnected?.Invoke(this, clientIp);
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private byte[] BuildInfoPlistResponse(string protocol, string cseq)
    {
        var mac = GetMacAddress();
        var pkBase64 = Convert.ToBase64String(_security.EdPublicKey);

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>deviceID</key>
                <string>{mac}</string>
                <key>features</key>
                <integer>130367356646</integer>
                <key>model</key>
                <string>AppleTV3,2</string>
                <key>name</key>
                <string>JRemote-PC</string>
                <key>pi</key>
                <string>2e388006-13ba-4041-9a67-25dd4a43d536</string>
                <key>pk</key>
                <data>{pkBase64}</data>
                <key>protovers</key>
                <string>1.1</string>
                <key>srcvers</key>
                <string>220.68</string>
                <key>statusFlags</key>
                <integer>68</integer>
                <key>vv</key>
                <integer>2</integer>
                <key>keepAliveLowPower</key>
                <true/>
                <key>keepAliveSendStatsAsBody</key>
                <true/>
                <key>displays</key>
                <array>
                    <dict>
                        <key>uuid</key>
                        <string>e0ff8a27-6738-3d56-8a16-cc53aacee925</string>
                        <key>width</key>
                        <integer>1920</integer>
                        <key>height</key>
                        <integer>1080</integer>
                        <key>widthPixels</key>
                        <integer>1920</integer>
                        <key>heightPixels</key>
                        <integer>1080</integer>
                        <key>rotation</key>
                        <false/>
                        <key>refreshRate</key>
                        <real>0.016666666666666666</real>
                        <key>overscanned</key>
                        <true/>
                        <key>features</key>
                        <integer>14</integer>
                    </dict>
                </array>
            </dict>
            </plist>
            """;

        var bodyBytes = Encoding.UTF8.GetBytes(plist);
        var headers = $"{protocol} 200 OK\r\n" +
                      $"CSeq: {cseq}\r\n" +
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

    private static byte[] BuildOctetStreamResponse(string protocol, string cseq, byte[] payload)
    {
        var headers = $"{protocol} 200 OK\r\n" +
                      $"CSeq: {cseq}\r\n" +
                      $"Content-Type: application/octet-stream\r\n" +
                      $"Content-Length: {payload.Length}\r\n" +
                      $"Server: AirTunes/220.68\r\n" +
                      $"\r\n";

        var headerBytes = Encoding.UTF8.GetBytes(headers);
        var result = new byte[headerBytes.Length + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, result, headerBytes.Length, payload.Length);
        }
        return result;
    }

    private static byte[] BuildRtspOptionsResponse(string protocol, string cseq)
    {
        var msg = $"{protocol} 200 OK\r\n" +
                  $"CSeq: {cseq}\r\n" +
                  $"Server: AirTunes/220.68\r\n" +
                  $"Public: SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET\r\n" +
                  $"\r\n";
        return Encoding.UTF8.GetBytes(msg);
    }

    private static byte[] BuildRtspSetupResponse(string protocol, string cseq)
    {
        var msg = $"{protocol} 200 OK\r\n" +
                  $"CSeq: {cseq}\r\n" +
                  $"Server: AirTunes/220.68\r\n" +
                  $"Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n" +
                  $"Session: 1\r\n" +
                  $"\r\n";
        return Encoding.UTF8.GetBytes(msg);
    }

    private static byte[] BuildRtspRecordResponse(string protocol, string cseq)
    {
        var msg = $"{protocol} 200 OK\r\n" +
                  $"CSeq: {cseq}\r\n" +
                  $"Server: AirTunes/220.68\r\n" +
                  $"Audio-Latency: 11025\r\n" +
                  $"Audio-Jack-Status: connected; type=analog\r\n" +
                  $"\r\n";
        return Encoding.UTF8.GetBytes(msg);
    }

    private static string GetMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                     !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                                     !n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) &&
                                     n.GetPhysicalAddress().GetAddressBytes().Length == 6);

            if (nic != null)
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                return string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
        }
        catch { }

        return "9C:2F:9D:A5:95:7B";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
