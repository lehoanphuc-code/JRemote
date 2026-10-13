using H264Sharp;
using JRemote.Core.Interfaces;
using JRemote.Core.Logging;
using JRemote.Core.Models;
using System.Runtime.InteropServices;

namespace JRemote.Rendering.Decoders;

/// <summary>
/// Bộ giải mã video H.264 thời gian thực sử dụng Cisco OpenH264 (thông qua H264Sharp)
/// Giải mã luồng NAL units sang định dạng BGRA32 để hiển thị trực tiếp lên WPF WriteableBitmap
/// </summary>
public class OpenH264Decoder : IVideoDecoder
{
    private H264Decoder? _decoder;
    private RgbImage? _rgbImage;
    private readonly object _syncLock = new();
    private bool _initialized;
    private bool _isDisposed;
    private long _decodedFrameCount;
    private bool _loggedFirstFrame;

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    public event EventHandler<VideoFrame>? FrameDecoded;

    public OpenH264Decoder()
    {
    }

    private void EnsureInitialized()
    {
        if (_initialized || _isDisposed) return;

        try
        {
            _decoder = new H264Decoder();
            int rc = _decoder.Initialize();
            if (rc == 0)
            {
                _initialized = true;
                AppLogger.Log("[OpenH264Decoder] Khởi tạo OpenH264 Decoder thành công (Native Cisco OpenH264).");
            }
            else
            {
                AppLogger.Log($"[OpenH264Decoder-LỖI] Khởi tạo OpenH264 thất bại, mã kết quả: {rc}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[OpenH264Decoder-LỖI] Không thể nạp OpenH264 DLL: {ex.Message}");
        }
    }

    public Task FeedNalUnitAsync(byte[] nalData, long timestampMs)
    {
        if (_isDisposed || nalData == null || nalData.Length == 0)
            return Task.CompletedTask;

        lock (_syncLock)
        {
            if (_isDisposed) return Task.CompletedTask;
            EnsureInitialized();
            if (_decoder == null) return Task.CompletedTask;

            try
            {
                bool success = _decoder.Decode(
                    nalData,
                    0,
                    nalData.Length,
                    noDelay: false,
                    out DecodingState state,
                    out YUVImagePointer yuv);

                if (success && yuv.Width > 0 && yuv.Height > 0)
                {
                    OutputWidth = yuv.Width;
                    OutputHeight = yuv.Height;

                    // Khởi tạo hoặc cập nhật bộ đệm RGB nếu kích thước thay đổi
                    if (_rgbImage == null || _rgbImage.Width != yuv.Width || _rgbImage.Height != yuv.Height)
                    {
                        _rgbImage?.Dispose();
                        _rgbImage = new RgbImage(ImageFormat.Bgra, yuv.Width, yuv.Height);
                        AppLogger.Log($"[OpenH264Decoder] Khởi tạo bộ đệm hiển thị: {yuv.Width}x{yuv.Height} (Bgra32, Stride: {_rgbImage.Stride})");
                    }

                    // Chuyển đổi màu sắc YUV420P sang BGRA32 bằng SIMD tăng tốc phần cứng
                    Converter.Yuv2Rgb(yuv, _rgbImage);

                    int totalBytes = yuv.Width * yuv.Height * 4;
                    byte[] bgraPixels = new byte[totalBytes];
                    Marshal.Copy(_rgbImage.NativeBytes, bgraPixels, 0, totalBytes);

                    _decodedFrameCount++;
                    if (!_loggedFirstFrame)
                    {
                        _loggedFirstFrame = true;
                        AppLogger.Log($"[OpenH264Decoder] ★ GIẢI MÃ THÀNH CÔNG FRAME ĐẦU TIÊN ({yuv.Width}x{yuv.Height}) - Đang chuyển tới Renderer!");
                    }
                    else if (_decodedFrameCount % 180 == 0)
                    {
                        AppLogger.Log($"[OpenH264Decoder] Đã giải mã thành công {_decodedFrameCount} frames video.");
                    }

                    var frame = new VideoFrame(
                        yuv.Width,
                        yuv.Height,
                        yuv.Width * 4,
                        bgraPixels,
                        timestampMs);

                    FrameDecoded?.Invoke(this, frame);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpenH264Decoder-LỖI] Ngoại lệ khi giải mã frame: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public void Flush()
    {
        // No-op or reset buffer
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _rgbImage?.Dispose();
                _rgbImage = null;
                _decoder?.Dispose();
                _decoder = null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[OpenH264Decoder] Lỗi khi giải phóng tài nguyên: {ex.Message}");
            }
        }
    }
}
