using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class BuildAppIcon
{
    private static int Main(string[] args)
    {
        string brand = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "AetherPC.App", "Assets", "Brand"));

        using var masterImg = Image.FromFile(Path.Combine(brand, "AetherPC.png"));
        using var master = new Bitmap(masterImg);
        int w = master.Width, h = master.Height;
        using var punched = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = master.GetPixel(x, y);
                bool nearBlack = c.R < 28 && c.G < 28 && c.B < 35;
                punched.SetPixel(x, y, nearBlack
                    ? Color.FromArgb(0, 0, 0, 0)
                    : Color.FromArgb(255, c.R, c.G, c.B));
            }
        }

        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (punched.GetPixel(x, y).A > 10)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }

        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        using var cropped = punched.Clone(new Rectangle(minX, minY, cw, ch), PixelFormat.Format32bppArgb);
        int side = Math.Max(cw, ch);
        int pad = (int)Math.Ceiling(side * 0.08);
        int canvas = side + pad * 2;

        using var logo = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(logo))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(cropped, (canvas - cw) / 2, (canvas - ch) / 2, cw, ch);
        }

        int radius = Math.Max(4, (int)Math.Round(canvas * 0.028));
        using var outlined = AddOutline(logo, radius, Color.FromArgb(200, 10, 14, 20));
        outlined.Save(Path.Combine(brand, "AetherPC-icon-source.png"), ImageFormat.Png);
        outlined.Save(Path.Combine(brand, "AetherPC-transparent.png"), ImageFormat.Png);

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<(int Size, byte[] Png)>();
        foreach (int s in sizes)
        {
            using var dest = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dest))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(outlined, 0, 0, s, s);
            }

            using (var ms = new MemoryStream())
            {
                dest.Save(ms, ImageFormat.Png);
                frames.Add((s, ms.ToArray()));
            }

            if (s is 32 or 48)
            {
                SaveQa(dest, brand, s, "white", Color.White);
                SaveQa(dest, brand, s, "dark", Color.FromArgb(255, 32, 32, 32));
                SaveQa(dest, brand, s, "lightgray", Color.FromArgb(255, 240, 240, 240));
            }
        }

        WriteIco(Path.Combine(brand, "AetherPC.ico"), frames);
        Console.WriteLine($"OK frames={frames.Count} outlineR={radius} canvas={canvas}");
        return 0;
    }

    private static void SaveQa(Bitmap dest, string brand, int s, string name, Color bg)
    {
        using var qa = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var gq = Graphics.FromImage(qa);
        gq.Clear(bg);
        gq.DrawImage(dest, 0, 0);
        qa.Save(Path.Combine(brand, $"qa-{s}-{name}.png"), ImageFormat.Png);
    }

    private static Bitmap AddOutline(Bitmap src, int radius, Color outline)
    {
        int w = src.Width, h = src.Height;
        var bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[bd.Stride * h];
        Marshal.Copy(bd.Scan0, px, 0, px.Length);
        int stride = bd.Stride;
        src.UnlockBits(bd);

        bool[,] mask = new bool[w, h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[x, y] = px[y * stride + x * 4 + 3] > 40;

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var db = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] outp = new byte[db.Stride * h];
        int r2 = radius * radius;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask[x, y]) continue;
                bool near = false;
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                for (int yy = y0; yy <= y1 && !near; yy++)
                {
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        int dx = xx - x, dy = yy - y;
                        if (dx * dx + dy * dy <= r2 && mask[xx, yy])
                        {
                            near = true;
                            break;
                        }
                    }
                }

                if (near)
                {
                    int i = y * db.Stride + x * 4;
                    outp[i] = outline.B;
                    outp[i + 1] = outline.G;
                    outp[i + 2] = outline.R;
                    outp[i + 3] = outline.A;
                }
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = y * stride + x * 4;
                int di = y * db.Stride + x * 4;
                byte a = px[si + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    outp[di] = px[si];
                    outp[di + 1] = px[si + 1];
                    outp[di + 2] = px[si + 2];
                    outp[di + 3] = 255;
                }
                else
                {
                    float af = a / 255f;
                    float ia = 1 - af;
                    outp[di] = (byte)(px[si] * af + outp[di] * ia);
                    outp[di + 1] = (byte)(px[si + 1] * af + outp[di + 1] * ia);
                    outp[di + 2] = (byte)(px[si + 2] * af + outp[di + 2] * ia);
                    outp[di + 3] = (byte)Math.Min(255, a + outp[di + 3] * (1 - af));
                }
            }
        }

        Marshal.Copy(outp, 0, db.Scan0, outp.Length);
        dst.UnlockBits(db);
        return dst;
    }

    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            bw.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            bw.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(f.Png.Length);
            bw.Write(offset);
            offset += f.Png.Length;
        }

        foreach (var f in frames)
            bw.Write(f.Png);
    }
}
