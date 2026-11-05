using System.Globalization;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Parsing;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>
/// Evaluates <c>[[ ... ]]</c> expressions and the argument form used by <c>test</c> / <c>[</c>.
/// Both share the same file and string predicates; only the syntax differs.
/// </summary>
public static class ConditionalEvaluator
{
    public static bool Evaluate(ConditionalNode node, ShellContext context)
    {
        var expander = new Expander(context);
        return Evaluate(node, context, expander);
    }

    private static bool Evaluate(ConditionalNode node, ShellContext context, Expander expander)
    {
        switch (node)
        {
            case ConditionalNot not:
                return !Evaluate(not.Operand, context, expander);

            case ConditionalAndOr andOr:
                return andOr.IsAnd
                    ? Evaluate(andOr.Left, context, expander) && Evaluate(andOr.Right, context, expander)
                    : Evaluate(andOr.Left, context, expander) || Evaluate(andOr.Right, context, expander);

            case ConditionalWord word:
                return expander.ExpandToString(word.Word).Length > 0;

            case ConditionalUnary unary:
                return UnaryTest(unary.Operator, expander.ExpandToString(unary.Operand), context);

            case ConditionalBinary binary:
            {
                var left = expander.ExpandToString(binary.Left);

                // Inside [[ ]] the right operand of == and != is a pattern unless it was quoted.
                if (binary.Operator is "==" or "=" or "!=")
                {
                    var pattern = binary.RightIsLiteral
                        ? ShellPattern.Escape(expander.ExpandToString(binary.Right))
                        : expander.ExpandToPattern(binary.Right);

                    var matched = ShellPattern.IsMatch(left, pattern);
                    return binary.Operator == "!=" ? !matched : matched;
                }

                if (binary.Operator == "=~")
                {
                    var pattern = expander.ExpandToString(binary.Right);

                    try
                    {
                        var match = Regex.Match(left, pattern);
                        if (match.Success)
                        {
                            var groups = new List<string>();
                            for (var i = 0; i < match.Groups.Count; i++) groups.Add(match.Groups[i].Value);
                            context.SetArray("BASH_REMATCH", groups);
                        }

                        return match.Success;
                    }
                    catch (ArgumentException ex)
                    {
                        throw new ShellException($"{pattern}: invalid regular expression: {ex.Message}", 2);
                    }
                }

                return BinaryTest(binary.Operator, left, expander.ExpandToString(binary.Right), context);
            }

            default:
                return false;
        }
    }

    // ------------------------------------------------------ test / [ form

    /// <summary>Evaluates the argument list of <c>test</c>, which is a small grammar of its own.</summary>
    public static bool EvaluateTest(IReadOnlyList<string> arguments, ShellContext context)
    {
        var position = 0;
        var result = ParseOr(arguments, ref position, context);

        if (position != arguments.Count)
            throw new ShellException($"too many arguments", 2);

        return result;
    }

    private static bool ParseOr(IReadOnlyList<string> args, ref int i, ShellContext context)
    {
        var left = ParseAnd(args, ref i, context);

        while (i < args.Count && args[i] == "-o")
        {
            i++;
            var right = ParseAnd(args, ref i, context);
            left = left || right;
        }

        return left;
    }

    private static bool ParseAnd(IReadOnlyList<string> args, ref int i, ShellContext context)
    {
        var left = ParseTerm(args, ref i, context);

        while (i < args.Count && args[i] == "-a")
        {
            i++;
            var right = ParseTerm(args, ref i, context);
            left = left && right;
        }

        return left;
    }

    private static bool ParseTerm(IReadOnlyList<string> args, ref int i, ShellContext context)
    {
        if (i >= args.Count) return false;

        if (args[i] == "!")
        {
            i++;
            return !ParseTerm(args, ref i, context);
        }

        if (args[i] == "(")
        {
            i++;
            var inner = ParseOr(args, ref i, context);
            if (i >= args.Count || args[i] != ")") throw new ShellException("missing `)'", 2);
            i++;
            return inner;
        }

        // Binary form: operand OP operand.
        if (i + 1 < args.Count && IsBinaryOperator(args[i + 1]))
        {
            var left = args[i];
            var op = args[i + 1];
            if (i + 2 >= args.Count) throw new ShellException($"{op}: argument expected", 2);
            var right = args[i + 2];
            i += 3;
            return BinaryTest(op, left, right, context);
        }

        // Unary form: OP operand.
        if (args[i].Length == 2 && args[i][0] == '-' && char.IsLetter(args[i][1]) && i + 1 < args.Count)
        {
            var op = args[i];
            var operand = args[i + 1];
            i += 2;
            return UnaryTest(op, operand, context);
        }

        var value = args[i];
        i++;
        return value.Length > 0;
    }

