using BCnEncoder.Decoder;
using EgoEngineLibrary.Formats.Tpk;
using EgoEngineLibrary.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using EgoDds = EgoEngineLibrary.Graphics.Dds.DdsFile;

namespace Dr2Extract;

internal static class TextureCommands
{
    public static int TpkToPng(string[] args)
    {
        var pos = Program.Positional(args);
        if (Single8Bit(pos[0]) is { } single)
        {
            single.Image.SaveAsPng(pos[1]);
            Console.WriteLine($"wrote {pos[1]} (8-bit {single.Image.Width}x{single.Image.Height})");
            single.Image.Dispose();
            return 0;
        }

        using var stream = File.OpenRead(pos[0]);
        var tpk = new TpkFile();
        tpk.Read(stream);
        DdsToPng(tpk.ToDds(), pos[1]);
        Console.WriteLine($"wrote {pos[1]} ({tpk.Name} {tpk.Format} {tpk.Width}x{tpk.Height})");
        return 0;
    }

    /// <summary>
    /// Header version 0x66 tpk with format 38: one byte per pixel (frontend route-map splines), payload = last
    /// width*height bytes. Returns null for anything else.
    /// </summary>
    public static (Image<L8> Image, int Width, int Height)? Single8Bit(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 0x40 || BitConverter.ToInt32(bytes, 0) != 0x66 || BitConverter.ToInt32(bytes, 0x28) != 38)
        {
            return null;
        }

        var (w, h) = (BitConverter.ToInt32(bytes, 0x2C), BitConverter.ToInt32(bytes, 0x30));
        if (w <= 0 || h <= 0 || (long)w * h > bytes.Length)
        {
            return null;
        }

        var payload = bytes.AsSpan(bytes.Length - w * h).ToArray();
        var format = Environment.GetEnvironmentVariable("DR2_TPK38") ?? "raw";
        if (format == "raw")
        {
            return (Image.LoadPixelData<L8>(payload, w, h), w, h);
        }

        var decoded = new BcDecoder().DecodeRaw(payload, w, h, Enum.Parse<BCnEncoder.Shared.CompressionFormat>(format));
        var gray = new Image<L8>(w, h);
        for (var i = 0; i < decoded.Length; i++)
        {
            var c = decoded[i];
            gray[i % w, i / w] = new L8((Environment.GetEnvironmentVariable("DR2_TPK38_CH") ?? "a") switch
            {
                "r" => c.r,
                "g" => c.g,
                "b" => c.b,
                _ => c.a,
            });
        }

        return (gray, w, h);
    }

    public static void DdsToPng(EgoDds egoDds, string path)
    {
        using var ms = new MemoryStream();
        egoDds.Write(ms);
        ms.Position = 0;
        var dds = BCnEncoder.Shared.ImageFiles.DdsFile.Load(ms);
        using var image = new Image<Rgba32>((int)dds.header.dwWidth, (int)dds.header.dwHeight);
        if (!image.DangerousTryGetSinglePixelMemory(out var pixels))
        {
            throw new InvalidOperationException("image memory not contiguous");
        }

        new BcDecoder().DecodeDdsToPixels(dds, pixels.Span);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        image.SaveAsPng(path);
    }
}
