using AdvancedSharpAdbClient.Models;
using JRemote.Android.Adb;
using JRemote.Android.Devices;
using JRemote.Android.Input;
using JRemote.Core.Interfaces;
using JRemote.Core.Logging;
using JRemote.Core.Models;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace JRemote.Android.Session;

using CoreDeviceState = JRemote.Core.Models.DeviceState;

public class AndroidMirroringSession : IMirroringSession
{
    private readonly AndroidDevice _androidDevice;
    private readonly AndroidInputController _inputController;
    private readonly IVideoDecoder? _videoDecoder;

    private Process? _serverProcess;
    private TcpClient? _videoClient;
    private TcpClient? _controlClient;
    private CancellationTokenSource? _sessionCts;
    private bool _isRunning;
    private int _localPort;

    public IDevice Device => _androidDevice;
    public IInputController InputController => _inputController;
    public bool IsRunning => _isRunning;

    public event EventHandler<VideoFrame>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    public AndroidMirroringSession(AndroidDevice androidDevice, IVideoDecoder? videoDecoder = null)
    {
        _androidDevice = androidDevice;
        _videoDecoder = videoDecoder;
        _inputController = new AndroidInputController(
            androidDevice.AdbClient,
            androidDevice.DeviceData,
            SendScrcpyControlBytesAsync);

        if (_videoDecoder != null)
        {
            _videoDecoder.FrameDecoded += (s, frame) =>
            {
                if (_isRunning)
                {
                    FrameReceived?.Invoke(this, frame);
                }
            };
        }
    }

    public async Task StartAsync(MirroringConfig? config = null, CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        config ??= new MirroringConfig();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;
        _androidDevice.UpdateState(CoreDeviceState.Mirroring);

        try
        {
            var adbExe = AdbLocator.FindAdbPath();
            if (string.IsNullOrEmpty(adbExe) || !File.Exists(adbExe))
            {
                throw new FileNotFoundException("Không tìm thấy adb.exe để khởi chạy mirroring Android.");
            }

            var scrcpyJar = FindScrcpyJarPath();
            if (string.IsNullOrEmpty(scrcpyJar) || !File.Exists(scrcpyJar))
            {
                throw new FileNotFoundException("Không tìm thấy file bin/scrcpy/scrcpy-server.jar");
            }

            _localPort = GetAvailablePort(27183);
            var serial = _androidDevice.DeviceData.Serial;

            AppLogger.Log($"[Android Scrcpy] Chuẩn bị khởi chạy phiên phản chiếu cho {serial} trên cổng {_localPort}...");

            // 1. Luôn push scrcpy-server.jar lên thiết bị để đảm bảo file luôn tồn tại
            await ExecuteAdbCommandAsync(adbExe, $"-s {serial} push \"{scrcpyJar}\" /data/local/tmp/scrcpy-server.jar", _sessionCts.Token);

            // 2. Thiết lập forward port từ PC tới abstract socket 'scrcpy' trên Android
            await ExecuteAdbCommandAsync(adbExe, $"-s {serial} forward tcp:{_localPort} localabstract:scrcpy", _sessionCts.Token);

            // 3. Khởi chạy scrcpy-server trên Android bằng tiến trình app_process
            var maxRes = config.MaxResolution > 0 ? config.MaxResolution : 1920;
            var maxFps = config.MaxFps > 0 ? config.MaxFps : 60;
            var bitRate = config.BitrateBps > 0 ? config.BitrateBps : 8000000;

            var serverArgs = $"-s {serial} shell CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 2.4 log_level=info audio=false video=true control=true tunnel_forward=true send_frame_meta=false send_device_meta=false send_dummy_byte=false send_codec_meta=false max_size={maxRes} max_fps={maxFps} video_bit_rate={bitRate}";

            var psi = new ProcessStartInfo
            {
                FileName = adbExe,
                Arguments = serverArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _serverProcess = Process.Start(psi);
            if (_serverProcess != null)
            {
                _serverProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        AppLogger.Log($"[scrcpy-server {serial}] {e.Data}");
                    }
                };
                _serverProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        AppLogger.Log($"[scrcpy-server-ERR {serial}] {e.Data}");
                    }
                };
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }

            // 4. Kết nối TCP Socket 1 (Video Socket) với retry timeout 5 giây
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && !_sessionCts.Token.IsCancellationRequested)
            {
                try
                {
                    var testClient = new TcpClient();
                    await testClient.ConnectAsync("127.0.0.1", _localPort, _sessionCts.Token);
                    if (testClient.Connected)
                    {
                        _videoClient = testClient;
                        break;
                    }
                }
                catch
                {
                    await Task.Delay(200, _sessionCts.Token);
                }
            }

            if (_videoClient == null || !_videoClient.Connected)
            {
                throw new TimeoutException($"Không thể kết nối video socket tới cổng {_localPort} sau 5 giây.");
            }

            // 5. Kết nối TCP Socket 2 (Control Socket)
            try
            {
                _controlClient = new TcpClient();
                await _controlClient.ConnectAsync("127.0.0.1", _localPort, _sessionCts.Token);
                AppLogger.Log($"[Android Scrcpy] Đã kết nối thành công Kênh Hình Ảnh và Kênh Điều Khiển ({serial}).");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Android Scrcpy] Không kết nối được Control socket (sẽ dùng ADB shell input fallback): {ex.Message}");
                _controlClient?.Dispose();
                _controlClient = null;
            }

            // 6. Khởi chạy luồng đọc và giải mã luồng video H.264
            _ = Task.Run(() => ReadVideoStreamAsync(_videoClient, _sessionCts.Token), _sessionCts.Token);
        }
        catch (Exception ex)
        {
            _isRunning = false;
            _androidDevice.UpdateState(CoreDeviceState.Connected, ex.Message);
            AppLogger.Log($"[Android Scrcpy-LỖI] Khởi động phiên phản chiếu thất bại: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
            await CleanupAsync();
            throw;
        }
    }

    private async Task ReadVideoStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var buffer = new byte[65536];
        var parser = new H264AnnexBParser();
        long receivedNalCount = 0;

        AppLogger.Log($"[Android Stream] Bắt đầu tiếp nhận luồng video H.264 từ {_androidDevice.Info.Name}...");

        try
        {
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0) break;

                long pts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                parser.FeedData(buffer, 0, bytesRead, nalUnit =>
                {
                    receivedNalCount++;
                    if (receivedNalCount == 1)
                    {
                        AppLogger.Log($"[Android Stream] Đã nhận Frame video đầu tiên ({nalUnit.Length} bytes) từ {_androidDevice.Info.Name}!");
                    }

                    if (_videoDecoder != null)
                    {
                        _ = _videoDecoder.FeedNalUnitAsync(nalUnit, pts);
                    }
                    else
                    {
                        FrameReceived?.Invoke(this, new VideoFrame(0, 0, 0, nalUnit, pts));
                    }
                });
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Log($"[Android Stream] Luồng video kết thúc: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _sessionCts?.Cancel();
        await CleanupAsync();
        _videoDecoder?.Dispose();
        _androidDevice.UpdateState(CoreDeviceState.Connected);
    }

    private async Task CleanupAsync()
    {
        try { _videoClient?.Close(); } catch { }
        try { _controlClient?.Close(); } catch { }
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill();
            }
        }
        catch { }

        // Gỡ bỏ forward port
        if (_localPort > 0)
        {
            var adbExe = AdbLocator.FindAdbPath();
            if (!string.IsNullOrEmpty(adbExe))
            {
                await ExecuteAdbCommandAsync(adbExe, $"-s {_androidDevice.DeviceData.Serial} forward --remove tcp:{_localPort}", CancellationToken.None);
            }
        }
    }

    private async Task SendScrcpyControlBytesAsync(byte[] data)
    {
        if (_controlClient?.Connected == true)
        {
            try
            {
                var stream = _controlClient.GetStream();
                await stream.WriteAsync(data);
                await stream.FlushAsync();
            }
            catch
            {
                // Ignore transient control write error
            }
        }
    }

    private static string? FindScrcpyJarPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "scrcpy", "scrcpy-server.jar"),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", "scrcpy", "scrcpy-server.jar"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scrcpy-server.jar")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    private static int GetAvailablePort(int startingPort)
    {
        for (int p = startingPort; p < startingPort + 100; p++)
        {
            try
            {
                using var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch
            {
                // Port busy
            }
        }
        return startingPort;
    }

    private static async Task ExecuteAdbCommandAsync(string adbExe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbExe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionCts?.Dispose();
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        _serverProcess?.Dispose();
    }
}

