using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Expansion;

/// <summary>Pathname expansion: turns a (possibly escaped) glob pattern into matching paths.</summary>
public static class PathGlob
{
    /// <summary>
    /// Expands <paramref name="pattern"/> relative to <paramref name="workingDirectory"/>.
    /// Returns null when the pattern matched nothing, so the caller can fall back to the
    /// unexpanded word the way bash does with <c>nullglob</c> off.
    /// </summary>
    public static List<string>? Expand(string pattern, string workingDirectory)
    {
        if (!ShellPattern.HasMetacharacters(pattern)) return null;

        var (root, components, rooted) = Split(pattern, workingDirectory);
        if (components.Count == 0) return null;

        var results = new List<string>();
        Walk(root, components, 0, rooted ? root : string.Empty, results);
        if (results.Count == 0) return null;

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>Separates the fixed root (drive, leading slash, or the cwd) from the pattern components.</summary>
    private static (string Root, List<string> Components, bool Rooted) Split(string pattern, string workingDirectory)
    {
        var root = workingDirectory;
        var rooted = false;
        var rest = pattern;

        // "C:/foo" or "C:\foo"
        if (rest.Length >= 2 && char.IsLetter(rest[0]) && rest[1] == ':')
        {
            root = rest[..2] + Path.DirectorySeparatorChar;
            rest = rest.Length > 2 && (rest[2] == '/' || rest[2] == '\\') ? rest[3..] : rest[2..];
            rooted = true;
        }
        else if (rest.StartsWith("//") || rest.StartsWith("\\\\"))
        {
            // UNC paths are not globbed at the share level; treat the whole thing as literal.
            return (root, new List<string>(), false);
        }
        else if (rest.StartsWith('/') || rest.StartsWith('\\'))
        {
            root = Path.GetPathRoot(workingDirectory) ?? "\\";
            rest = rest[1..];
            rooted = true;
        }

        var components = new List<string>();
        var sb = new StringBuilder();

        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];

            if (c == '\\' && i + 1 < rest.Length && rest[i + 1] is not ('/' or '\\'))
            {
                sb.Append(c).Append(rest[i + 1]);
                i++;
                continue;
            }

            if (c is '/' or '\\')
            {
                components.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        components.Add(sb.ToString());
        return (root, components, rooted);
    }

    private static void Walk(string directory, List<string> components, int index, string prefix, List<string> results)
    {
        while (true)
        {
            if (index >= components.Count)
            {
                if (prefix.Length > 0) results.Add(prefix);
                return;
            }

            var component = components[index];
            var last = index == components.Count - 1;

            if (component.Length == 0)
            {
                // Collapsed separator, e.g. "a//b".
                index++;
                continue;
            }

            if (!ShellPattern.HasMetacharacters(component))
            {
                var literal = ShellPattern.Unescape(component);
                var next = Path.Combine(directory, literal);
                var nextPrefix = prefix.Length == 0 ? literal : JoinDisplay(prefix, literal);

                if (last)
                {
                    if (File.Exists(next) || Directory.Exists(next)) results.Add(nextPrefix);
                    return;
                }

                if (!Directory.Exists(next)) return;
                directory = next;
                prefix = nextPrefix;
                index++;
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = last
                    ? Directory.EnumerateFileSystemEntries(directory)
                    : Directory.EnumerateDirectories(directory);
            }
            catch (Exception)
            {
                return; // unreadable directory: nothing matches below it
            }

            // A leading '.' is only matched by a pattern that spells it out.
            var matchHidden = component.StartsWith('.');

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.Length == 0) continue;
                if (!matchHidden && name.StartsWith('.')) continue;
                if (!ShellPattern.IsMatch(name, component, ignoreCase: true)) continue;

                var nextPrefix = prefix.Length == 0 ? name : JoinDisplay(prefix, name);

                if (last) results.Add(nextPrefix);
                else Walk(entry, components, index + 1, nextPrefix, results);
            }

            return;
        }
    }

    /// <summary>Joins path pieces with '/', which is what a shell user expects to see.</summary>
    private static string JoinDisplay(string left, string right) =>
        left.EndsWith('/') || left.EndsWith('\\') ? left + right : left + "/" + right;
}
