using JRemote.Core.Interfaces;
using JRemote.Core.Models;
using JRemote.iOS.AirPlay;
using JRemote.iOS.Bluetooth;
using JRemote.iOS.Devices;

namespace JRemote.iOS.Session;

public class IOSMirroringSession : IMirroringSession
{
    private readonly IOSDevice _device;
    private readonly BluetoothHidController _hidController;
    private readonly AirPlayReceiverServer? _airPlayServer;
    private readonly IVideoDecoder? _videoDecoder;
    private CancellationTokenSource? _sessionCts;
    private bool _isRunning;

    public IDevice Device => _device;
    public IInputController InputController => _hidController;
    public bool IsRunning => _isRunning;

    public event EventHandler<VideoFrame>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    public IOSMirroringSession(IOSDevice device, AirPlayReceiverServer? airPlayServer = null, IVideoDecoder? videoDecoder = null)
    {
        _device = device;
        _airPlayServer = airPlayServer;
        _videoDecoder = videoDecoder;
        _hidController = new BluetoothHidController();

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

        if (_airPlayServer != null)
        {
            _airPlayServer.VideoFrameReceived += OnServerVideoFrameReceived;
        }
    }

    private void OnServerVideoFrameReceived(object? sender, byte[] nalUnit)
    {
        if (!_isRunning) return;

        if (_videoDecoder != null)
        {
            _ = _videoDecoder.FeedNalUnitAsync(nalUnit, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        else
        {
            FrameReceived?.Invoke(this, new VideoFrame(0, 0, 0, nalUnit, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    public async Task StartAsync(MirroringConfig? config = null, CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        config ??= new MirroringConfig();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;
        _device.UpdateState(DeviceState.Mirroring);

        // Bắt đầu Bluetooth HID emulation cho chuột và phím
        _ = _hidController.StartEmulationAsync();
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _sessionCts?.Cancel();
        _hidController.Stop();
        if (_airPlayServer != null)
        {
            _airPlayServer.VideoFrameReceived -= OnServerVideoFrameReceived;
        }
        _videoDecoder?.Dispose();
        _device.UpdateState(DeviceState.Connected);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionCts?.Dispose();
    }
}
