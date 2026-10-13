using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using JRemote.Android.Input;
using JRemote.Core.Interfaces;
using JRemote.Core.Models;

namespace JRemote.Android.Devices;

using CoreDeviceState = JRemote.Core.Models.DeviceState;

public class AndroidDevice : IDevice
{
    private readonly IAdbClient _adbClient;
    private readonly DeviceData _deviceData;
    private CoreDeviceState _state = CoreDeviceState.Connected;

    public DeviceInfo Info { get; }
    public DeviceData DeviceData => _deviceData;
    public IAdbClient AdbClient => _adbClient;

    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    public AndroidDevice(IAdbClient adbClient, DeviceData deviceData)
    {
        _adbClient = adbClient;
        _deviceData = deviceData;

        var isWifi = deviceData.Serial.Contains(':');
        Info = new DeviceInfo
        {
            Id = deviceData.Serial,
            Name = string.IsNullOrWhiteSpace(deviceData.Model) ? deviceData.Serial : $"{deviceData.Product} {deviceData.Model}".Trim(),
            Type = DeviceType.Android,
            ConnectionType = isWifi ? ConnectionType.WiFi : ConnectionType.Usb,
            State = CoreDeviceState.Connected,
            IpAddress = isWifi ? deviceData.Serial.Split(':')[0] : null
        };
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        UpdateState(CoreDeviceState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        UpdateState(CoreDeviceState.Disconnected);
        return Task.CompletedTask;
    }

    public void UpdateState(CoreDeviceState newState, string? reason = null)
    {
        if (_state == newState) return;
        var oldState = _state;
        _state = newState;
        Info.State = newState;
        StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(oldState, newState, reason));
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
