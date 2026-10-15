using JRemote.Core.Logging;
using JRemote.Core.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace JRemote.Core.Services;

public class UpdateService
{
    private const string RepoOwner = "lehoanphuc-code";
    private const string RepoName = "JRemote";
    private readonly HttpClient _httpClient;

    public UpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "JRemote-AutoUpdater");
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersionStr, CancellationToken cancellationToken = default)
    {
        try
        {
            var cleanCurrent = currentVersionStr.Trim().TrimStart('v', 'V');
            AppLogger.Log($"[AutoUpdate] Đang kiểm tra bản phát hành mới trên GitHub (Bản hiện tại: v{cleanCurrent})...");

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=5";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Log($"[AutoUpdate] GitHub API phản hồi HTTP {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            Version.TryParse(cleanCurrent, out var currentVer);
            currentVer ??= new Version(1, 0, 0);

            Version? highestVer = null;
            JsonElement? bestRelease = null;
            JsonElement? bestAsset = null;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var isDraft) && isDraft.GetBoolean()) continue;
                if (!rel.TryGetProperty("tag_name", out var tagElem)) continue;

                var tagName = tagElem.GetString() ?? "";
                var cleanTag = tagName.Trim().TrimStart('v', 'V');
                if (!Version.TryParse(cleanTag, out var relVer)) continue;

                // Tìm asset zip định dạng win-x64
                if (!rel.TryGetProperty("assets", out var assetsElem) || assetsElem.ValueKind != JsonValueKind.Array) continue;
                JsonElement? matchingAsset = null;
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp))
                    {
                        var assetName = nameProp.GetString() ?? "";
                        if (assetName.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            matchingAsset = asset;
                            break;
                        }
                    }
                }

                if (matchingAsset == null) continue;

                if (highestVer == null || relVer > highestVer)
                {
                    highestVer = relVer;
                    bestRelease = rel;
                    bestAsset = matchingAsset;
                }
            }

            if (highestVer != null && bestRelease != null && bestAsset != null && highestVer > currentVer)
            {
                var tagName = bestRelease.Value.GetProperty("tag_name").GetString() ?? $"v{highestVer}";
                var title = bestRelease.Value.TryGetProperty("name", out var n) ? n.GetString() : tagName;
                var body = bestRelease.Value.TryGetProperty("body", out var b) ? b.GetString() : null;
                var downloadUrl = bestAsset.Value.GetProperty("browser_download_url").GetString()!;
                var size = bestAsset.Value.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;

                AppLogger.Log($"[AutoUpdate] ★ Phát hiện phiên bản mới: {tagName} (Phiên bản máy: v{cleanCurrent})");
                return new UpdateInfo
                {
                    CurrentVersion = cleanCurrent,
                    NewVersion = highestVer.ToString(3),
                    TagName = tagName,
                    ReleaseTitle = title,
                    ReleaseNotes = body,
                    DownloadUrl = downloadUrl,
                    DownloadSize = size,
                    IsUpdateAvailable = true
                };
            }

            AppLogger.Log($"[AutoUpdate] Ứng dụng đang ở phiên bản mới nhất (v{cleanCurrent}).");
            return new UpdateInfo
            {
                CurrentVersion = cleanCurrent,
                NewVersion = cleanCurrent,
                TagName = $"v{cleanCurrent}",
                DownloadUrl = string.Empty,
                IsUpdateAvailable = false
            };
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AutoUpdate] Không thể kiểm tra cập nhật: {ex.Message}");
            return null;
        }
    }

    public async Task<string> DownloadAndExtractAsync(UpdateInfo updateInfo, IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "JRemote_Update");
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "update.zip");
        var extractDir = Path.Combine(tempDir, "extracted");

        AppLogger.Log($"[AutoUpdate] Đang tải gói cập nhật {updateInfo.TagName} từ: {updateInfo.DownloadUrl}...");

        using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? updateInfo.DownloadSize;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                progress?.Report((totalRead, totalBytes));
            }
        }

        AppLogger.Log($"[AutoUpdate] Tải hoàn tất ({updateInfo.TagName}). Đang giải nén gói cập nhật...");
        ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        // Xác định thư mục chứa JRemote.UI.exe
        var sourceDir = extractDir;
        if (!File.Exists(Path.Combine(extractDir, "JRemote.UI.exe")))
        {
            var found = Directory.GetFiles(extractDir, "JRemote.UI.exe", SearchOption.AllDirectories);
            if (found.Length > 0)
            {
                sourceDir = Path.GetDirectoryName(found[0])!;
            }
        }

        AppLogger.Log($"[AutoUpdate] Đã giải nén sẵn sàng tại: {sourceDir}");
        return sourceDir;
    }

    public void LaunchUpdaterAndRestart(string extractedSourceDir)
    {
        var currentPid = Environment.ProcessId;
        var currentAppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        extractedSourceDir = extractedSourceDir.TrimEnd('\\', '/');
        var exeName = "JRemote.UI.exe";

        var tempDir = Path.GetDirectoryName(extractedSourceDir) ?? Path.GetTempPath();
        var scriptPath = Path.Combine(tempDir, "apply_update.ps1");

        var scriptContent = $$"""
            param(
                [int]$TargetPid,
                [string]$SourceDir,
                [string]$DestDir,
                [string]$ExeName
            )

            # 1. Chờ tiến trình JRemote cũ thoát hoàn toàn
            $timeoutSeconds = 30
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt $timeoutSeconds) {
                $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
                if (-not $proc) { break }
                Start-Sleep -Milliseconds 300
            }

            # 2. Dừng tiến trình adb nếu đang chạy ngầm để giải phóng lock file
            Stop-Process -Name adb -Force -ErrorAction SilentlyContinue

            # 3. Đợi thêm 1 giây để OS nhả toàn bộ file locks
            Start-Sleep -Seconds 1

            # 3. Ghi đè toàn bộ file mới vào thư mục cài đặt
            Copy-Item -Path "$SourceDir\*" -Destination "$DestDir" -Recurse -Force

            # 4. Khởi động lại ứng dụng mới
            $newExe = Join-Path "$DestDir" $ExeName
            if (Test-Path $newExe) {
                Start-Process -FilePath $newExe
            }

            # 5. Dọn dẹp thư mục tải về tạm
            Start-Sleep -Seconds 3
            $parentDir = Split-Path -Parent $SourceDir
            if (Test-Path $parentDir) {
                Remove-Item -Path $parentDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        File.WriteAllText(scriptPath, scriptContent, Encoding.UTF8);

        AppLogger.Log($"[AutoUpdate] Đang chuyển giao cho Updater Script (PID: {currentPid})...");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{scriptPath}\" -TargetPid {currentPid} -SourceDir \"{extractedSourceDir}\" -DestDir \"{currentAppDir}\" -ExeName \"{exeName}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
    }
}
