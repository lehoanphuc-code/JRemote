using JRemote.Core.Logging;
using JRemote.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JRemote.Core.Services;

public class SettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JRemote"
    );

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(settings.AirPlayDeviceName))
                    {
                        settings.AirPlayDeviceName = GenerateDefaultAirPlayName();
                        SaveSettings(settings);
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Settings] Lỗi đọc cấu hình: {ex.Message}. Sử dụng cấu hình mặc định.");
        }

        var defaultSettings = new AppSettings
        {
            AirPlayDeviceName = GenerateDefaultAirPlayName()
        };
        SaveSettings(defaultSettings);
        return defaultSettings;
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Settings] Lỗi lưu cấu hình: {ex.Message}");
        }
    }

    public static string GenerateDefaultAirPlayName()
    {
        var raw = Environment.MachineName;
        // Chuẩn hóa tên máy tính chỉ giữ lại chữ cái, số, gạch ngang
        var sanitized = Regex.Replace(raw, @"[^a-zA-Z0-9\-]", "");
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "PC";
        }
        else if (sanitized.Length > 20)
        {
            sanitized = sanitized[..20];
        }

        return $"JRemote-{sanitized}";
    }
}
