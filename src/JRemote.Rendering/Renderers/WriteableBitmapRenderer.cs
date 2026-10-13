using JRemote.Core.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JRemote.Rendering.Renderers;

public class WriteableBitmapRenderer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    public WriteableBitmap? Bitmap => _bitmap;
    public event EventHandler<WriteableBitmap>? BitmapCreatedOrResized;

    public WriteableBitmapRenderer(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void RenderFrame(VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data.Length == 0) return;

        if (_dispatcher.CheckAccess())
        {
            UpdateBitmap(frame);
        }
        else
        {
            _dispatcher.BeginInvoke(() => UpdateBitmap(frame), DispatcherPriority.Render);
        }
    }

    private void UpdateBitmap(VideoFrame frame)
    {
        if (_bitmap == null || _width != frame.Width || _height != frame.Height)
        {
            _width = frame.Width;
            _height = frame.Height;
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            BitmapCreatedOrResized?.Invoke(this, _bitmap);
        }

        _bitmap.Lock();
        try
        {
            int sourceStride = frame.Stride > 0 ? frame.Stride : _width * 4;
            int copyBytes = Math.Min(sourceStride * _height, frame.Data.Length);
            Marshal.Copy(frame.Data, 0, _bitmap.BackBuffer, copyBytes);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    public void Dispose()
    {
        _bitmap = null;
    }
}