/// <summary>
/// Parser tách các NAL Unit chuẩn H.264 Annex B (00 00 00 01 hoặc 00 00 01) từ luồng byte liên tục
/// </summary>
public class H264AnnexBParser
{
    private byte[] _buffer = new byte[256 * 1024];
    private int _bufferLength = 0;

    public void FeedData(byte[] data, int offset, int count, Action<byte[]> onNalUnit)
    {
        if (_bufferLength + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _bufferLength + count));
        }

        Buffer.BlockCopy(data, offset, _buffer, _bufferLength, count);
        _bufferLength += count;

        int searchStart = 0;
        int lastStartCode = -1;

        while (true)
        {
            int nextStartCode = FindStartCode(_buffer, searchStart, _bufferLength);
            if (nextStartCode < 0) break;

            if (lastStartCode >= 0)
            {
                int nalLength = nextStartCode - lastStartCode;
                if (nalLength > 0)
                {
                    byte[] nalUnit = new byte[nalLength];
                    Buffer.BlockCopy(_buffer, lastStartCode, nalUnit, 0, nalLength);
                    onNalUnit(nalUnit);
                }
            }

            lastStartCode = nextStartCode;
            searchStart = nextStartCode + 3;
        }

        if (lastStartCode >= 0)
        {
            int remaining = _bufferLength - lastStartCode;
            if (remaining > 0 && lastStartCode > 0)
            {
                Buffer.BlockCopy(_buffer, lastStartCode, _buffer, 0, remaining);
            }
            _bufferLength = remaining;
        }
    }

    private static int FindStartCode(byte[] buffer, int start, int end)
    {
        for (int i = start; i <= end - 3; i++)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                if (buffer[i + 2] == 1) return i;
                if (i <= end - 4 && buffer[i + 2] == 0 && buffer[i + 3] == 1) return i;
            }
        }
        return -1;
    }
}
