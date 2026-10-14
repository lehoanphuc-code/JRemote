using JRemote.Core.Logging;
using JRemote.iOS.PlayFair;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JRemote.iOS.AirPlay;

public class AirPlayFrameEventArgs : EventArgs
{
    public required string ClientIp { get; init; }
    public required byte[] NalUnit { get; init; }
}

public class AirPlayClientStreamContext
{
    public string ClientIp { get; set; } = string.Empty;
    public IBufferedCipher? AesCtrDecrypt { get; set; }
    public bool IsCipherReady { get; set; }
    public byte[] Og { get; } = new byte[16];
    public int NextDecryptCount { get; set; }
    public byte[]? SpsPps { get; set; }
    public long ReceivedFrameCount { get; set; }
    public byte[]? CurrentKey { get; set; }
    public byte[]? CurrentIV { get; set; }
}

/// <summary>
/// Server TCP lắng nghe trên cổng 7011 (dataPort) để tiếp nhận và giải mã luồng video H.264 AirPlay Mirroring từ tối đa nhiều iPhone cùng lúc
/// </summary>
public class AirPlayMirroringServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private readonly ConcurrentDictionary<string, AirPlayClientStreamContext> _clientContexts = new();

    public int Port { get; }
    public event EventHandler<AirPlayFrameEventArgs>? VideoFrameReceived;

    public AirPlayMirroringServer(int port = 7011)
    {
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

            AppLogger.Log($"[AirPlay Video] Đang lắng nghe kênh hình ảnh (Mirroring Data) trên cổng {Port}...");
            _ = Task.Run(() => AcceptVideoLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video-LỖI] Không thể mở cổng {Port}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void InitCipher(string clientIp, byte[] decryptedAesKey, byte[] ecdhShared, string streamConnectionId)
    {
        try
        {
            byte[] eaesKey = Utilities.Hash(decryptedAesKey, ecdhShared);

            byte[] skey = Encoding.UTF8.GetBytes($"AirPlayStreamKey{streamConnectionId}");
            byte[] hash1 = Utilities.Hash(skey, Utilities.CopyOfRange(eaesKey, 0, 16));

            byte[] siv = Encoding.UTF8.GetBytes($"AirPlayStreamIV{streamConnectionId}");
            byte[] hash2 = Utilities.Hash(siv, Utilities.CopyOfRange(eaesKey, 0, 16));

            byte[] decryptAesKey = new byte[16];
            byte[] decryptAesIV = new byte[16];
            Array.Copy(hash1, 0, decryptAesKey, 0, 16);
            Array.Copy(hash2, 0, decryptAesIV, 0, 16);

            var keyParameter = ParameterUtilities.CreateKeyParameter("AES", decryptAesKey);
            var cipherParameters = new ParametersWithIV(keyParameter, decryptAesIV, 0, 16);

            var ctx = _clientContexts.GetOrAdd(clientIp, ip => new AirPlayClientStreamContext { ClientIp = ip });
            ctx.CurrentKey = decryptAesKey;
            ctx.CurrentIV = decryptAesIV;
            ctx.AesCtrDecrypt = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            ctx.AesCtrDecrypt.Init(false, cipherParameters);
            ctx.NextDecryptCount = 0;
            Array.Fill(ctx.Og, (byte)0);
            ctx.ReceivedFrameCount = 0;
            ctx.IsCipherReady = true;

            AppLogger.Log($"[AirPlay Video] Đã khởi tạo AES-CTR Video Decryptor cho Client: {clientIp} | Stream ID: '{streamConnectionId}' | Key: {Convert.ToHexString(decryptAesKey)} | IV: {Convert.ToHexString(decryptAesIV)}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video-LỖI] Khởi tạo Cipher cho {clientIp} thất bại: {ex.Message}");
        }
    }

    public bool IsCipherReady(string clientIp)
    {
        return _clientContexts.TryGetValue(clientIp, out var ctx) && ctx.IsCipherReady && ctx.AesCtrDecrypt != null;
    }

    public bool IsReceivingVideo(string clientIp)
    {
        return _clientContexts.TryGetValue(clientIp, out var ctx) && ctx.ReceivedFrameCount > 0;
    }

    public void ResetClientCipher(string clientIp)
    {
        _clientContexts.TryRemove(clientIp, out _);
    }

    private async Task AcceptVideoLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                AppLogger.Log($"[AirPlay Video] >>> Thiết bị đã kết nối kênh hình ảnh từ: {remoteEp} (IP: {clientIp})");

                // Khởi tạo mới trạng thái Cipher cho luồng video kết nối mới từ clientIp này
                var ctx = _clientContexts.GetOrAdd(clientIp, ip => new AirPlayClientStreamContext { ClientIp = ip });
                if (ctx.CurrentKey != null && ctx.CurrentIV != null)
                {
                    var keyParameter = ParameterUtilities.CreateKeyParameter("AES", ctx.CurrentKey);
                    var cipherParameters = new ParametersWithIV(keyParameter, ctx.CurrentIV, 0, 16);
                    ctx.AesCtrDecrypt = CipherUtilities.GetCipher("AES/CTR/NoPadding");
                    ctx.AesCtrDecrypt.Init(false, cipherParameters);
                    ctx.NextDecryptCount = 0;
                    Array.Fill(ctx.Og, (byte)0);
                    ctx.ReceivedFrameCount = 0;
                    ctx.IsCipherReady = true;
                }

                _ = Task.Run(() => ReadVideoStreamAsync(client, clientIp, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AirPlay Video-LỖI] Chấp nhận kết nối video thất bại: {ex.Message}");
            }
        }
    }

    private async Task ReadVideoStreamAsync(TcpClient client, string clientIp, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var headerBuffer = new byte[128];
        var ctx = _clientContexts.GetOrAdd(clientIp, ip => new AirPlayClientStreamContext { ClientIp = ip });

        try
        {
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                // Đọc 128 bytes header
                int headerRead = 0;
                while (headerRead < 128)
                {
                    int r = await stream.ReadAsync(headerBuffer.AsMemory(headerRead, 128 - headerRead), cancellationToken);
                    if (r == 0) return;
                    headerRead += r;
                }

                int payloadSize = BitConverter.ToInt32(headerBuffer, 0);
                short payloadType = (short)(BitConverter.ToInt16(headerBuffer, 4) & 0xFF);

                if (payloadSize <= 0 || payloadSize > 10 * 1024 * 1024)
                {
                    continue;
                }

                // Đọc payloadSize bytes dữ liệu
                var payload = new byte[payloadSize];
                int payloadRead = 0;
                while (payloadRead < payloadSize)
                {
                    int r = await stream.ReadAsync(payload.AsMemory(payloadRead, payloadSize - payloadRead), cancellationToken);
                    if (r == 0) return;
                    payloadRead += r;
                }

                if (payloadType == 0)
                {
                    // Frame H.264 video
                    if (ctx.IsCipherReady)
                    {
                        DecryptVideoData(ctx, payload);
                    }

                    ProcessVideoPayload(ctx, payload);
                }
                else if (payloadType == 1)
                {
                    // SPS / PPS parameter set
                    ProcessSpsPps(ctx, payload);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video] Kết nối truyền hình ảnh từ {clientIp} đã đóng: {ex.Message}");
        }
    }

    private void DecryptVideoData(AirPlayClientStreamContext ctx, byte[] videoData)
    {
        if (ctx.AesCtrDecrypt == null) return;

        if (ctx.ReceivedFrameCount < 3)
        {
            AppLogger.Log($"[AirPlay Decrypt {ctx.ClientIp}] Frame #{ctx.ReceivedFrameCount + 1} BEFORE ({videoData.Length}B): {Convert.ToHexString(videoData.AsSpan(0, Math.Min(16, videoData.Length)))}");
        }

        if (ctx.NextDecryptCount > 0)
        {
            for (int i = 0; i < ctx.NextDecryptCount; i++)
            {
                videoData[i] = (byte)(videoData[i] ^ ctx.Og[(16 - ctx.NextDecryptCount) + i]);
            }
        }

        int encryptLen = ((videoData.Length - ctx.NextDecryptCount) / 16) * 16;
        if (encryptLen > 0)
        {
            ctx.AesCtrDecrypt.ProcessBytes(videoData, ctx.NextDecryptCount, encryptLen, videoData, ctx.NextDecryptCount);
        }

        int restLen = (videoData.Length - ctx.NextDecryptCount) % 16;
        int restStart = videoData.Length - restLen;
        ctx.NextDecryptCount = 0;
        if (restLen > 0)
        {
            Array.Fill(ctx.Og, (byte)0);
            Array.Copy(videoData, restStart, ctx.Og, 0, restLen);
            ctx.AesCtrDecrypt.ProcessBytes(ctx.Og, 0, 16, ctx.Og, 0);
            Array.Copy(ctx.Og, 0, videoData, restStart, restLen);
            ctx.NextDecryptCount = 16 - restLen;
        }

        if (ctx.ReceivedFrameCount < 3)
        {
            AppLogger.Log($"[AirPlay Decrypt {ctx.ClientIp}] Frame #{ctx.ReceivedFrameCount + 1} AFTER  ({videoData.Length}B): {Convert.ToHexString(videoData.AsSpan(0, Math.Min(16, videoData.Length)))}");
        }
    }

    private void ProcessSpsPps(AirPlayClientStreamContext ctx, byte[] payload)
    {
        try
        {
            var h264 = new H264Codec
            {
                Version = payload[0],
                ProfileHigh = payload[1],
                Compatibility = payload[2],
                Level = payload[3],
                Reserved6AndNal = payload[4],
                Reserved3AndSps = payload[5],
                LengthOfSps = (short)(((payload[6] & 255) << 8) + (payload[7] & 255))
            };

            var sequence = new byte[h264.LengthOfSps];
            Array.Copy(payload, 8, sequence, 0, h264.LengthOfSps);
            h264.SequenceParameterSet = sequence;
            h264.NumberOfPps = payload[h264.LengthOfSps + 8];
            h264.LengthOfPps = (short)(((payload[h264.LengthOfSps + 9] & 2040) + payload[h264.LengthOfSps + 10]) & 255);

            var picture = new byte[h264.LengthOfPps];
            Array.Copy(payload, h264.LengthOfSps + 11, picture, 0, h264.LengthOfPps);
            h264.PictureParameterSet = picture;

            if (h264.LengthOfSps + h264.LengthOfPps < 102400)
            {
                var spsPpsLen = h264.LengthOfSps + h264.LengthOfPps + 8;
                ctx.SpsPps = new byte[spsPpsLen];

                ctx.SpsPps[0] = 0; ctx.SpsPps[1] = 0; ctx.SpsPps[2] = 0; ctx.SpsPps[3] = 1;
                Array.Copy(h264.SequenceParameterSet, 0, ctx.SpsPps, 4, h264.LengthOfSps);

                int ppsOffset = h264.LengthOfSps + 4;
                ctx.SpsPps[ppsOffset] = 0; ctx.SpsPps[ppsOffset + 1] = 0; ctx.SpsPps[ppsOffset + 2] = 0; ctx.SpsPps[ppsOffset + 3] = 1;
                Array.Copy(h264.PictureParameterSet, 0, ctx.SpsPps, ppsOffset + 4, h264.LengthOfPps);

                AppLogger.Log($"[AirPlay Video {ctx.ClientIp}] Đã nhận SPS ({h264.LengthOfSps}B) và PPS ({h264.LengthOfPps}B) từ iPhone.");
                // Chuyển tiếp ngay lập tức tham số cấu hình SPS/PPS tới Video Decoder của đúng client IP
                VideoFrameReceived?.Invoke(this, new AirPlayFrameEventArgs { ClientIp = ctx.ClientIp, NalUnit = ctx.SpsPps });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video-LỖI {ctx.ClientIp}] Xử lý SPS/PPS thất bại: {ex.Message}");
        }
    }

    private void ProcessVideoPayload(AirPlayClientStreamContext ctx, byte[] payload)
    {
        if (payload.Length < 5) return;

        bool containsIdr = false;
        int naluSize = 0;
        int nalCount = 0;

        if (ctx.ReceivedFrameCount < 5)
        {
            AppLogger.Log($"[AirPlay Video {ctx.ClientIp}] Frame #{ctx.ReceivedFrameCount + 1} Raw Payload ({payload.Length}B): {Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length)))}");
        }

        while (naluSize + 4 <= payload.Length)
        {
            int ncLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(naluSize, 4));
            if (ncLen <= 0 || naluSize + 4 + ncLen > payload.Length)
            {
                if (ctx.ReceivedFrameCount < 5 && nalCount == 0)
                {
                    AppLogger.Log($"[AirPlay Video-CẢNH BÁO {ctx.ClientIp}] Không đọc được NALU: ncLen={ncLen}, naluSize={naluSize}, payloadLen={payload.Length}");
                }
                break;
            }

            int nalType = payload[naluSize + 4] & 0x1F;
            if (nalType == 5)
            {
                containsIdr = true;
            }

            payload[naluSize] = 0;
            payload[naluSize + 1] = 0;
            payload[naluSize + 2] = 0;
            payload[naluSize + 3] = 1;
            naluSize += 4 + ncLen;
            nalCount++;
        }

        byte[] finalFrame;
        if (containsIdr && ctx.SpsPps != null && ctx.SpsPps.Length > 0)
        {
            finalFrame = new byte[ctx.SpsPps.Length + payload.Length];
            Buffer.BlockCopy(ctx.SpsPps, 0, finalFrame, 0, ctx.SpsPps.Length);
            Buffer.BlockCopy(payload, 0, finalFrame, ctx.SpsPps.Length, payload.Length);
        }
        else
        {
            finalFrame = payload;
        }

        ctx.ReceivedFrameCount++;
        if (ctx.ReceivedFrameCount == 1)
        {
            AppLogger.Log($"[AirPlay Video {ctx.ClientIp}] === BẮT ĐẦU NHẬN LUỒNG VIDEO H.264 TỪ IPHONE ({payload.Length} bytes) ===");
        }
        else if (ctx.ReceivedFrameCount % 180 == 0)
        {
            AppLogger.Log($"[AirPlay Video {ctx.ClientIp}] Đã nhận {ctx.ReceivedFrameCount} frames video thành công.");
        }

        VideoFrameReceived?.Invoke(this, new AirPlayFrameEventArgs { ClientIp = ctx.ClientIp, NalUnit = finalFrame });
    }

    public async ValueTask DisposeAsync()
    {
        _isRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _clientContexts.Clear();
        await Task.CompletedTask;
    }
}
