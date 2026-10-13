namespace JRemote.Core.Models;

public class MirroringConfig
{
    public int MaxResolution { get; set; } = 1920;
    public int BitrateBps { get; set; } = 8_000_000; // 8 Mbps
    public int MaxFps { get; set; } = 60;
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; } = false;
    public bool ForwardAudio { get; set; } = true;
    public int BufferMs { get; set; } = 50;
}
