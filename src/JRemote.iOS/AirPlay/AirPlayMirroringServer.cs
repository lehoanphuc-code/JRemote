using JRemote.Core.Logging;
using JRemote.iOS.PlayFair;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JRemote.iOS.AirPlay;

/// <summary>
/// Server TCP lắng nghe trên cổng 7011 (dataPort) để tiếp nhận và giải mã luồng video H.264 AirPlay Mirroring từ iPhone
/// </summary>
public class AirPlayMirroringServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private IBufferedCipher? _aesCtrDecrypt;
    private bool _isCipherReady;

    private readonly byte[] _og = new byte[16];
    private int _nextDecryptCount;
    private byte[]? _spsPps;
    private long _receivedFrameCount;

    public int Port { get; }
    public event EventHandler<byte[]>? VideoFrameReceived;

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

    public void InitCipher(byte[] decryptedAesKey, byte[] ecdhShared, string streamConnectionId)
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

            _aesCtrDecrypt = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            _aesCtrDecrypt.Init(false, cipherParameters);
            _isCipherReady = true;

            AppLogger.Log($"[AirPlay Video] Đã khởi tạo AES-CTR Video Decryptor cho Stream ID: {streamConnectionId}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video-LỖI] Khởi tạo Cipher thất bại: {ex.Message}");
        }
    }

    private async Task AcceptVideoLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                AppLogger.Log($"[AirPlay Video] >>> Thiết bị đã kết nối kênh hình ảnh từ: {remoteEp}");

                _ = Task.Run(() => ReadVideoStreamAsync(client, cancellationToken), cancellationToken);
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

    private async Task ReadVideoStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var headerBuffer = new byte[128];

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
                    if (_isCipherReady)
                    {
                        DecryptVideoData(payload);
                    }

                    ProcessVideoPayload(payload);
                }
                else if (payloadType == 1)
                {
                    // SPS / PPS parameter set
                    ProcessSpsPps(payload);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video] Kết nối truyền hình ảnh đã đóng: {ex.Message}");
        }
    }

    private void DecryptVideoData(byte[] videoData)
    {
        if (_aesCtrDecrypt == null) return;

        if (_nextDecryptCount > 0)
        {
            for (int i = 0; i < _nextDecryptCount; i++)
            {
                videoData[i] = (byte)(videoData[i] ^ _og[(16 - _nextDecryptCount) + i]);
            }
        }

        int encryptLen = ((videoData.Length - _nextDecryptCount) / 16) * 16;
        if (encryptLen > 0)
        {
            _aesCtrDecrypt.ProcessBytes(videoData, _nextDecryptCount, encryptLen, videoData, _nextDecryptCount);
        }

        int restLen = (videoData.Length - _nextDecryptCount) % 16;
        int restStart = videoData.Length - restLen;
        _nextDecryptCount = 0;
        if (restLen > 0)
        {
            Array.Fill(_og, (byte)0);
            Array.Copy(videoData, restStart, _og, 0, restLen);
            _aesCtrDecrypt.ProcessBytes(_og, 0, 16, _og, 0);
            Array.Copy(_og, 0, videoData, restStart, restLen);
            _nextDecryptCount = 16 - restLen;
        }
    }

    private void ProcessSpsPps(byte[] payload)
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
                _spsPps = new byte[spsPpsLen];

                _spsPps[0] = 0; _spsPps[1] = 0; _spsPps[2] = 0; _spsPps[3] = 1;
                Array.Copy(h264.SequenceParameterSet, 0, _spsPps, 4, h264.LengthOfSps);

                int ppsOffset = h264.LengthOfSps + 4;
                _spsPps[ppsOffset] = 0; _spsPps[ppsOffset + 1] = 0; _spsPps[ppsOffset + 2] = 0; _spsPps[ppsOffset + 3] = 1;
                Array.Copy(h264.PictureParameterSet, 0, _spsPps, ppsOffset + 4, h264.LengthOfPps);

                AppLogger.Log($"[AirPlay Video] Đã nhận SPS ({h264.LengthOfSps}B) và PPS ({h264.LengthOfPps}B) từ iPhone.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AirPlay Video-LỖI] Xử lý SPS/PPS thất bại: {ex.Message}");
        }
    }

    private void ProcessVideoPayload(byte[] payload)
    {
        int naluSize = 0;
        while (naluSize + 4 <= payload.Length)
        {
            int ncLen = (payload[naluSize] << 24) | (payload[naluSize + 1] << 16) | (payload[naluSize + 2] << 8) | payload[naluSize + 3];
            if (ncLen <= 0 || naluSize + 4 + ncLen > payload.Length) break;

            payload[naluSize] = 0;
            payload[naluSize + 1] = 0;
            payload[naluSize + 2] = 0;
            payload[naluSize + 3] = 1;
            naluSize += 4 + ncLen;
        }

        byte[] finalFrame;
        int frameType = payload.Length > 4 ? (payload[4] & 0x1F) : 0;
        if (frameType == 5 && _spsPps != null && _spsPps.Length > 0)
        {
            finalFrame = new byte[_spsPps.Length + payload.Length];
            Buffer.BlockCopy(_spsPps, 0, finalFrame, 0, _spsPps.Length);
            Buffer.BlockCopy(payload, 0, finalFrame, _spsPps.Length, payload.Length);
        }
        else
        {
            finalFrame = payload;
        }

        _receivedFrameCount++;
        if (_receivedFrameCount == 1)
        {
            AppLogger.Log($"[AirPlay Video] === BẮT ĐẦU NHẬN LUỒNG VIDEO H.264 TỪ IPHONE ({payload.Length} bytes) ===");
        }
        else if (_receivedFrameCount % 180 == 0)
        {
            AppLogger.Log($"[AirPlay Video] Đã nhận {_receivedFrameCount} frames video thành công.");
        }

        VideoFrameReceived?.Invoke(this, finalFrame);
    }

    public async ValueTask DisposeAsync()
    {
        _isRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        await Task.CompletedTask;
    }
}
