using JRemote.Core.Models;

namespace JRemote.Core.Interfaces;

public interface IMirroringSession : IAsyncDisposable
{
    IDevice Device { get; }
    IInputController InputController { get; }
    bool IsRunning { get; }

    Task StartAsync(MirroringConfig? config = null, CancellationToken cancellationToken = default);
    Task StopAsync();

    event EventHandler<VideoFrame>? FrameReceived;
    event EventHandler<string>? ErrorOccurred;
}
