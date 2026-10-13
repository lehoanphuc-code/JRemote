using JRemote.Core.Interfaces;
using JRemote.Core.Models;

namespace JRemote.iOS.Devices;

public class IOSDevice : IDevice
{
    private DeviceState _state = DeviceState.Connected;

    public DeviceInfo Info { get; }

    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    public IOSDevice(string id, string name, string? ipAddress = null)
    {
        Info = new DeviceInfo
        {
            Id = id,
            Name = name,
            Type = DeviceType.iOS,
            ConnectionType = ConnectionType.WiFi,
            State = DeviceState.Connected,
            IpAddress = ipAddress
        };
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        UpdateState(DeviceState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateState(DeviceState.Disconnected);
        return Task.CompletedTask;
    }

    public void UpdateState(DeviceState newState, string? reason = null)
    {
        if (_state == newState) return;
        var oldState = _state;
        _state = newState;
        Info.State = newState;
        StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(oldState, newState, reason));
    }

    public void Dispose()
    {
    }
}
