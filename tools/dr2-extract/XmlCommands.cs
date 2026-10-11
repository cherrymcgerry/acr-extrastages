using System.Xml;
using EgoEngineLibrary.Xml;

namespace Dr2Extract;

/// <summary>
/// Codemasters binary XML ("\x1A\"Rr" BinXml or BXML) to text XML.
/// </summary>
internal static class XmlCommands
{
    public static int Convert(string[] args)
    {
        var pos = Program.Positional(args);
        if (Directory.Exists(pos[0]))
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(pos[0], "*.xml", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".text.xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.WriteAllText(Path.ChangeExtension(file, ".text.xml"), ToText(Load(file)));
                count++;
            }

            Console.WriteLine($"converted {count} files under {pos[0]}");
            return 0;
        }

        var text = ToText(Load(pos[0]));
        if (pos.Length > 1)
        {
            File.WriteAllText(pos[1], text);
        }
        else
        {
            Console.Write(text);
        }

        return 0;
    }

    public static XmlDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return new XmlFile(stream).Document;
    }

    private static string ToText(XmlDocument doc)
    {
        using var writer = new StringWriter();
        using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        {
            doc.Save(xml);
        }

        return writer.ToString();
    }
}
