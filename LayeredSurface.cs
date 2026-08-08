using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Traymetry
{
    /// <summary>
    /// A window whose every pixel carries its own transparency.
    ///
    /// The widget's transparent mode is a colour key: one colour is declared
    /// invisible and every other pixel is fully opaque.  Nothing in between
    /// exists, so the half-covered pixels along the edge of a glyph - the ones
    /// that make text look smooth - are drawn against the key colour and then
    /// kept.  Over a dark desktop that halo is invisible; over a light one it
    /// is the ragged, dirty edge around every digit.
    ///
    /// UpdateLayeredWindow takes a 32-bit bitmap with real alpha instead, and
    /// GDI+ writes exactly that alpha when it draws antialiased text onto an
    /// empty surface.  The compositor then blends the widget over whatever is
    /// actually behind it, and the halo stops existing rather than being made
    /// less visible.
    ///
    /// The price is that such a window has no child controls: a child window
    /// draws itself into the screen, not into this bitmap, so everything has
    /// to be painted onto the surface and every click has to be hit-tested by
    /// hand.  Alpha does pay one of that back - a pixel left transparent is
    /// click-through by definition, so a shaped click catcher is not needed.
    /// </summary>
    internal sealed class LayeredSurface : IDisposable
    {
        private readonly Form _owner;
        private Bitmap _buffer;
        private Size _size;
        private Size _capacity;
        private IntPtr _memoryDc;
        private IntPtr _section;
        private IntPtr _previousBitmap;

        internal LayeredSurface(Form owner)
        {
            if (owner == null)
                throw new ArgumentNullException("owner");
            _owner = owner;
        }

        /// <summary>
        /// The extended style a layered window needs.  Applied through
        /// CreateParams by the form itself, because switching a window to
        /// layered after it is on screen loses whatever it had drawn.
        /// </summary>
        internal const int ExStyleLayered = 0x00080000;

        /// <summary>
        /// The whole-window opacity, 0-255.  It has to ride here rather than on
        /// Form.Opacity: that property calls SetLayeredWindowAttributes, which
        /// puts the window into the other layering mode and throws the pushed
        /// bitmap away.  The next frame puts it back, so a slider being dragged
        /// makes the two modes trade the window back and forth - which on screen
        /// is the transparent mode dropping out and the controls underneath it
        /// smearing.
        /// </summary>
        internal byte ConstantAlpha
        {
            get { return _constantAlpha; }
            set { _constantAlpha = value; }
        }

        private byte _constantAlpha = 255;

        /// <summary>
        /// Draws one frame and hands it to the compositor.  The surface is
        /// cleared to fully transparent first: what the callback does not
        /// paint is what the desktop keeps.
        /// </summary>
        internal void Render(Size size, Action<Graphics> paint)
        {
            if (paint == null)
                throw new ArgumentNullException("paint");
            if (size.Width < 1 || size.Height < 1 || !_owner.IsHandleCreated)
                return;
            if (!EnsureSurface(size))
                return;

            try
            {
                using (Graphics graphics = Graphics.FromImage(_buffer))
                {
                    // The buffer is usually bigger than the window; everything
                    // past the window's own size is nobody's business, and the
                    // clip keeps Clear from wiping more than it has to.
                    graphics.SetClip(new Rectangle(0, 0, size.Width, size.Height));
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // Grey coverage, not subpixel coverage.  ClearType writes three
                    // colour channels of coverage and no alpha at all, which on a
                    // transparent surface comes out as coloured confetti along the
                    // edges; this one writes the alpha the compositor needs.
                    graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                    paint(graphics);
                }
            }
            catch (OutOfMemoryException error)
            {
                // Not the managed heap running dry: GDI+ reports every drawing
                // call it could not complete as an out-of-memory, and a single
                // refused arc used to close the widget.  A frame the compositor
                // never receives leaves the last one on screen, which is a blink
                // rather than a loss.
                Report("frame dropped: " + error.Message);
                return;
            }

            Push();
        }

        /// <summary>
        /// Makes the frame buffer and the device context the compositor reads
        /// from, and keeps both until the window changes size.
        ///
        /// The obvious version of this - paint into a plain Bitmap, call
        /// GetHbitmap, push, delete - allocates a fresh full-window bitmap on
        /// every single frame.  At widget size that is invisible; expanded over
        /// a large screen it is eight megabytes allocated, copied into, read by
        /// the compositor and thrown away, twenty-five times a second.  The
        /// pages are new every time, so every one of them faults: the log showed
        /// a hundred and fifty thousand page faults in half a second, the UI
        /// stalling in half-second lumps, and finally GDI+ refusing to allocate
        /// at all and taking the widget down with an out-of-memory from inside
        /// DrawArc.
        ///
        /// A DIB section is the same memory seen two ways at once - as a GDI
        /// bitmap and as the pixel buffer GDI+ paints into - so the copy and the
        /// allocation both disappear, and the pages stay mapped between frames.
        ///
        /// The buffer also only ever grows, and grows in steps.  Sized exactly
        /// to the window it would be thrown away and made again on every frame
        /// of a resize drag, which is the same storm as before with a smaller
        /// window to hide behind.  A buffer wider than the window costs some
        /// memory and nothing else: the compositor is told how much of it to
        /// read.
        /// </summary>
        private bool EnsureSurface(Size size)
        {
            if (_buffer != null && _capacity.Width >= size.Width &&
                _capacity.Height >= size.Height)
            {
                _size = size;
                return true;
            }

            const int Step = 128;
            Size capacity = new Size(
                Math.Max(_capacity.Width, ((size.Width + Step - 1) / Step) * Step),
                Math.Max(_capacity.Height, ((size.Height + Step - 1) / Step) * Step));

            ReleaseSurface();

            NativeSurface.BITMAPINFO info = new NativeSurface.BITMAPINFO();
            info.Header.Size = Marshal.SizeOf(typeof(NativeSurface.BITMAPINFOHEADER));
            info.Header.Width = capacity.Width;
            // Negative height: rows top down, which is the order GDI+ expects of
            // the buffer it is handed.  A bottom-up DIB draws the widget upside
            // down.
            info.Header.Height = -capacity.Height;
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            info.Header.Compression = NativeSurface.BI_RGB;

            IntPtr bits;
            IntPtr section = NativeSurface.CreateDIBSection(IntPtr.Zero, ref info,
                NativeSurface.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (section == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Report("CreateDIBSection failed, error " +
                    Marshal.GetLastWin32Error().ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                if (section != IntPtr.Zero)
                    NativeSurface.DeleteObject(section);
                return false;
            }

            IntPtr screenDc = NativeSurface.GetDC(IntPtr.Zero);
            IntPtr memoryDc = NativeSurface.CreateCompatibleDC(screenDc);
            NativeSurface.ReleaseDC(IntPtr.Zero, screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                NativeSurface.DeleteObject(section);
                Report("CreateCompatibleDC failed");
                return false;
            }

            _section = section;
            _memoryDc = memoryDc;
            _previousBitmap = NativeSurface.SelectObject(memoryDc, section);
            // Premultiplied, because that is what the compositor is told to
            // expect through AC_SRC_ALPHA.  Straight alpha here comes out
            // as glowing edges on dark backgrounds and dirty ones on light.
            _buffer = new Bitmap(capacity.Width, capacity.Height, capacity.Width * 4,
                PixelFormat.Format32bppPArgb, bits);
            _capacity = capacity;
            _size = size;
            return true;
        }

        private void ReleaseSurface()
        {
            if (_buffer != null)
            {
                _buffer.Dispose();
                _buffer = null;
            }
            if (_memoryDc != IntPtr.Zero)
            {
                if (_previousBitmap != IntPtr.Zero)
                    NativeSurface.SelectObject(_memoryDc, _previousBitmap);
                NativeSurface.DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
                _previousBitmap = IntPtr.Zero;
            }
            if (_section != IntPtr.Zero)
            {
                NativeSurface.DeleteObject(_section);
                _section = IntPtr.Zero;
            }
            _size = Size.Empty;
            _capacity = Size.Empty;
        }

        private void Push()
        {
            // GDI+ has finished with the buffer, but the drawing GDI itself did
            // through the selected bitmap may still be sitting in a batch.
            NativeSurface.GdiFlush();

            IntPtr screenDc = NativeSurface.GetDC(IntPtr.Zero);
            try
            {
                NativeSurface.SIZE size = new NativeSurface.SIZE
                {
                    cx = _size.Width,
                    cy = _size.Height
                };
                NativeSurface.POINT source = new NativeSurface.POINT { x = 0, y = 0 };
                NativeSurface.BLENDFUNCTION blend = new NativeSurface.BLENDFUNCTION
                {
                    BlendOp = NativeSurface.AC_SRC_OVER,
                    BlendFlags = 0,
                    // The whole-window opacity slider rides here: it scales the
                    // per-pixel alpha instead of replacing it.
                    SourceConstantAlpha = _constantAlpha,
                    AlphaFormat = NativeSurface.AC_SRC_ALPHA
                };

                // No destination point: the window is already where the widget
                // put it.  Handing one over makes every frame a move as well,
                // which drags the window back out from under the pointer while
                // it is being dragged or resized.
                if (!NativeSurface.UpdateLayeredWindow(_owner.Handle, screenDc, IntPtr.Zero,
                        ref size, _memoryDc, ref source, 0, ref blend, NativeSurface.ULW_ALPHA))
                    Report("UpdateLayeredWindow failed, error " +
                        Marshal.GetLastWin32Error().ToString(
                            System.Globalization.CultureInfo.InvariantCulture) +
                        ", exStyle 0x" + NativeSurface.GetWindowLong(_owner.Handle, -20)
                            .ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            }
            finally
            {
                NativeSurface.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// A failed frame is silent on screen - the window simply stays as it
        /// was - so the reason is written down where it can be read.
        /// </summary>
        private static void Report(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Traymetry-layered.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            ReleaseSurface();
        }
    }

    internal static class NativeSurface
    {
        internal const byte AC_SRC_OVER = 0;
        internal const byte AC_SRC_ALPHA = 1;
        internal const int ULW_ALPHA = 2;
        internal const int BI_RGB = 0;
        internal const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPixelsPerMeter;
            public int YPixelsPerMeter;
            public int ColorsUsed;
            public int ColorsImportant;
        }

        /// <summary>
        /// The colour table a 32-bit BI_RGB bitmap does not have.  One entry is
        /// declared anyway because the header the call reads is followed by it,
        /// and a struct that stops early would have the call reading past what
        /// was handed over.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFO
        {
            public BITMAPINFOHEADER Header;
            public int FirstColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc,
            IntPtr position, ref SIZE size, IntPtr sourceDc, ref POINT source,
            int colorKey, ref BLENDFUNCTION blend, int flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GdiFlush();

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr window, int index);
    }
}
