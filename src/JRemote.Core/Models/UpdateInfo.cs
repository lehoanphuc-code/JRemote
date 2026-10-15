namespace JRemote.Core.Models;

public class UpdateInfo
{
    public required string CurrentVersion { get; init; }
    public required string NewVersion { get; init; }
    public required string TagName { get; init; }
    public string? ReleaseTitle { get; init; }
    public string? ReleaseNotes { get; init; }
    public required string DownloadUrl { get; init; }
    public long DownloadSize { get; init; }
    public bool IsUpdateAvailable { get; init; }
}
