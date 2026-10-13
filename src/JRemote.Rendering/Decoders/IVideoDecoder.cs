using JRemote.Core.Models;

namespace JRemote.Rendering.Decoders;

public interface IVideoDecoder : IDisposable
{
    int OutputWidth { get; }
    int OutputHeight { get; }

    event EventHandler<VideoFrame>? FrameDecoded;

    Task FeedNalUnitAsync(byte[] nalData, long timestampMs);
    void Flush();
}
