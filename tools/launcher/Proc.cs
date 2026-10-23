using System.Diagnostics;

namespace AcrExtraStages;

/// Child processes: the bundled tools run with the working folder at the root (their defaults are work\..., config\...),
/// output appended to a log file and, with --verbose, echoed.
internal sealed class Proc
{
    readonly Layout _l;
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Verbose { get; set; }

    public Proc(Layout l) { _l = l; }

    public sealed record Result(int Code, List<string> Lines)
    {
        public string Text => string.Join("\n", Lines);
    }

    public Result Run(string exe, IEnumerable<string> args, TextWriter? log, string? label = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _l.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        var lines = new List<string>();
        var gate = new object();
        log?.WriteLine($"> {Path.GetFileName(exe)} {string.Join(' ', psi.ArgumentList.Select(Quote))}");
        log?.Flush();
        void On(string? s)
        {
            if (s is null) return;
            lock (gate)
            {
                lines.Add(s);
                log?.WriteLine(s);
                if (Verbose) Ui.Detail(s);
            }
        }
        using var ticker = label is null ? null : new Ticker(label);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => On(e.Data);
        p.ErrorDataReceived += (_, e) => On(e.Data);
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e) { throw new LauncherException($"cannot start {exe}: {e.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        log?.WriteLine($"< exit {p.ExitCode}");
        log?.Flush();
        return new Result(p.ExitCode, lines);
    }

    /// Run and throw on a non-zero exit code (like the scripts' Run helper).
    public Result Must(string exe, IEnumerable<string> args, TextWriter? log, string? label = null)
    {
        var r = Run(exe, args, log, label);
        if (r.Code != 0)
        {
            var tail = r.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(3).Select(x => x.Trim());
            throw new StepFailed($"{Path.GetFileNameWithoutExtension(exe)} {args.FirstOrDefault()} failed ({r.Code}): {string.Join(" | ", tail)}");
        }
        return r;
    }

    static string Quote(string a) => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a;
}

internal sealed class StepFailed(string message) : Exception(message);

/// One "    label ... 12 s" status line that updates in place while a long child process runs (interactive consoles only).
internal sealed class Ticker : IDisposable
{
    readonly Timer? _t;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly string _label;

    public Ticker(string label)
    {
        _label = label;
        if (Console.IsOutputRedirected) return;
        _t = new Timer(_ => Draw(), null, 1000, 1000);
    }

    void Draw()
    {
        lock (Ui.Gate)
        {
            var s = $"    {_label} ... {(int)_sw.Elapsed.TotalSeconds} s";
            var w = Math.Max(10, SafeWidth() - 1);
            Console.Write("\r" + (s.Length > w ? s[..w] : s.PadRight(w)));
        }
    }

    static int SafeWidth() { try { return Console.WindowWidth; } catch (IOException) { return 80; } }

    public void Dispose()
    {
        if (_t is null) return;
        _t.Dispose();
        lock (Ui.Gate) Console.Write("\r" + new string(' ', Math.Max(10, SafeWidth() - 1)) + "\r");
    }
}

internal static class Ui
{
    public static readonly object Gate = new();
    public static bool Yes;     // --yes: answer every prompt with its default (non-interactive runs)

    public static void Line(string s = "") { lock (Gate) Console.WriteLine(s); }
    public static void Detail(string s) { lock (Gate) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("      " + s); Console.ResetColor(); } }
    public static void Ok(string s) => Color(ConsoleColor.Green, s);
    public static void Warn(string s) => Color(ConsoleColor.Yellow, s);
    public static void Err(string s) => Color(ConsoleColor.Red, s);
    public static void Head(string s) { Line(); Color(ConsoleColor.Cyan, s); }

    static void Color(ConsoleColor c, string s)
    {
        lock (Gate) { Console.ForegroundColor = c; Console.WriteLine(s); Console.ResetColor(); }
    }

    public static string? Ask(string prompt, string? def = null)
    {
        if (Yes || Console.IsInputRedirected && Console.In.Peek() < 0) return def;
        lock (Gate) Console.Write(def is null ? $"{prompt}: " : $"{prompt} [{def}]: ");
        var s = Console.ReadLine()?.Trim().Trim('"');
        return string.IsNullOrEmpty(s) ? def : s;
    }

    public static bool Confirm(string prompt, bool def = true)
    {
        var a = Ask(prompt + (def ? " (Y/n)" : " (y/N)"), null);
        if (a is null) return def;
        return a.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
