using JRemote.Core.Interfaces;
using JRemote.Core.Models;
using JRemote.iOS.Bluetooth;
using JRemote.iOS.Devices;

namespace JRemote.iOS.Session;

public class IOSMirroringSession : IMirroringSession
{
    private readonly IOSDevice _device;
    private readonly BluetoothHidController _hidController;
    private CancellationTokenSource? _sessionCts;
    private bool _isRunning;

    public IDevice Device => _device;
    public IInputController InputController => _hidController;
    public bool IsRunning => _isRunning;

    public event EventHandler<VideoFrame>? FrameReceived;
    public event EventHandler<string>? ErrorOccurred;

    public IOSMirroringSession(IOSDevice device)
    {
        _device = device;
        _hidController = new BluetoothHidController();
    }

    public async Task StartAsync(MirroringConfig? config = null, CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        config ??= new MirroringConfig();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;
        _device.UpdateState(DeviceState.Mirroring);

        // Try to start Bluetooth HID emulation for mouse/keyboard control
        _ = _hidController.StartEmulationAsync();

        // Background frame processing loop
        _ = Task.Run(() => StreamLoopAsync(_sessionCts.Token), _sessionCts.Token);
        await Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _sessionCts?.Cancel();
        _hidController.Stop();
        _device.UpdateState(DeviceState.Connected);
        return Task.CompletedTask;
    }

    private async Task StreamLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                // In production, AirPlay H.264 NAL units are demuxed and decoded here
                await Task.Delay(16, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"iOS stream error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionCts?.Dispose();
    }
}
