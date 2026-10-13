using CommunityToolkit.Mvvm.ComponentModel;
using JRemote.Core.Interfaces;
using JRemote.Core.Models;

namespace JRemote.UI.ViewModels;

public partial class DeviceItemViewModel : ObservableObject
{
    private readonly IDevice _device;

    public IDevice Device => _device;
    public string Id => _device.Info.Id;
    public string Name => _device.Info.Name;
    public DeviceType Type => _device.Info.Type;
    public ConnectionType ConnectionType => _device.Info.ConnectionType;
    public bool IsAndroid => Type == DeviceType.Android;
    public bool IsIos => Type == DeviceType.iOS;
    public bool IsWifi => ConnectionType == ConnectionType.WiFi;

    [ObservableProperty]
    private DeviceState _state;

    [ObservableProperty]
    private bool _isMirroring;

    public string TypeBadge => Type == DeviceType.Android ? "Android" : "iPhone";
    public string ConnectionBadge => ConnectionType == ConnectionType.Usb ? "USB" : "Wi-Fi";

    public DeviceItemViewModel(IDevice device)
    {
        _device = device;
        _state = device.Info.State;
        _isMirroring = _state == DeviceState.Mirroring;

        _device.StateChanged += (s, e) =>
        {
            State = e.NewState;
            IsMirroring = e.NewState == DeviceState.Mirroring;
        };
    }
}
