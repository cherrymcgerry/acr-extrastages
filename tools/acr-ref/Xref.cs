using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text;
using Iced.Intel;

namespace AcrRef;

/// Finds code in a PE image that references given strings (rip-relative LEA) and disassembles the
/// containing functions (bounds from .pdata), highlighting accesses to given struct offsets.
internal static class Xref
{
    public static int Run(Options o)
    {
        var exe = o.Named.GetValueOrDefault("exe") ?? Path.Combine(o.Game, "acr", "Binaries", "Win64", "acr.exe");
        var strings = o.Positional;
        var callees = o.Named.GetValueOrDefault("callers-of", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => ulong.Parse(s.Replace("0x", ""), NumberStyles.HexNumber)).ToHashSet();
        if (strings.Count == 0 && callees.Count == 0 && !o.Named.ContainsKey("disasm") && !o.Named.ContainsKey("scan-range"))
            throw new ArgumentException("xref needs strings, --callers-of, --disasm or --scan-range");
        var fields = o.Named.GetValueOrDefault("fields", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.Parse(s.Replace("0x", ""), NumberStyles.HexNumber)).ToHashSet();

        var img = File.ReadAllBytes(exe);
        using var pe = new PEReader(new MemoryStream(img));
        var secs = pe.PEHeaders.SectionHeaders;
        ulong imageBase = pe.PEHeaders.PEHeader!.ImageBase;
        int ToOff(long rva) { foreach (var s in secs) if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData) return (int)(rva - s.VirtualAddress + s.PointerToRawData); return -1; }
        long ToRva(int off) { foreach (var s in secs) if (off >= s.PointerToRawData && off < s.PointerToRawData + s.SizeOfRawData) return off - s.PointerToRawData + s.VirtualAddress; return -1; }

        var targets = new Dictionary<long, string>();
        foreach (var str in strings)
            foreach (var enc in new Encoding[] { Encoding.Unicode, Encoding.ASCII })
            {
                var pat = enc.GetBytes(str);
                for (int i = img.AsSpan().IndexOf(pat), b = 0; i >= 0; b = i + 1, i = Next(img, pat, b))
                    targets[ToRva(i)] = str;
            }
        Console.WriteLine($"{targets.Count} string occurrences");

        var text = secs.First(s => s.Name == ".text");
        var refs = new List<(long rva, string str)>();
        int tStart = text.PointerToRawData, tEnd = tStart + text.SizeOfRawData - 7;
        for (int i = tStart; i < tEnd; i++)
        {
            if (callees.Count > 0 && (img[i] == 0xE8 || img[i] == 0xE9))
            {
                long crva = ToRva(i);
                var dest = imageBase + (ulong)(crva + 5 + BitConverter.ToInt32(img, i + 1));
                if (callees.Contains(dest)) refs.Add((crva, $"{(img[i] == 0xE8 ? "call" : "jmp")} {dest:X}"));
            }
            if ((img[i] != 0x48 && img[i] != 0x4C) || img[i + 1] != 0x8D || (img[i + 2] & 0xC7) != 0x05) continue;
            long rva = ToRva(i);
            long tgt = rva + 7 + BitConverter.ToInt32(img, i + 3);
            if (targets.TryGetValue(tgt, out var s)) refs.Add((rva, s));
        }

