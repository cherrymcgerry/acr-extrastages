using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AcrRef;

/// A block-compressed DDS (legacy FourCC or DX10 header) split into mips, with BC1/BC3/BC4/BC5 decoding and a PNG writer
/// for previews.
internal sealed class Dds
{
    public enum Fmt { BC1, BC3, BC4, BC5, BC7 }
    public Fmt Format { get; }
    public bool Srgb { get; }
    public int Width { get; }
    public int Height { get; }
    public List<byte[]> Mips { get; } = new();
    public int BlockBytes => Format is Fmt.BC1 or Fmt.BC4 ? 8 : 16;

    public Dds(string path) : this(File.ReadAllBytes(path), Path.GetFileName(path)) { }

    public Dds(byte[] d, string name)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x20534444) throw new InvalidDataException($"{name}: not a DDS");
        Height = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        Width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(28)));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        string fourCC = Encoding.ASCII.GetString(d, 84, 4);
        int at = 128;
        if ((pfFlags & 4) == 0) throw new NotSupportedException($"{name}: uncompressed DDS");
        if (fourCC == "DX10")
        {
            uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(128));
            (Format, Srgb) = dxgi switch
            {
                71 => (Fmt.BC1, false), 72 => (Fmt.BC1, true), 77 => (Fmt.BC3, false), 78 => (Fmt.BC3, true),
                80 => (Fmt.BC4, false), 83 => (Fmt.BC5, false), 98 => (Fmt.BC7, false), 99 => (Fmt.BC7, true),
                _ => throw new NotSupportedException($"{name}: DXGI format {dxgi}"),
            };
            at += 20;
        }
        else
        {
            // legacy FourCC: colour space is not recorded; DR2 diffuse textures are authored in sRGB
            (Format, Srgb) = fourCC switch
            {
                "DXT1" => (Fmt.BC1, true), "DXT5" => (Fmt.BC3, true), "ATI1" or "BC4U" => (Fmt.BC4, false), "ATI2" or "BC5U" => (Fmt.BC5, false),
                _ => throw new NotSupportedException($"{name}: FourCC {fourCC}"),
            };
        }
        for (int m = 0; m < mips; m++)
        {
            int size = MipBytes(m);
            if (at + size > d.Length) break;
            Mips.Add(d.AsSpan(at, size).ToArray());
            at += size;
        }
    }

    public int MipWidth(int m) => Math.Max(1, Width >> m);
    public int MipHeight(int m) => Math.Max(1, Height >> m);
    public int MipBytes(int m) => Math.Max(1, (MipWidth(m) + 3) / 4) * Math.Max(1, (MipHeight(m) + 3) / 4) * BlockBytes;

    /// RGBA8 pixels of one mip (BC7 is not decoded: returns null).
    public byte[]? Decode(int m) => Format == Fmt.BC7 ? null : DecodeBlocks(Format, Mips[m], MipWidth(m), MipHeight(m));

    public static byte[] DecodeBlocks(Fmt format, byte[] src, int w, int h)
    {
        int BlockBytes = format is Fmt.BC1 or Fmt.BC4 ? 8 : 16, bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var px = new byte[w * h * 4];
        var blk = new byte[64];
        var Format = format;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var b = src.AsSpan((by * bw + bx) * BlockBytes, BlockBytes);
                switch (Format)
                {
                    case Fmt.BC1: Color(b, blk, true); break;
                    case Fmt.BC3: Color(b[8..], blk, false); Alpha(b, blk, 3); break;
                    case Fmt.BC4: Alpha(b, blk, 0); for (int i = 0; i < 16; i++) { blk[i * 4 + 1] = blk[i * 4 + 2] = blk[i * 4]; blk[i * 4 + 3] = 255; } break;
                    case Fmt.BC5: Alpha(b, blk, 0); Alpha(b[8..], blk, 1); for (int i = 0; i < 16; i++) { blk[i * 4 + 2] = 255; blk[i * 4 + 3] = 255; } break;
                }
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int X = bx * 4 + x, Y = by * 4 + y;
                        if (X < w && Y < h) blk.AsSpan((y * 4 + x) * 4, 4).CopyTo(px.AsSpan((Y * w + X) * 4));
                    }
            }
        return px;
    }

    static void Color(ReadOnlySpan<byte> b, byte[] o, bool bc1)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4], a = stackalloc int[4];
        r[0] = (c0 >> 11) * 255 / 31; g[0] = ((c0 >> 5) & 63) * 255 / 63; bl[0] = (c0 & 31) * 255 / 31; a[0] = 255;
        r[1] = (c1 >> 11) * 255 / 31; g[1] = ((c1 >> 5) & 63) * 255 / 63; bl[1] = (c1 & 31) * 255 / 31; a[1] = 255;
        if (!bc1 || c0 > c1)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3; a[2] = 255;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3; a[3] = 255;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2; a[2] = 255;
            r[3] = g[3] = bl[3] = 0; a[3] = 0;
        }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(idx >> (2 * i)) & 3;
            o[i * 4] = (byte)r[k]; o[i * 4 + 1] = (byte)g[k]; o[i * 4 + 2] = (byte)bl[k]; o[i * 4 + 3] = (byte)a[k];
        }
    }

    static void Alpha(ReadOnlySpan<byte> b, byte[] o, int channel)
    {
        int a0 = b[0], a1 = b[1];
        Span<int> v = stackalloc int[8];
        v[0] = a0; v[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i < 5; i++) v[i + 1] = ((5 - i) * a0 + i * a1) / 5; v[6] = 0; v[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) o[i * 4 + channel] = (byte)v[(int)(bits >> (3 * i)) & 7];
    }

    /// BC1 (4-colour mode) of an RGBA8 image whose sides are multiples of 4: endpoints are the block's extremes along
    /// its bounding-box diagonal.
    public static byte[] EncodeBc1(byte[] rgba, int w, int h)
    {
        int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
        var o = new byte[bw * bh * 8];
        Span<int> px = stackalloc int[48];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int[] mn = [255, 255, 255], mx = [0, 0, 0];
                for (int i = 0; i < 16; i++)
                {
                    int X = Math.Min(w - 1, bx * 4 + i % 4), Y = Math.Min(h - 1, by * 4 + i / 4), k = (Y * w + X) * 4;
                    for (int c = 0; c < 3; c++) { px[i * 3 + c] = rgba[k + c]; mn[c] = Math.Min(mn[c], rgba[k + c]); mx[c] = Math.Max(mx[c], rgba[k + c]); }
                }
                ushort To565(int[] c) => (ushort)(((c[0] * 31 + 127) / 255 << 11) | ((c[1] * 63 + 127) / 255 << 5) | (c[2] * 31 + 127) / 255);
                ushort c0 = To565(mx), c1 = To565(mn);
                uint idx = 0;
                if (c0 == c1) { /* solid: all indices 0 */ }
                else
                {
                    if (c0 < c1) (c0, c1) = (c1, c0);
                    int[][] pal = new int[4][];
                    int[] U(ushort c) => [(c >> 11) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31];
                    pal[0] = U(c0); pal[1] = U(c1);
                    pal[2] = [(2 * pal[0][0] + pal[1][0]) / 3, (2 * pal[0][1] + pal[1][1]) / 3, (2 * pal[0][2] + pal[1][2]) / 3];
                    pal[3] = [(pal[0][0] + 2 * pal[1][0]) / 3, (pal[0][1] + 2 * pal[1][1]) / 3, (pal[0][2] + 2 * pal[1][2]) / 3];
                    for (int i = 0; i < 16; i++)
                    {
                        int best = 0, bestD = int.MaxValue;
                        for (int k = 0; k < 4; k++)
                        {
                            int d = 0;
                            for (int c = 0; c < 3; c++) { int e = px[i * 3 + c] - pal[k][c]; d += e * e; }
                            if (d < bestD) { bestD = d; best = k; }
                        }
                        idx |= (uint)best << (2 * i);
                    }
                }
                var b = o.AsSpan((by * bw + bx) * 8);
                BinaryPrimitives.WriteUInt16LittleEndian(b, c0); BinaryPrimitives.WriteUInt16LittleEndian(b[2..], c1);
                BinaryPrimitives.WriteUInt32LittleEndian(b[4..], idx);
            }
        return o;
    }

    /// 2x2 box filter (sides halve, minimum 1).
    public static (byte[] px, int w, int h) Half(byte[] rgba, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int s = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                        s += rgba[(Math.Min(h - 1, y * 2 + dy) * w + Math.Min(w - 1, x * 2 + dx)) * 4 + c];
                    o[(y * nw + x) * 4 + c] = (byte)((s + 2) / 4);
                }
        return (o, nw, nh);
    }

    /// Full BC1 mip chain (down to 1x1; blocks below 4x4 encode the clamped image) of an RGBA8 image.
    public static List<byte[]> Bc1Chain(byte[] rgba, int w, int h)
    {
        var r = new List<byte[]>();
        while (true)
        {
            r.Add(EncodeBc1(Pad4(rgba, w, h), Math.Max(4, w), Math.Max(4, h)));
            if (w == 1 && h == 1) return r;
            (rgba, w, h) = Half(rgba, w, h);
        }
    }

    static byte[] Pad4(byte[] rgba, int w, int h)
    {
        if (w >= 4 && h >= 4) return rgba;
        int nw = Math.Max(4, w), nh = Math.Max(4, h);
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
            rgba.AsSpan((Math.Min(h - 1, y) * w + Math.Min(w - 1, x)) * 4, 4).CopyTo(o.AsSpan((y * nw + x) * 4));
        return o;
    }

    /// One BC5 block (two BC4 halves) of a constant R, G.
    public static byte[] SolidBc5(byte r, byte g) => [r, r, 0, 0, 0, 0, 0, 0, g, g, 0, 0, 0, 0, 0, 0];

    public static void WritePng(string path, int w, int h, byte[] rgba)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(rgba, y * w * 4, w * 4); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", []);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, data.Length); s.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        uint c = 0xFFFFFFFF;
        foreach (var x in td) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, ~c); s.Write(crc);
    }
}
