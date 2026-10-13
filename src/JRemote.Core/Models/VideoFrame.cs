namespace JRemote.Core.Models;

public class VideoFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public long TimestampMs { get; init; }

    public VideoFrame() { }

    public VideoFrame(int width, int height, int stride, byte[] data, long timestampMs)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Data = data;
        TimestampMs = timestampMs;
    }
}
