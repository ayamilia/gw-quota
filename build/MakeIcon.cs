// =====================================================================
//  MakeIcon · 生成多尺寸 gwquota.ico（BMP 条目，csc /win32icon 可直接用）
//  用法：MakeIcon.exe <输出目录>
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    static readonly Color Bg1 = Color.FromArgb(255, 30, 35, 42);
    static readonly Color Bg2 = Color.FromArgb(255, 11, 13, 17);
    static readonly Color Accent = Color.FromArgb(255, 82, 208, 224);
    static readonly int[] Sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

    static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad = Math.Max(0.4f, size * 0.02f);
            float w = size - 2 * pad;
            float r = w * 0.33f;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(pad, pad, r * 2, r * 2, 180, 90);
                p.AddArc(pad + w - r * 2, pad, r * 2, r * 2, 270, 90);
                p.AddArc(pad + w - r * 2, pad + w - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(pad, pad + w - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new PointF(0, 0), new PointF(size, size), Bg1, Bg2))
                    g.FillPath(br, p);
            }

            float inset = size * 0.235f;
            float penW = Math.Max(1.1f, size * 0.085f);
            using (Pen pen = new Pen(Accent, penW))
                g.DrawEllipse(pen, inset, inset, size - 2 * inset, size - 2 * inset);

            float dot = size * 0.115f;
            using (SolidBrush b = new SolidBrush(Accent))
                g.FillEllipse(b, (size - dot) / 2f, (size - dot) / 2f, dot, dot);
        }
        return bmp;
    }

    static byte[] BuildImageBlock(int size, out int stride)
    {
        using (Bitmap bmp = Draw(size))
        {
            Rectangle rect = new Rectangle(0, 0, size, size);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = data.Stride;
            byte[] raw = new byte[data.Stride * size];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            bmp.UnlockBits(data);

            int maskRow = ((size + 31) / 32) * 4;
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);

            bw.Write(40);           // biSize
            bw.Write(size);         // biWidth
            bw.Write(size * 2);     // biHeight（含 AND 掩码）
            bw.Write((short)1);     // biPlanes
            bw.Write((short)32);    // biBitCount
            bw.Write(0);            // biCompression
            bw.Write(0);            // biSizeImage
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

            for (int y = size - 1; y >= 0; y--)
                bw.Write(raw, y * stride, size * 4);

            bw.Write(new byte[maskRow * size]);
            bw.Flush();
            byte[] block = ms.ToArray();
            bw.Dispose();
            ms.Dispose();
            return block;
        }
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string outPath = Path.Combine(dir, "gwquota.ico");

        List<byte[]> blocks = new List<byte[]>();
        List<int> sizes = new List<int>();
        for (int i = 0; i < Sizes.Length; i++)
        {
            int stride;
            blocks.Add(BuildImageBlock(Sizes[i], out stride));
            sizes.Add(Sizes[i]);
        }

        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)blocks.Count);

            int offset = 6 + 16 * blocks.Count;
            for (int i = 0; i < blocks.Count; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(blocks[i].Length);
                bw.Write(offset);
                offset += blocks[i].Length;
            }
            for (int i = 0; i < blocks.Count; i++) bw.Write(blocks[i]);
            bw.Flush();
        }

        long len = new FileInfo(outPath).Length;
        Console.WriteLine("OK " + outPath + " " + len + " bytes, sizes=" + string.Join(",", sizes.ConvertAll(delegate (int v) { return v.ToString(); }).ToArray()));
        return 0;
    }
}
