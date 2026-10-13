namespace JRemote.Core.Interfaces;

public interface IDeviceManager : IAsyncDisposable
{
    IReadOnlyList<IDevice> Devices { get; }

    Task StartDiscoveryAsync(CancellationToken cancellationToken = default);
    Task StopDiscoveryAsync();

    event EventHandler<IDevice>? DeviceConnected;
    event EventHandler<IDevice>? DeviceDisconnected;
}
