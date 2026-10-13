using System.Net;
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

    public AirPlayReceiverServer(int port = 7000)
    {
        Port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _isRunning = true;

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _cts?.Cancel();
        _listener?.Stop();
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
            catch
            {
                // Accept error handling
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        var buffer = new byte[8192];
        var deviceName = "iPhone";

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

                var method = requestLine[0];
                var uri = requestLine[1];

                // Parse CSeq
                string cseq = "1";
                foreach (var line in lines)
                {
                    if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                    {
                        cseq = line.Substring(5).Trim();
                    }
                    if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    {
                        var ua = line.Substring(11).Trim();
                        if (ua.Contains("AirPlay")) deviceName = "Apple Device (" + clientIp + ")";
                    }
                }

                var response = BuildRtspResponse(method, cseq);
                var responseBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(responseBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
                {
                    ClientConnected?.Invoke(this, new AirPlayClientConnectedEventArgs
                    {
                        ClientIp = clientIp,
                        DeviceName = deviceName,
                        ClientSocket = client
                    });
                }
                else if (method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase))
                {
                    ClientDisconnected?.Invoke(this, clientIp);
                    break;
                }
            }
        }
        catch
        {
            ClientDisconnected?.Invoke(this, clientIp);
        }
    }

    private static string BuildRtspResponse(string method, string cseq)
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
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
