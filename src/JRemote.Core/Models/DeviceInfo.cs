namespace JRemote.Core.Models;

public class DeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DeviceType Type { get; init; }
    public ConnectionType ConnectionType { get; set; }
    public DeviceState State { get; set; } = DeviceState.Disconnected;
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 2400;
    public int? BatteryPercent { get; set; }
    public string? IpAddress { get; set; }
    public string? OsVersion { get; set; }

    public bool IsLandscape => Width > Height;
    public double AspectRatio => Height > 0 ? (double)Width / Height : 9.0 / 16.0;

    public override string ToString() => $"{Name} ({Type} - {ConnectionType}) [{Id}]";
}