    private static bool IsBinaryOperator(string text) => text is
        "=" or "==" or "!=" or "<" or ">" or "=~" or
        "-eq" or "-ne" or "-lt" or "-le" or "-gt" or "-ge" or
        "-nt" or "-ot" or "-ef";

    // ---------------------------------------------------------- predicates

    public static bool UnaryTest(string op, string operand, ShellContext context)
    {
        string Path() => context.ResolvePath(operand);

        switch (op)
        {
            case "-z": return operand.Length == 0;
            case "-n": return operand.Length > 0;
            case "-v": return context.IsSet(operand);
            case "-o": return IsOptionSet(operand, context);
            case "-t": return false; // no command runs on a real terminal descriptor here
        }

        var path = Path();

        switch (op)
        {
            case "-e" or "-a": return File.Exists(path) || Directory.Exists(path);
            case "-f": return File.Exists(path);
            case "-d": return Directory.Exists(path);
            case "-s": return File.Exists(path) && new FileInfo(path).Length > 0;
            case "-r": return CanRead(path);
            case "-w": return CanWrite(path);
            case "-x": return IsExecutable(path);
            case "-h" or "-L": return IsSymbolicLink(path);
            case "-G" or "-O": return File.Exists(path) || Directory.Exists(path);
            case "-N":
            {
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                return info.LastWriteTimeUtc > info.LastAccessTimeUtc;
            }
            case "-b" or "-c" or "-p" or "-S" or "-g" or "-k" or "-u":
                return false; // no such file types on Windows
            default:
                throw new ShellException($"{op}: unary operator expected", 2);
        }
    }

    public static bool BinaryTest(string op, string left, string right, ShellContext context)
    {
        switch (op)
        {
            case "=" or "==": return string.Equals(left, right, StringComparison.Ordinal);
            case "!=": return !string.Equals(left, right, StringComparison.Ordinal);
            case "<": return string.CompareOrdinal(left, right) < 0;
            case ">": return string.CompareOrdinal(left, right) > 0;
            case "=~": return Regex.IsMatch(left, right);
        }

        if (op is "-nt" or "-ot" or "-ef")
        {
            var leftPath = context.ResolvePath(left);
            var rightPath = context.ResolvePath(right);

            if (op == "-ef")
                return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);

            var leftTime = LastWrite(leftPath);
            var rightTime = LastWrite(rightPath);
            return op == "-nt" ? leftTime > rightTime : leftTime < rightTime;
        }

        var a = ToNumber(left, op);
        var b = ToNumber(right, op);

        return op switch
        {
            "-eq" => a == b,
            "-ne" => a != b,
            "-lt" => a < b,
            "-le" => a <= b,
            "-gt" => a > b,
            "-ge" => a >= b,
            _ => throw new ShellException($"{op}: binary operator expected", 2),
        };
    }

    private static long ToNumber(string text, string op) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ShellException($"{text}: integer expression expected", 2);

    private static DateTime LastWrite(string path)
    {
        if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
        if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
        return DateTime.MinValue;
    }

    private static bool CanRead(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.EnumerateFileSystemEntries(path).Take(1).ToList(); return true; }
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanWrite(string path)
    {
        try
        {
            if (Directory.Exists(path)) return (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
            if (!File.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsExecutable(string path)
    {
        if (Directory.Exists(path)) return true;
        if (!File.Exists(path)) return false;

        var extension = System.IO.Path.GetExtension(path).ToUpperInvariant();
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToUpperInvariant());

        return pathExt.Contains(extension);
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsOptionSet(string name, ShellContext context) => name switch
    {
        "errexit" => context.Options.ExitOnError,
        "nounset" => context.Options.ErrorOnUnset,
        "xtrace" => context.Options.Trace,
        "noglob" => context.Options.NoGlob,
        "verbose" => context.Options.Verbose,
        "noclobber" => context.Options.NoClobber,
        "pipefail" => context.Options.PipeFail,
        "allexport" => context.Options.AllExport,
        _ => false,
    };
}
