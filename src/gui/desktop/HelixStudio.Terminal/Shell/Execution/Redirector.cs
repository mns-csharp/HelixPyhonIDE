using System.Text;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Parsing;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>Builds the <see cref="ShellIo"/> a command runs with by applying its redirections in order.</summary>
public static class Redirector
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static ShellIo Apply(ShellIo baseIo, IReadOnlyList<Redirection> redirections, ShellContext context, Expander expander)
    {
        if (redirections.Count == 0) return baseIo;

        var input = baseIo.Input;
        var output = baseIo.Output;
        var error = baseIo.Error;
        var owned = new List<IDisposable>();

        try
        {
            foreach (var redirection in redirections)
            {
                switch (redirection.Type)
                {
                    case RedirectionType.Input:
                    case RedirectionType.ReadWrite:
                    {
                        var path = ResolveTarget(redirection, context, expander);
                        var stream = redirection.Type == RedirectionType.ReadWrite
                            ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
                            : OpenRead(path);
                        owned.Add(stream);
                        AssignInput(redirection.FileDescriptor ?? 0, stream, ref input, ref output, ref error);
                        break;
                    }

                    case RedirectionType.Output:
                    case RedirectionType.Clobber:
                    case RedirectionType.Append:
                    {
                        var path = ResolveTarget(redirection, context, expander);
                        var append = redirection.Type == RedirectionType.Append;

                        if (!append && redirection.Type == RedirectionType.Output && context.Options.NoClobber && File.Exists(path))
                            throw new ShellException($"{path}: cannot overwrite existing file");

                        var stream = OpenWrite(path, append);
                        owned.Add(stream);
                        AssignOutput(redirection.FileDescriptor ?? 1, stream, ref output, ref error);
                        break;
                    }

                    case RedirectionType.OutputAndError:
                    case RedirectionType.AppendOutputAndError:
                    {
                        var path = ResolveTarget(redirection, context, expander);
                        var stream = OpenWrite(path, redirection.Type == RedirectionType.AppendOutputAndError);
                        owned.Add(stream);
                        output = stream;
                        error = stream;
                        break;
                    }

                    case RedirectionType.HereDocument:
                    {
                        var body = redirection.HereDocument?.Body ?? string.Empty;
                        if (redirection.HereDocument is { Quoted: false })
                            body = expander.ExpandToString(Lexing.Lexer.SubWord(body));

                        var stream = new MemoryStream(Utf8NoBom.GetBytes(body));
                        owned.Add(stream);
                        AssignInput(redirection.FileDescriptor ?? 0, stream, ref input, ref output, ref error);
                        break;
                    }

                    case RedirectionType.HereString:
                    {
                        var text = expander.ExpandToString(redirection.Target) + "\n";
                        var stream = new MemoryStream(Utf8NoBom.GetBytes(text));
                        owned.Add(stream);
                        AssignInput(redirection.FileDescriptor ?? 0, stream, ref input, ref output, ref error);
                        break;
                    }

                    case RedirectionType.DuplicateOutput:
                    {
                        var source = redirection.TargetDescriptor switch
                        {
                            0 => input,
                            1 => output,
                            2 => error,
                            _ => throw new ShellException($"{redirection.TargetDescriptor}: bad file descriptor"),
                        };

                        AssignOutput(redirection.FileDescriptor ?? 1, source, ref output, ref error);
                        break;
                    }

                    case RedirectionType.DuplicateInput:
                    {
                        var source = redirection.TargetDescriptor switch
                        {
                            0 => input,
                            1 => output,
                            2 => error,
                            _ => throw new ShellException($"{redirection.TargetDescriptor}: bad file descriptor"),
                        };

                        AssignInput(redirection.FileDescriptor ?? 0, source, ref input, ref output, ref error);
                        break;
                    }

                    case RedirectionType.CloseDescriptor:
                    {
                        switch (redirection.FileDescriptor ?? 1)
                        {
                            case 0: input = Stream.Null; break;
                            case 1: output = Stream.Null; break;
                            case 2: error = Stream.Null; break;
                        }

                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            foreach (var item in owned) { try { item.Dispose(); } catch (Exception) { } }
            throw;
        }

        var io = new ShellIo(input, output, error);
        io.Owned.AddRange(owned);
        return io;
    }

    private static void AssignInput(int fd, Stream stream, ref Stream input, ref Stream output, ref Stream error)
    {
        switch (fd)
        {
            case 0: input = stream; break;
            case 1: output = stream; break;
            case 2: error = stream; break;
            default: throw new ShellException($"{fd}: only file descriptors 0, 1 and 2 are supported");
        }
    }

    private static void AssignOutput(int fd, Stream stream, ref Stream output, ref Stream error)
    {
        switch (fd)
        {
            case 1: output = stream; break;
            case 2: error = stream; break;
            case 0: throw new ShellException("0: cannot redirect standard input for output");
            default: throw new ShellException($"{fd}: only file descriptors 0, 1 and 2 are supported");
        }
    }

    private static string ResolveTarget(Redirection redirection, ShellContext context, Expander expander)
    {
        var raw = expander.ExpandToString(redirection.Target);
        if (raw.Length == 0) throw new ShellException("ambiguous redirect");

        if (raw is "/dev/null" or "/dev/zero") return "NUL";
        return context.ResolvePath(raw);
    }

    private static Stream OpenRead(string path)
    {
        if (path.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return Stream.Null;

        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            throw new ShellException($"{path}: No such file or directory");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ShellException($"{path}: No such file or directory");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ShellException($"{path}: Permission denied");
        }
    }

    private static Stream OpenWrite(string path, bool append)
    {
        if (path.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return Stream.Null;

        try
        {
            return new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (DirectoryNotFoundException)
        {
            throw new ShellException($"{path}: No such file or directory");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ShellException($"{path}: Permission denied");
        }
        catch (IOException ex)
        {
            throw new ShellException($"{path}: {ex.Message}");
        }
    }
}
