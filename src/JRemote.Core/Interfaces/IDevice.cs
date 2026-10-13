using JRemote.Core.Models;

namespace JRemote.Core.Interfaces;

public class DeviceStateChangedEventArgs : EventArgs
{
    public DeviceState OldState { get; }
    public DeviceState NewState { get; }
    public string? Reason { get; }

    public DeviceStateChangedEventArgs(DeviceState oldState, DeviceState newState, string? reason = null)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }
}

public interface IDevice : IDisposable
{
    DeviceInfo Info { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    event EventHandler<DeviceStateChangedEventArgs>? StateChanged;
}
