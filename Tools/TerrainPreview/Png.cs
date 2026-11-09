using System;
using System.IO;
using System.IO.Compression;

namespace Wort.Tools
{
    /// <summary>RGB 图像与最小 PNG 编码器。</summary>
    public sealed class Image
    {
        public readonly int Width, Height;
        public readonly byte[] Rgb;

        public Image(int w, int h, uint fill = 0x202020)
        {
            Width = w; Height = h; Rgb = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++) Put(i, fill);
        }

        void Put(int i, uint c) { Rgb[i * 3] = (byte)(c >> 16); Rgb[i * 3 + 1] = (byte)(c >> 8); Rgb[i * 3 + 2] = (byte)c; }

        public void Set(int x, int y, uint c) { if (x >= 0 && y >= 0 && x < Width && y < Height) Put(y * Width + x, c); }

        public uint Get(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return (uint)(Rgb[i] << 16 | Rgb[i + 1] << 8 | Rgb[i + 2]);
        }

        public void Blend(int x, int y, uint c, double a)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            Set(x, y, Mix(Get(x, y), c, a));
        }

        public void FillRect(int x0, int y0, int w, int h, uint c) { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) Set(x, y, c); }

        public void Disc(double cx, double cy, double r, uint c)
        {
            for (int y = (int)(cy - r - 1); y <= cy + r + 1; y++)
                for (int x = (int)(cx - r - 1); x <= cx + r + 1; x++)
                    if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) <= r * r) Set(x, y, c);
        }

        public static uint Mix(uint a, uint b, double t)
        {
            int r = (int)(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t);
            int g = (int)(((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t);
            int bl = (int)((a & 255) * (1 - t) + (b & 255) * t);
            return (uint)(r << 16 | g << 8 | bl);
        }

        public static uint Scale(uint c, double k)
        {
            int r = Math.Min(255, (int)(((c >> 16) & 255) * k));
            int g = Math.Min(255, (int)(((c >> 8) & 255) * k));
            int b = Math.Min(255, (int)((c & 255) * k));
            return (uint)(r << 16 | g << 8 | b);
        }

        public void Save(string path)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            BE(ihdr, 0, Width); BE(ihdr, 4, Height);
            ihdr[8] = 8; ihdr[9] = 2;
            Chunk(fs, "IHDR", ihdr);
            var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            {
                for (int y = 0; y < Height; y++) { z.WriteByte(0); z.Write(Rgb, y * Width * 3, Width * 3); }
            }
            Chunk(fs, "IDAT", raw.ToArray());
            Chunk(fs, "IEND", Array.Empty<byte>());
        }

        static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td);
            var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
        }

        static uint[] table;
        static uint Crc(byte[] d)
        {
            if (table == null)
            {
                table = new uint[256];
                for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1; table[n] = c; }
            }
            uint crc = 0xFFFFFFFFU;
            foreach (byte b in d) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFU;
        }
    }
}
