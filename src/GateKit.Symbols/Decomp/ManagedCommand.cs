using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Decomp;

/// <summary>`dotnet` (the Toolkit's own managed assemblies to C# with ILSpy, into the database)
/// and `csharp` (search that C#, or print one file).</summary>
internal static class ManagedCommand
{
    static readonly string[] ThirdParty =
    [
        "System.", "Microsoft.", "Xceed.", "Google.", "Newtonsoft.", "Magick.", "WeifenLuo.", "Cyotek.", "Emoji.",
        "Typography.", "ScintillaNET", "SciLexer", "Office", "DocumentFormat.", "p4api", "Steamworks", "SFGLib",
        "FreeImageNET", "DirectXTexNet", "Skybound", "SmartPropertyGrid", "GDEXSharp", "Xceed",
    ];

    /// <summary>How to start ILSpy: dotnet + ilspycmd.dll under &lt;tools&gt;/ilspycmd/*/tools/*/any/,
    /// else an ilspycmd on the PATH.</summary>
    static (string File, List<string> Args) FindIlspy(DecompSettings settings, string? dotnetRoot)
    {
        var root = Path.Combine(settings.Tools, "ilspycmd");
        string? dll = null;
        if (Directory.Exists(root))
            dll = Directory.EnumerateDirectories(root)
                .SelectMany(v => Directory.Exists(Path.Combine(v, "tools")) ? Directory.EnumerateDirectories(Path.Combine(v, "tools")) : [])
                .Select(t => Path.Combine(t, "any", "ilspycmd.dll")).Where(File.Exists).Order(StringComparer.Ordinal).LastOrDefault();
        if (dll != null)
        {
            string runner = dotnetRoot != null ? Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet";
            return (runner, [dll]);
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "ilspycmd.exe", "ilspycmd.cmd" } : ["ilspycmd"])
                if (File.Exists(Path.Combine(dir, name)))
                    return (Path.Combine(dir, name), []);
        throw new SymbolsException($"ilspycmd not found under {root} and not on the PATH: unpack the ilspycmd package from nuget.org there");
    }

    /// <summary>True for a PE file with a CLR header (read from its first 4 KB, as the Python tool did).</summary>
    static bool IsManaged(string path)
    {
        var head = new byte[4096];
        int n;
        using (var f = File.OpenRead(path))
            n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (n < 0x40 || head[0] != 'M' || head[1] != 'Z')
            return false;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
        if (pe < 0 || pe + 24 + 240 > n)
            return false;
        int magic = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 24));
        int clr = pe + 24 + (magic == 0x20B ? 112 : 96) + 14 * 8;
        return BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(clr)) != 0;
    }

    static string Tail(string text, int n) => text.Length <= n ? text : text[^n..];

    public static int Dotnet(DecompSession s, string? dotnetRoot, string? only, int timeout, bool fresh)
    {
        var (runner, prefix) = FindIlspy(s.Settings, dotnetRoot);
        var outRoot = Path.Combine(s.Settings.Work, "dotnet");
        int done = 0, failed = 0;
        var names = new List<string>();
        var toolkit = s.Settings.Toolkit;
        var candidates = Directory.Exists(toolkit)
            ? Directory.EnumerateFiles(toolkit, "*.dll").Order(Py.PathOrder).Concat(Directory.EnumerateFiles(toolkit, "*.exe").Order(Py.PathOrder)).ToList()
            : [];
        foreach (var dll in candidates)
        {
            var file = Path.GetFileName(dll);
            if (ThirdParty.Any(p => file.StartsWith(p, StringComparison.Ordinal))
                || (only != null && !file.ToLowerInvariant().Contains(only.ToLowerInvariant(), StringComparison.Ordinal)))
                continue;
            if (!IsManaged(dll))
                continue;                                 // not a .NET assembly
            var stem = Path.GetFileNameWithoutExtension(dll);
            var target = Path.Combine(outRoot, stem);
            names.Add(stem);
            if (Directory.Exists(target) && (Directory.EnumerateFiles(target, "*.cs", SearchOption.AllDirectories).Any()
                                             || File.Exists(Path.Combine(target, "ilspy-error.txt"))) && !fresh)
                continue;
            Directory.CreateDirectory(target);
            var psi = new ProcessStartInfo(runner)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in prefix.Concat(["-p", "-o", target, dll]))
                psi.ArgumentList.Add(a);
            if (dotnetRoot != null)
                psi.Environment["DOTNET_ROOT"] = dotnetRoot;
            // ilspycmd targets .NET 8; without that runtime it runs on the newer one GateKit itself needs
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD")))
                psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            using var process = Process.Start(psi) ?? throw new SymbolsException($"could not start {runner}");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(timeout)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // exited meanwhile
                }
                failed++;
                File.WriteAllText(Path.Combine(target, "ilspy-error.txt"), $"gave up after {timeout} s", new UTF8Encoding(false));
                s.Err.WriteLine($"  {file}: gave up after {timeout} s (mixed C++/CLI assemblies can take very long)");
                continue;
            }
            process.WaitForExit();
            string o = stdout.Result, e = stderr.Result;
            int files = Directory.EnumerateFiles(target, "*.cs", SearchOption.AllDirectories).Count();
            if (process.ExitCode != 0 || files == 0)
            {
                failed++;
                File.WriteAllText(Path.Combine(target, "ilspy-error.txt"), Tail(o + e, 4000), new UTF8Encoding(false));
                var said = Py.Strip(e.Length > 0 ? e : o);
                s.Err.WriteLine($"  {file}: not decompiled ({(said.Length > 0 ? Py.Left(Py.SplitLines(said)[^1], 120) : "no output")})");
            }
            else
            {
                done++;
                s.Err.WriteLine($"  {file}: {files} files");
            }
        }
        var db = s.OpenWrite();
        db.Execute("DELETE FROM managed_code; DELETE FROM managed_files; DELETE FROM managed_fts;");
        long fileCount = 0, lines = 0;
        db.Execute("BEGIN");
        if (Directory.Exists(outRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(outRoot).Order(Py.PathOrder))
            {
                var name = Path.GetFileName(dir);
                foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(Py.PathOrder))
                {
                    var text = File.ReadAllText(path, new UTF8Encoding(false, false));   // a leading BOM is dropped, as utf-8-sig does
                    var rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
                    int count = text.Count(c => c == '\n');
                    db.Execute("INSERT INTO managed_files (assembly, path, lines) VALUES (?,?,?)", name, rel, count);
                    long id = Convert.ToInt64(db.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
                    db.Execute("INSERT INTO managed_code VALUES (?,?)", id, DecompStore.Compress(text));
                    db.Execute("INSERT INTO managed_fts (rowid, path, code) VALUES (?,?,?)", id, $"{name}/{rel}", text);
                    fileCount++;
                    lines += count;
                }
            }
        }
        db.Execute("COMMIT");
        s.Out.WriteLine($"{names.Count} managed assemblies, {done} decompiled now, {failed} failed; {fileCount} C# files, {lines} lines in the database");
        return 0;
    }

    public static int CSharp(DecompSession s, string text, bool file, int lines, bool raw, int limit)
    {
        var db = s.Db;
        if (file)
        {
            var blob = db.Scalar("SELECT c.text FROM managed_files f JOIN managed_code c ON c.file_id = f.id WHERE f.assembly || '/' || f.path = ?"
                                 + " OR f.path = ? LIMIT 1", text, text);
            s.Out.WriteLine(DecompStore.Decompress(blob) ?? "no such file");
            return 0;
        }
        var query = raw ? text : GrepCommand.WordQuery(text);
        var rows = db.Query("SELECT f.id, f.assembly, f.path FROM managed_fts JOIN managed_files f ON f.id = managed_fts.rowid"
                            + " WHERE managed_fts MATCH ? LIMIT ?", query, limit).ToList();
        var words = GrepCommand.LineWords(text);
        foreach (var r in rows)
        {
            s.Out.WriteLine($"{Py.Str(r[1])}/{Py.Str(r[2])}");
            if (lines != 0)
                GrepCommand.PrintLines(s.Out, DecompStore.Decompress(db.Scalar("SELECT text FROM managed_code WHERE file_id = ?", r[0])), words, lines, true);
        }
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found");
        return 0;
    }
}
