using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>
/// OSC 8 hyperlinks to local paths. <c>ls</c> wraps the names it prints to the window in them, so
/// the window knows which cells belong to which file; the window turns each link back into a path.
/// </summary>
public static class TerminalLinks
{
    private const string Open = "\x1b]8;;";
    private const string Terminator = "\x1b\\";

    /// <summary>Printable ASCII a URI path may hold as it is. Everything else is percent-encoded.</summary>
    private const string Safe = "-._~/:!$&'()*+,;=@";

    /// <summary>Wraps text in an OSC 8 hyperlink to a local path. The escapes take no columns.</summary>
    public static string Wrap(string text, string fullPath) =>
        Open + ToUri(fullPath) + Terminator + text + Open + Terminator;

    /// <summary>
    /// <c>C:\Users\A b</c> becomes <c>file:///C:/Users/A%20b</c>, and a UNC path
    /// <c>\\server\share\x</c> becomes <c>file://server/share/x</c>.
    /// </summary>
    public static string ToUri(string fullPath)
    {
        var path = fullPath.Replace('\\', '/');
        var uri = new StringBuilder("file://");

        // A UNC path names its host where a URI puts one; a drive path has an empty host.
        if (path.StartsWith("//", StringComparison.Ordinal)) path = path[2..];
        else uri.Append('/');

        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || Safe.Contains(c))) uri.Append(c);
            else uri.Append('%').Append(b.ToString("X2"));
        }

        return uri.ToString();
    }

    /// <summary>The Windows path a file URI names, or null when it is not a file URI.</summary>
    public static string? FromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;

        var rest = uri["file://".Length..];
        var path = rest.StartsWith('/') ? rest[1..] : "//" + rest;
        return Uri.UnescapeDataString(path).Replace('/', '\\');
    }

    /// <summary>
    /// Wraps a path so the shell reads it as one word whatever is in it. Single quotes take
    /// everything literally, and the one thing they cannot hold — a single quote — is closed,
    /// escaped and reopened, which is the same trick a shell script would use.
    /// </summary>
    public static string QuoteForShell(string path) =>
        "'" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";
}
