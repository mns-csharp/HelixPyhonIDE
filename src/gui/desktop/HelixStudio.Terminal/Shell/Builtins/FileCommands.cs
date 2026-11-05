using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>The file and directory utilities a shell is expected to come with.</summary>
public static class FileCommands
{
    private const string Reset = "\x1b[0m";
    private const string DirectoryColour = "\x1b[1;34m";
    private const string ExecutableColour = "\x1b[1;32m";
    private const string LinkColour = "\x1b[1;36m";
    private const string ArchiveColour = "\x1b[1;31m";

    private static readonly HashSet<string> ExecutableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".com", ".bat", ".cmd", ".ps1", ".msi", ".sh" };

    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".gz", ".tar", ".rar", ".bz2", ".xz", ".tgz", ".cab" };

    public static void Register(BuiltinRegistry registry)
    {
        registry.Register("ls", "ls [-alhrtSR1dF] [path ...]", Ls);
        registry.Register("dir", "dir [-alhrtSR1dF] [path ...]", Ls);
        registry.Register("cp", "cp [-rfvpn] source ... target", Cp);
        registry.Register("mv", "mv [-fvn] source ... target", Mv);
        registry.Register("rm", "rm [-rfvd] path ...", Rm);
        registry.Register("mkdir", "mkdir [-pv] dir ...", Mkdir);
        registry.Register("rmdir", "rmdir [-p] dir ...", Rmdir);
        registry.Register("touch", "touch [-c] file ...", Touch);
        registry.Register("ln", "ln [-s] target link", Ln);
        registry.Register("readlink", "readlink [-f] path", ReadLink);
        registry.Register("realpath", "realpath path ...", RealPath);
        registry.Register("basename", "basename path [suffix]", Basename);
        registry.Register("dirname", "dirname path ...", Dirname);
        registry.Register("stat", "stat [-c format] path ...", Stat);
        registry.Register("du", "du [-sh] [path ...]", Du);
        registry.Register("df", "df [-h]", Df);
        registry.Register("find", "find [path] [-name pat] [-type f|d] [-maxdepth n] [-exec cmd {} ;]", Find);
        registry.Register("tree", "tree [-adL n] [path]", Tree);
        registry.Register("chmod", "chmod mode path ...", Chmod);
        registry.Register("mktemp", "mktemp [-d]", MkTemp);
    }

    // ------------------------------------------------------------------ ls

    private static int Ls(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var paths = options.Operands.Count > 0 ? options.Operands : new List<string> { "." };
        var status = 0;
        var multiple = paths.Count > 1;
        var first = true;

        var files = new List<FileSystemInfo>();
        var directories = new List<string>();

        foreach (var path in paths)
        {
            var resolved = context.ResolvePath(path);

            if (Directory.Exists(resolved) && !options.Has('d'))
            {
                directories.Add(path);
            }
            else if (File.Exists(resolved) || Directory.Exists(resolved))
            {
                files.Add(Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved));
            }
            else
            {
                Interpreter.WriteError(context, $"ls: cannot access '{path}': No such file or directory");
                status = 2;
            }
        }

        if (files.Count > 0)
        {
            WriteEntries(context, Sort(files, options), options, showTotal: false);
            first = false;
        }

        foreach (var path in directories)
        {
            var resolved = context.ResolvePath(path);

            if (multiple || files.Count > 0)
            {
                if (!first) context.Io.Out.WriteLine();
                context.Io.Out.WriteLine($"{path}:");
            }

            first = false;

            try
            {
                var entries = new DirectoryInfo(resolved).GetFileSystemInfos()
                    .Where(e => options.Has('a') || options.Has('A') || !IsHidden(e))
                    .Cast<FileSystemInfo>()
                    .ToList();

                WriteEntries(context, Sort(entries, options), options, showTotal: options.Has('l'));

                if (options.Has('R'))
                {
                    foreach (var sub in Sort(entries.OfType<DirectoryInfo>().Cast<FileSystemInfo>().ToList(), options))
                    {
                        context.Io.Out.WriteLine();
                        var childArgs = new List<string> { args[0] };
                        childArgs.AddRange(args.Skip(1).Where(a => a.StartsWith('-')));
                        childArgs.Add(Path.Combine(path, sub.Name));
                        Ls(context, childArgs, interpreter);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                Interpreter.WriteError(context, $"ls: cannot open directory '{path}': Permission denied");
                status = 2;
            }
        }

        context.Io.Out.Flush();
        return status;
    }

    private static List<FileSystemInfo> Sort(List<FileSystemInfo> entries, CommandLine options)
    {
        IEnumerable<FileSystemInfo> sorted = options switch
        {
            _ when options.Has('t') => entries.OrderByDescending(e => e.LastWriteTimeUtc),
            _ when options.Has('S') => entries.OrderByDescending(e => e is FileInfo f ? f.Length : 0),
            _ => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = sorted.ToList();
        if (options.Has('r')) list.Reverse();
        return list;
    }

    private static void WriteEntries(ShellContext context, List<FileSystemInfo> entries, CommandLine options, bool showTotal)
    {
        if (entries.Count == 0) return;

        // Piped or redirected output gets one plain name per line, so `ls | grep x` behaves.
        var toScreen = context.Io.IsOutputTerminal;

        if (options.Has('l'))
        {
            if (showTotal)
            {
                var total = entries.OfType<FileInfo>().Sum(f => (f.Length + 1023) / 1024);
                context.Io.Out.WriteLine($"total {total}");
            }

            var sizeWidth = entries.Max(e => FormatSize(e, options).Length);

            foreach (var entry in entries)
            {
                var size = FormatSize(entry, options).PadLeft(sizeWidth);
                var time = entry.LastWriteTime.ToString(
                    entry.LastWriteTime.Year == DateTime.Now.Year ? "MMM dd HH:mm" : "MMM dd  yyyy");

                context.Io.Out.WriteLine($"{Mode(entry)} 1 {Environment.UserName,-12} {size} {time} {Decorate(entry, options, toScreen, link: toScreen)}");
            }

            return;
        }

        if (options.Has('1') || !toScreen)
        {
            foreach (var entry in entries) context.Io.Out.WriteLine(Decorate(entry, options, toScreen, link: toScreen));
            return;
        }

        var items = entries
            .Select(e => (Plain: e.Name + Suffix(e, options), Coloured: Decorate(e, options, toScreen, link: toScreen)))
            .ToList();

        WriteColumns(context, items, context.GetValue("COLUMNS"));
    }

    private static void WriteColumns(ShellContext context, List<(string Plain, string Coloured)> items, string? columnsSetting)
    {
        var width = int.TryParse(columnsSetting, out var parsed) && parsed > 20 ? parsed : 80;
        var longest = items.Max(i => i.Plain.Length) + 2;
        var perLine = Math.Max(1, width / longest);
        var rows = (items.Count + perLine - 1) / perLine;

        // Entries run down the columns, the way ls lays them out.
        for (var row = 0; row < rows; row++)
        {
            var line = new StringBuilder();

            for (var column = 0; column < perLine; column++)
            {
                var index = column * rows + row;
                if (index >= items.Count) continue;

                line.Append(items[index].Coloured);
                if (column < perLine - 1 && index + rows < items.Count)
                    line.Append(new string(' ', Math.Max(1, longest - items[index].Plain.Length)));
            }

            context.Io.Out.WriteLine(line.ToString().TrimEnd());
        }
    }

    private static string FormatSize(FileSystemInfo entry, CommandLine options)
    {
        var bytes = entry is FileInfo file ? file.Length : 0;
        return options.Has('h') ? HumanSize(bytes) : bytes.ToString();
    }

    internal static string HumanSize(long bytes)
    {
        string[] units = { "B", "K", "M", "G", "T", "P" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes}" : value >= 10 ? $"{value:0}{units[unit]}" : $"{value:0.0}{units[unit]}";
    }

    private static string Mode(FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        var sb = new StringBuilder();

        sb.Append(attributes.HasFlag(FileAttributes.ReparsePoint) ? 'l' : attributes.HasFlag(FileAttributes.Directory) ? 'd' : '-');

        var writable = !attributes.HasFlag(FileAttributes.ReadOnly);
        var executable = attributes.HasFlag(FileAttributes.Directory) ||
                         ExecutableExtensions.Contains(Path.GetExtension(entry.Name));

        for (var i = 0; i < 3; i++)
        {
            sb.Append('r');
            sb.Append(writable ? 'w' : '-');
            sb.Append(executable ? 'x' : '-');
        }

        return sb.ToString();
    }

    private static bool IsHidden(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden);

    private static string Suffix(FileSystemInfo entry, CommandLine options)
    {
        if (!options.Has('F') && !options.Has('p')) return string.Empty;
        if (entry is DirectoryInfo) return "/";
        if (options.Has('F') && ExecutableExtensions.Contains(Path.GetExtension(entry.Name))) return "*";
        return string.Empty;
    }

    private static string Decorate(FileSystemInfo entry, CommandLine options, bool useColour = true, bool link = false)
    {
        var name = entry.Name + Suffix(entry, options);

        // The link sits inside the colour. Its escapes take no columns, and the column layout
        // measures the plain name, so the listing lines up exactly as it would without it.
        if (link) name = TerminalLinks.Wrap(name, entry.FullName);

        if (!useColour || options.HasLong("no-color") || options.HasLong("color=never")) return name;

        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return LinkColour + name + Reset;
        if (entry is DirectoryInfo) return DirectoryColour + name + Reset;

        var extension = Path.GetExtension(entry.Name);
        if (ExecutableExtensions.Contains(extension)) return ExecutableColour + name + Reset;
        if (ArchiveExtensions.Contains(extension)) return ArchiveColour + name + Reset;

        return name;
    }

    // ------------------------------------------------------- copy and move

    private static int Cp(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count < 2) throw new ShellException("missing destination file operand", 2);

        var sources = options.Operands.Take(options.Operands.Count - 1).ToList();
        var destination = context.ResolvePath(options.Operands[^1]);
        var destinationIsDirectory = Directory.Exists(destination);

        if (sources.Count > 1 && !destinationIsDirectory)
            throw new ShellException($"target '{options.Operands[^1]}' is not a directory", 1);

        var status = 0;

        foreach (var source in sources)
        {
            var from = context.ResolvePath(source);
            var to = destinationIsDirectory ? Path.Combine(destination, Path.GetFileName(from.TrimEnd('\\', '/'))) : destination;

            try
            {
                if (Directory.Exists(from))
                {
                    if (!options.Has('r') && !options.Has('R'))
                    {
                        Interpreter.WriteError(context, $"cp: -r not specified; omitting directory '{source}'");
                        status = 1;
                        continue;
                    }

                    CopyDirectory(from, to, options);
                }
                else if (File.Exists(from))
                {
                    if (options.Has('n') && File.Exists(to)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to) ?? destination);
                    File.Copy(from, to, overwrite: true);
                    if (options.Has('p')) File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
                }
                else
                {
                    Interpreter.WriteError(context, $"cp: cannot stat '{source}': No such file or directory");
                    status = 1;
                    continue;
                }

                if (options.Has('v')) context.Io.Out.WriteLine($"'{source}' -> '{ShellContext.DisplayPath(to)}'");
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"cp: cannot copy '{source}': {ex.Message}");
                status = 1;
            }
        }

        context.Io.Out.Flush();
        return status;
    }

    private static void CopyDirectory(string from, string to, CommandLine options)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (options.Has('n') && File.Exists(target)) continue;
            File.Copy(file, target, overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)), options);
    }

    private static int Mv(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count < 2) throw new ShellException("missing destination file operand", 2);

        var sources = options.Operands.Take(options.Operands.Count - 1).ToList();
        var destination = context.ResolvePath(options.Operands[^1]);
        var destinationIsDirectory = Directory.Exists(destination);
        var status = 0;

        foreach (var source in sources)
        {
            var from = context.ResolvePath(source);
            var to = destinationIsDirectory ? Path.Combine(destination, Path.GetFileName(from.TrimEnd('\\', '/'))) : destination;

            try
            {
                if (options.Has('n') && (File.Exists(to) || Directory.Exists(to))) continue;

                if (Directory.Exists(from))
                {
                    if (Directory.Exists(to) && options.Has('f')) Directory.Delete(to, recursive: true);
                    Directory.Move(from, to);
                }
                else if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
                else
                {
                    Interpreter.WriteError(context, $"mv: cannot stat '{source}': No such file or directory");
                    status = 1;
                    continue;
                }

                if (options.Has('v')) context.Io.Out.WriteLine($"'{source}' -> '{ShellContext.DisplayPath(to)}'");
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"mv: cannot move '{source}': {ex.Message}");
                status = 1;
            }
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Rm(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var recursive = options.Has('r') || options.Has('R');
        var force = options.Has('f');
        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (Directory.Exists(path))
                {
                    if (!recursive && !options.Has('d'))
                    {
                        Interpreter.WriteError(context, $"rm: cannot remove '{operand}': Is a directory");
                        status = 1;
                        continue;
                    }

                    Directory.Delete(path, recursive);
                }
                else if (File.Exists(path))
                {
                    // Clear the read-only bit so `rm -f` behaves the way it does elsewhere.
                    if (force) File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                else
                {
                    if (force) continue;
                    Interpreter.WriteError(context, $"rm: cannot remove '{operand}': No such file or directory");
                    status = 1;
                    continue;
                }

                if (options.Has('v')) context.Io.Out.WriteLine($"removed '{operand}'");
            }
            catch (Exception ex)
            {
                if (force) continue;
                Interpreter.WriteError(context, $"rm: cannot remove '{operand}': {ex.Message}");
                status = 1;
            }
        }

        context.Io.Out.Flush();
        return status;
    }

    // -------------------------------------------------------- directories

    private static int Mkdir(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count == 0) throw new ShellException("missing operand", 2);

        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (Directory.Exists(path))
                {
                    if (options.Has('p')) continue;
                    Interpreter.WriteError(context, $"mkdir: cannot create directory '{operand}': File exists");
                    status = 1;
                    continue;
                }

                var parent = Path.GetDirectoryName(path);
                if (!options.Has('p') && parent is not null && !Directory.Exists(parent))
                {
                    Interpreter.WriteError(context, $"mkdir: cannot create directory '{operand}': No such file or directory");
                    status = 1;
                    continue;
                }

                Directory.CreateDirectory(path);
                if (options.Has('v')) context.Io.Out.WriteLine($"mkdir: created directory '{operand}'");
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"mkdir: cannot create directory '{operand}': {ex.Message}");
                status = 1;
            }
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Rmdir(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (!Directory.Exists(path))
                {
                    Interpreter.WriteError(context, $"rmdir: failed to remove '{operand}': No such file or directory");
                    status = 1;
                    continue;
                }

                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Interpreter.WriteError(context, $"rmdir: failed to remove '{operand}': Directory not empty");
                    status = 1;
                    continue;
                }

                Directory.Delete(path);

                if (options.Has('p'))
                {
                    var parent = Path.GetDirectoryName(path);
                    while (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                        parent = Path.GetDirectoryName(parent);
                    }
                }
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"rmdir: failed to remove '{operand}': {ex.Message}");
                status = 1;
            }
        }

        return status;
    }

    private static int Touch(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "dt");
        var status = 0;
        var timestamp = DateTime.Now;

        if (options.Value('d') is { } dateText && DateTime.TryParse(dateText, out var parsed)) timestamp = parsed;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    if (options.Has('c')) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? context.WorkingDirectory);
                    File.WriteAllBytes(path, Array.Empty<byte>());
                }

                if (Directory.Exists(path)) Directory.SetLastWriteTime(path, timestamp);
                else File.SetLastWriteTime(path, timestamp);
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"touch: cannot touch '{operand}': {ex.Message}");
                status = 1;
            }
        }

        return status;
    }

    private static int Ln(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count < 2) throw new ShellException("missing file operand", 2);

        var target = context.ResolvePath(options.Operands[0]);
        var link = context.ResolvePath(options.Operands[1]);
        if (Directory.Exists(link)) link = Path.Combine(link, Path.GetFileName(target));

        try
        {
            if (options.Has('s'))
            {
                if (Directory.Exists(target)) Directory.CreateSymbolicLink(link, target);
                else File.CreateSymbolicLink(link, target);
            }
            else
            {
                // No hard-link API in .NET; copying is the closest honest fallback.
                File.Copy(target, link, overwrite: options.Has('f'));
                Interpreter.WriteError(context, "ln: hard links are not supported; the file was copied instead");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Interpreter.WriteError(context, $"ln: failed to create link '{options.Operands[1]}': {ex.Message}");
            return 1;
        }
    }

    private static int ReadLink(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);
            var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
            var target = info.LinkTarget;

            if (target is null && !options.Has('f')) { status = 1; continue; }
            context.Io.Out.WriteLine((target ?? path).Replace('\\', '/'));
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int RealPath(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);

            if (!options.Has('m') && !File.Exists(path) && !Directory.Exists(path))
            {
                Interpreter.WriteError(context, $"realpath: {operand}: No such file or directory");
                status = 1;
                continue;
            }

            context.Io.Out.WriteLine(path.Replace('\\', '/'));
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Basename(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("missing operand", 2);

        var name = args[1].TrimEnd('/', '\\');
        var index = name.LastIndexOfAny(new[] { '/', '\\' });
        if (index >= 0) name = name[(index + 1)..];

        if (args.Count > 2 && name.EndsWith(args[2], StringComparison.Ordinal) && name != args[2])
            name = name[..^args[2].Length];

        context.Io.Out.WriteLine(name);
        context.Io.Out.Flush();
        return 0;
    }

    private static int Dirname(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("missing operand", 2);

        foreach (var operand in args.Skip(1))
        {
            var name = operand.TrimEnd('/', '\\');
            var index = name.LastIndexOfAny(new[] { '/', '\\' });
            context.Io.Out.WriteLine(index switch
            {
                < 0 => ".",
                0 => "/",
                _ => name[..index].Replace('\\', '/'),
            });
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Stat(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "c");
        var status = 0;

        foreach (var operand in options.Operands)
        {
            var path = context.ResolvePath(operand);
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

            if (!info.Exists)
            {
                Interpreter.WriteError(context, $"stat: cannot stat '{operand}': No such file or directory");
                status = 1;
                continue;
            }

            var size = info is FileInfo file ? file.Length : 0;

            if (options.Value('c') is { } format)
            {
                context.Io.Out.WriteLine(format
                    .Replace("%n", info.Name)
                    .Replace("%s", size.ToString())
                    .Replace("%F", info is DirectoryInfo ? "directory" : "regular file")
                    .Replace("%y", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Replace("%Y", new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds().ToString())
                    .Replace("%A", Mode(info)));
                continue;
            }

            context.Io.Out.WriteLine($"  File: {info.Name}");
            context.Io.Out.WriteLine($"  Size: {size,-12} {(info is DirectoryInfo ? "directory" : "regular file")}");
            context.Io.Out.WriteLine($"Access: ({Mode(info)})  Uid: ({Environment.UserName})");
            context.Io.Out.WriteLine($"Modify: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            context.Io.Out.WriteLine($"Change: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Du(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var paths = options.Operands.Count > 0 ? options.Operands : new List<string> { "." };

        foreach (var operand in paths)
        {
            var path = context.ResolvePath(operand);
            var total = Measure(path, operand, context, options, top: true);

            if (options.Has('s'))
                context.Io.Out.WriteLine($"{FormatBlocks(total, options)}\t{operand}");
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static long Measure(string path, string display, ShellContext context, CommandLine options, bool top)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var length = new FileInfo(file).Length;
                total += length;
                if (options.Has('a') && !options.Has('s'))
                    context.Io.Out.WriteLine($"{FormatBlocks(length, options)}\t{display}/{Path.GetFileName(file)}");
            }

            foreach (var directory in Directory.EnumerateDirectories(path))
                total += Measure(directory, $"{display}/{Path.GetFileName(directory)}", context, options, top: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we cannot read, as du does.
        }

        if (!options.Has('s')) context.Io.Out.WriteLine($"{FormatBlocks(total, options)}\t{display}");
        return total;
    }

    private static string FormatBlocks(long bytes, CommandLine options) =>
        options.Has('h') ? HumanSize(bytes) : ((bytes + 1023) / 1024).ToString();

    private static int Df(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var human = options.Has('h');

        context.Io.Out.WriteLine($"{"Filesystem",-16}{"Size",-10}{"Used",-10}{"Avail",-10}{"Use%",-6}Mounted on");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var size = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var used = size - free;
            var percent = size > 0 ? (int)(used * 100 / size) : 0;

            string Format(long value) => human ? HumanSize(value) : ((value + 1023) / 1024).ToString();

            context.Io.Out.WriteLine(
                $"{drive.Name,-16}{Format(size),-10}{Format(used),-10}{Format(free),-10}{percent + "%",-6}{drive.Name.Replace('\\', '/')}");
        }

        context.Io.Out.Flush();
        return 0;
    }

    // ---------------------------------------------------------------- find

    private static int Find(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var roots = new List<string>();
        var i = 1;

        while (i < args.Count && !args[i].StartsWith('-')) roots.Add(args[i++]);
        if (roots.Count == 0) roots.Add(".");

        string? namePattern = null;
        var ignoreCase = false;
        char? type = null;
        var maxDepth = int.MaxValue;
        var minDepth = 0;
        List<string>? execCommand = null;
        var wantEmpty = false;
        string? pathPattern = null;

        for (; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-name" when i + 1 < args.Count: namePattern = args[++i]; break;
                case "-iname" when i + 1 < args.Count: namePattern = args[++i]; ignoreCase = true; break;
                case "-path" or "-wholename" when i + 1 < args.Count: pathPattern = args[++i]; break;
                case "-type" when i + 1 < args.Count: type = args[++i][0]; break;
                case "-maxdepth" when i + 1 < args.Count: maxDepth = int.Parse(args[++i]); break;
                case "-mindepth" when i + 1 < args.Count: minDepth = int.Parse(args[++i]); break;
                case "-empty": wantEmpty = true; break;
                case "-print" or "-print0": break;
                case "-exec":
                    execCommand = new List<string>();
                    while (++i < args.Count && args[i] != ";" && args[i] != "\\;") execCommand.Add(args[i]);
                    break;
                default:
                    Interpreter.WriteError(context, $"find: unknown predicate `{args[i]}'");
                    return 2;
            }
        }

        var status = 0;

        foreach (var root in roots)
        {
            var resolved = context.ResolvePath(root);

            if (!Directory.Exists(resolved) && !File.Exists(resolved))
            {
                Interpreter.WriteError(context, $"find: '{root}': No such file or directory");
                status = 1;
                continue;
            }

            Walk(resolved, root, 0);
        }

        context.Io.Out.Flush();
        return status;

        void Walk(string path, string display, int depth)
        {
            if (depth > maxDepth) return;

            var isDirectory = Directory.Exists(path);

            if (depth >= minDepth && Matches(path, display, isDirectory))
            {
                if (execCommand is not null) RunExec(display);
                else context.Io.Out.WriteLine(display.Replace('\\', '/'));
            }

            if (!isDirectory || depth >= maxDepth) return;

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(path).OrderBy(e => e, StringComparer.OrdinalIgnoreCase); }
            catch (UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
                Walk(entry, $"{display}/{Path.GetFileName(entry)}", depth + 1);
        }

        bool Matches(string path, string display, bool isDirectory)
        {
            if (type is 'f' && isDirectory) return false;
            if (type is 'd' && !isDirectory) return false;

            if (namePattern is not null && !ShellPattern.IsMatch(Path.GetFileName(path.TrimEnd('\\', '/')), namePattern, ignoreCase))
                return false;

            if (pathPattern is not null && !ShellPattern.IsMatch(display.Replace('\\', '/'), pathPattern)) return false;

            if (wantEmpty)
            {
                if (isDirectory && Directory.EnumerateFileSystemEntries(path).Any()) return false;
                if (!isDirectory && new FileInfo(path).Length > 0) return false;
            }

            return true;
        }

        void RunExec(string display)
        {
            var expanded = execCommand!.Select(a => a == "{}" ? display : a).ToList();
            var script = string.Join(' ', expanded.Select(Interpreter.SingleQuote));
            interpreter.Run(script, context);
        }
    }

    private static int Tree(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "L");
        var root = options.Operands.Count > 0 ? options.Operands[0] : ".";
        var maxDepth = int.TryParse(options.Value('L'), out var parsed) ? parsed : int.MaxValue;
        var path = context.ResolvePath(root);

        if (!Directory.Exists(path))
        {
            Interpreter.WriteError(context, $"tree: {root}: No such directory");
            return 1;
        }

        var directories = 0;
        var files = 0;

        context.Io.Out.WriteLine(root);
        Walk(path, string.Empty, 1);
        context.Io.Out.WriteLine();
        context.Io.Out.WriteLine($"{directories} directories, {files} files");
        context.Io.Out.Flush();
        return 0;

        void Walk(string directory, string prefix, int depth)
        {
            if (depth > maxDepth) return;

            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).GetFileSystemInfos()
                    .Where(e => options.Has('a') || !IsHidden(e))
                    .Where(e => !options.Has('d') || e is DirectoryInfo)
                    .OrderBy(e => e is not DirectoryInfo)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var last = index == entries.Count - 1;
                context.Io.Out.WriteLine($"{prefix}{(last ? "└── " : "├── ")}{Decorate(entry, options)}");

                if (entry is DirectoryInfo)
                {
                    directories++;
                    Walk(entry.FullName, prefix + (last ? "    " : "│   "), depth + 1);
                }
                else
                {
                    files++;
                }
            }
        }
    }

    private static int Chmod(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, string.Empty, stopAtFirstOperand: true);
        if (options.Operands.Count < 2) throw new ShellException("missing operand", 2);

        var mode = options.Operands[0];
        var status = 0;

        // Windows has no POSIX permission bits; only the read-only attribute maps across.
        bool? makeWritable = mode switch
        {
            _ when mode.Contains("+w") => true,
            _ when mode.Contains("-w") => false,
            _ when mode.Length == 3 && mode.All(char.IsAsciiDigit) => (mode[0] - '0' & 2) != 0,
            _ => null,
        };

        if (makeWritable is null) return 0;

        foreach (var operand in options.Operands.Skip(1))
        {
            var path = context.ResolvePath(operand);

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Interpreter.WriteError(context, $"chmod: cannot access '{operand}': No such file or directory");
                    status = 1;
                    continue;
                }

                var attributes = File.GetAttributes(path);
                File.SetAttributes(path, makeWritable.Value
                    ? attributes & ~FileAttributes.ReadOnly
                    : attributes | FileAttributes.ReadOnly);
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"chmod: {operand}: {ex.Message}");
                status = 1;
            }
        }

        return status;
    }

    private static int MkTemp(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var path = Path.Combine(Path.GetTempPath(), "tmp." + Guid.NewGuid().ToString("N")[..10]);

        if (options.Has('d')) Directory.CreateDirectory(path);
        else File.WriteAllBytes(path, Array.Empty<byte>());

        context.Io.Out.WriteLine(path.Replace('\\', '/'));
        context.Io.Out.Flush();
        return 0;
    }
}
