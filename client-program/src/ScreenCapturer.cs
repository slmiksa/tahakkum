using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tahakkum
{
    // Captures one monitor into a DIB and reports which regions changed since the last grab.
    sealed unsafe class ScreenCapturer : IDisposable
    {
        const int Tile = 64;
        const int StripHeight = 256;
        const int MaxRects = 40;
        const int FullFramePercent = 55;

        static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        public readonly Rectangle Bounds;
        readonly int stride;
        readonly int cols;
        readonly int rows;
        readonly bool[] pendingRefine;
        readonly IntPtr memDC;
        readonly IntPtr dib;
        readonly IntPtr oldObject;
        readonly IntPtr bits;
        readonly IntPtr previous;

        IntPtr cachedCursor;
        int hotX;
        int hotY;

        public ScreenCapturer(Rectangle bounds)
        {
            Bounds = bounds;
            stride = bounds.Width * 4;
            cols = (bounds.Width + Tile - 1) / Tile;
            rows = (bounds.Height + Tile - 1) / Tile;
            pendingRefine = new bool[cols * rows];

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.BITMAPINFOHEADER bmi = new Native.BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bmi.biWidth = bounds.Width;
            bmi.biHeight = -bounds.Height;
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            dib = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            oldObject = Native.SelectObject(memDC, dib);
            previous = Marshal.AllocHGlobal(new IntPtr((long)stride * bounds.Height));
        }

        public bool Grab()
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            bool ok = Native.BitBlt(memDC, 0, 0, Bounds.Width, Bounds.Height, screen, Bounds.X, Bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.ReleaseDC(IntPtr.Zero, screen);
            if (ok) DrawCursor();
            return ok;
        }

        void DrawCursor()
        {
            Native.CURSORINFO ci = new Native.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
            if (!Native.GetCursorInfo(ref ci) || ci.flags != Native.CURSOR_SHOWING) return;
            if (ci.hCursor != cachedCursor)
            {
                Native.ICONINFO info;
                if (!Native.GetIconInfo(ci.hCursor, out info)) return;
                hotX = info.xHotspot;
                hotY = info.yHotspot;
                if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
                cachedCursor = ci.hCursor;
            }
            Native.DrawIconEx(memDC, ci.pt.X - Bounds.X - hotX, ci.pt.Y - Bounds.Y - hotY, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
        }

        // Compares against the previous grab, updates it, and returns the changed rectangles.
        public List<Rectangle> Diff(bool force)
        {
            bool[] grid = new bool[cols * rows];
            byte* cur = (byte*)bits;
            byte* old = (byte*)previous;
            int w = Bounds.Width;
            int h = Bounds.Height;

            for (int r = 0; r < rows; r++)
            {
                int y0 = r * Tile;
                int y1 = Math.Min(y0 + Tile, h);
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * Tile;
                    int tw = Math.Min(Tile, w - x0);
                    bool changed = force;
                    for (int y = y0; y < y1 && !changed; y++)
                    {
                        uint* a = (uint*)(cur + y * stride + x0 * 4);
                        uint* b = (uint*)(old + y * stride + x0 * 4);
                        for (int i = 0; i < tw; i++)
                        {
                            if (a[i] != b[i]) { changed = true; break; }
                        }
                    }
                    if (!changed) continue;
                    grid[r * cols + c] = true;
                    pendingRefine[r * cols + c] = true;
                    long bytes = tw * 4;
                    for (int y = y0; y < y1; y++)
                        Buffer.MemoryCopy(cur + y * stride + x0 * 4, old + y * stride + x0 * 4, bytes, bytes);
                }
            }
            return Merge(grid);
        }

        public bool HasPendingRefine
        {
            get { return Array.IndexOf(pendingRefine, true) >= 0; }
        }

        // Regions that were sent in reduced quality during motion and can now be sharpened.
        public List<Rectangle> TakeRefine()
        {
            List<Rectangle> rects = Merge(pendingRefine);
            Array.Clear(pendingRefine, 0, pendingRefine.Length);
            return rects;
        }

        public void ClearRefine()
        {
            Array.Clear(pendingRefine, 0, pendingRefine.Length);
        }

        List<Rectangle> Merge(bool[] grid)
        {
            List<Rectangle> open = new List<Rectangle>();
            int dirty = 0;
            for (int r = 0; r < rows; r++)
            {
                int c = 0;
                while (c < cols)
                {
                    if (!grid[r * cols + c]) { c++; continue; }
                    int start = c;
                    while (c < cols && grid[r * cols + c]) { c++; dirty++; }
                    int x = start * Tile;
                    int y = r * Tile;
                    int spanW = (c - start) * Tile;
                    int found = -1;
                    for (int i = open.Count - 1; i >= 0; i--)
                    {
                        Rectangle o = open[i];
                        if (o.X == x && o.Width == spanW && o.Bottom == y) { found = i; break; }
                    }
                    if (found >= 0)
                    {
                        Rectangle o = open[found];
                        o.Height += Tile;
                        open[found] = o;
                    }
                    else
                    {
                        open.Add(new Rectangle(x, y, spanW, Tile));
                    }
                }
            }
            if (open.Count == 0) return open;

            if (open.Count > MaxRects || dirty * 100 >= cols * rows * FullFramePercent)
            {
                Rectangle all = open[0];
                foreach (Rectangle o in open) all = Rectangle.Union(all, o);
                open.Clear();
                open.Add(all);
            }

            Rectangle screen = new Rectangle(0, 0, Bounds.Width, Bounds.Height);
            List<Rectangle> result = new List<Rectangle>();
            foreach (Rectangle o in open)
            {
                Rectangle clipped = Rectangle.Intersect(o, screen);
                // Tall regions become strips so they can be encoded in parallel.
                for (int y = clipped.Y; y < clipped.Bottom; y += StripHeight)
                    result.Add(new Rectangle(clipped.X, y, clipped.Width, Math.Min(StripHeight, clipped.Bottom - y)));
            }
            return result;
        }

        public byte[] Encode(Rectangle r, int quality)
        {
            IntPtr start = new IntPtr((byte*)bits + r.Y * stride + r.X * 4);
            using (Bitmap bmp = new Bitmap(r.Width, r.Height, stride, PixelFormat.Format32bppRgb, start))
            using (MemoryStream ms = new MemoryStream())
            using (EncoderParameters p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bmp.Save(ms, JpegCodec, p);
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            Native.SelectObject(memDC, oldObject);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDC);
            Marshal.FreeHGlobal(previous);
        }
    }
}
