using System.Security.Cryptography;
using System.Text;

namespace JRemote.iOS.AirPlay;

/// <summary>
/// Sinh định danh duy nhất (Virtual MAC và Pairing GUID) dựa theo tên thiết bị AirPlay.
/// Điều này giúp tránh bị iOS lưu cache tên thiết bị cũ (caching pair-verify theo deviceID/pi)
/// và ngăn chặn xung đột khi nhiều máy tính chạy JRemote trong cùng mạng Wi-Fi.
/// </summary>
public static class AirPlayDeviceIdentity
{
    public static (string MacWithColons, string MacNoColons, string PairingId) Generate(string serverName)
    {
        var normalized = string.IsNullOrWhiteSpace(serverName) ? "JRemote-PC" : serverName.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));

        // Sinh địa chỉ MAC ảo 6-bytes:
        // Đặt bit 1 (0x02) để biểu thị Locally Administered Unicast MAC (theo chuẩn IEEE 802)
        // Xóa bit 0 (0xFE) để đảm bảo không phải là địa chỉ Multicast
        var macBytes = new byte[6];
        macBytes[0] = (byte)((hash[0] & 0xFE) | 0x02);
        macBytes[1] = hash[1];
        macBytes[2] = hash[2];
        macBytes[3] = hash[3];
        macBytes[4] = hash[4];
        macBytes[5] = hash[5];

        var macWithColons = string.Join(":", macBytes.Select(b => b.ToString("X2")));
        var macNoColons = string.Concat(macBytes.Select(b => b.ToString("X2")));

        // Sinh Pairing UUID (pi) chuẩn RFC 4122 phiên bản 4 từ hash[6..21]
        var guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 6, guidBytes, 0, 16);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40); // Version 4
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // Variant RFC 4122
        var pairingId = new Guid(guidBytes).ToString().ToLowerInvariant();

        return (macWithColons, macNoColons, pairingId);
    }
}
