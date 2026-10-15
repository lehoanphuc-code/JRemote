namespace JRemote.Core.Models;

public class AppSettings
{
    public string AirPlayDeviceName { get; set; } = string.Empty;
    public List<string> RecentWifiIps { get; set; } = new();
    public bool AutoReconnectWifi { get; set; } = true;
}