        var pdataDir = pe.PEHeaders.PEHeader.ExceptionTableDirectory;
        int pOff = ToOff(pdataDir.RelativeVirtualAddress), pCount = pdataDir.Size / 12;
        (uint begin, uint end, uint unwind) Fn(int k) => (BitConverter.ToUInt32(img, pOff + k * 12), BitConverter.ToUInt32(img, pOff + k * 12 + 4), BitConverter.ToUInt32(img, pOff + k * 12 + 8));
        (uint begin, uint end)? Containing(long rva)
        {
            int lo = 0, hi = pCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var f = Fn(mid);
                if (rva < f.begin) hi = mid - 1; else if (rva >= f.end) lo = mid + 1; else return (f.begin, f.end);
            }
            return null;
        }

        uint Primary(long rva)
        {
            int lo = 0, hi = pCount - 1, k = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; var f = Fn(mid); if (rva < f.begin) hi = mid - 1; else if (rva >= f.end) lo = mid + 1; else { k = mid; break; } }
            if (k < 0) return 0;
            var (begin, _, unwind) = Fn(k);
            for (int guard = 0; guard < 16; guard++)
            {
                int u = ToOff(unwind & ~1u);
                if ((img[u] >> 3 & 0x4) == 0) return begin;
                int codes = img[u + 2];
                int ch = u + 4 + ((codes + 1) & ~1) * 2;
                begin = BitConverter.ToUInt32(img, ch);
                unwind = BitConverter.ToUInt32(img, ch + 8);
            }
            return begin;
        }

        using var w = o.Out is null ? Console.Out : new StreamWriter(o.Out);
        var done = new HashSet<uint>();

        foreach (var va in o.Named.GetValueOrDefault("primary", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var rva = (long)(ulong.Parse(va.Replace("0x", ""), NumberStyles.HexNumber) - imageBase);
            w.WriteLine($"primary function of {va}: {imageBase + Primary(rva):X}");
        }

        foreach (var va in o.Named.GetValueOrDefault("disasm", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var rva = (long)(ulong.Parse(va.Replace("0x", ""), NumberStyles.HexNumber) - imageBase);
            if (Containing(rva) is not { } f || !done.Add(f.begin)) continue;
            w.WriteLine($"function chunk {imageBase + f.begin:X}-{imageBase + f.end:X}");
            Disassemble(w, img, ToOff(f.begin), f.begin, f.end, imageBase, targets, fields, rva2 => { int off = ToOff(rva2); return off < 0 ? null : TryString(img, off); });
            w.WriteLine();
        }

        if (o.Named.TryGetValue("scan-range", out var range) && fields.Count > 0)
        {
            var parts = range.Split('-').Select(s => ulong.Parse(s.Replace("0x", ""), NumberStyles.HexNumber)).ToArray();
            long lo = (long)(parts[0] - imageBase), hi = (long)(parts[1] - imageBase);
            var fmt = new MasmFormatter();
            var so = new StringOutput();
            for (int k = 0; k < pCount; k++)
            {
                var f = Fn(k);
                if (f.begin < lo || f.begin >= hi) continue;
                var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(img, ToOff(f.begin), (int)(f.end - f.begin)), imageBase + f.begin);
                while (dec.IP < imageBase + f.end)
                {
                    dec.Decode(out var ins);
                    if (ins.IsInvalid) break;
                    if (ins.MemoryBase is Register.None or Register.RIP or Register.RSP or Register.RBP) continue;
                    if (!o.Named.ContainsKey("any-size") && ins.MemorySize != MemorySize.UInt8 && ins.MemorySize != MemorySize.Int8) continue;
                    if (!fields.Contains((long)ins.MemoryDisplacement64)) continue;
                    fmt.Format(ins, so);
                    w.WriteLine($"byte access +0x{ins.MemoryDisplacement64:X} in fn {imageBase + f.begin:X}: {ins.IP:X}  {so.ToStringAndReset()}");
                }
            }
        }

        foreach (var (rva, str) in refs.OrderBy(r => r.rva))
        {
            var fn = Containing(rva);
            w.WriteLine($"ref to \"{str}\" at {imageBase + (ulong)rva:X} in function chunk {(fn is { } f0 ? $"{imageBase + f0.begin:X}-{imageBase + f0.end:X}" : "?")}");
            if (fn is not { } f || !done.Add(f.begin)) continue;
            Disassemble(w, img, ToOff(f.begin), f.begin, f.end, imageBase, targets, fields, rva2 => { int off = ToOff(rva2); return off < 0 ? null : TryString(img, off); });
            w.WriteLine();
        }
        Console.WriteLine($"{refs.Count} code references{(o.Out is null ? "" : " -> " + o.Out)}");
        return 0;
    }

    static int Next(byte[] img, byte[] pat, int from)
    {
        if (from >= img.Length) return -1;
        int j = img.AsSpan(from).IndexOf(pat);
        return j < 0 ? -1 : from + j;
    }

    static string? TryString(byte[] img, int off)
    {
        int n = 0;
        while (off + n * 2 + 1 < img.Length && n < 200 && img[off + n * 2 + 1] == 0 && img[off + n * 2] is >= 0x20 and < 0x7f) n++;
        if (n >= 4 && img[off + n * 2] == 0) return "L\"" + Encoding.Unicode.GetString(img, off, n * 2) + "\"";
        n = 0;
        while (off + n < img.Length && n < 200 && img[off + n] is >= 0x20 and < 0x7f) n++;
        if (n >= 4 && img[off + n] == 0) return "\"" + Encoding.ASCII.GetString(img, off, n) + "\"";
        return null;
    }

    static void Disassemble(TextWriter w, byte[] img, int off, uint begin, uint end, ulong imageBase,
        Dictionary<long, string> targets, HashSet<long> fields, Func<long, string?> str)
    {
        var reader = new ByteArrayCodeReader(img, off, (int)(end - begin));
        var dec = Iced.Intel.Decoder.Create(64, reader, imageBase + begin);
        var fmt = new MasmFormatter();
        fmt.Options.FirstOperandCharIndex = 10;
        var sb = new StringOutput();
        while (dec.IP < imageBase + end)
        {
            dec.Decode(out var ins);
            fmt.Format(ins, sb);
            var line = sb.ToStringAndReset();
            var note = "";
            if (ins.IsIPRelativeMemoryOperand)
            {
                var t = (long)(ins.IPRelativeMemoryAddress - imageBase);
                note = str(t) is { } s ? "  ; " + s : "";
            }
            if (fields.Count > 0 && ins.MemoryBase != Register.None && ins.MemoryBase != Register.RIP && fields.Contains((long)ins.MemoryDisplacement64))
                note += $"  ; <<< field +0x{ins.MemoryDisplacement64:X}";
            w.WriteLine($"  {ins.IP:X}  {line}{note}");
        }
    }
}
