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
        using var stream = File.OpenRead(pos[0]);
        var tpk = new TpkFile();
        tpk.Read(stream);
        DdsToPng(tpk.ToDds(), pos[1]);
        Console.WriteLine($"wrote {pos[1]} ({tpk.Name} {tpk.Format} {tpk.Width}x{tpk.Height})");
        return 0;
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
