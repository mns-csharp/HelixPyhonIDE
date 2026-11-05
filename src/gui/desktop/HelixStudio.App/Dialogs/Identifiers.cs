namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Shared name validation for the naming dialogs (Rename, Node editor, Add nested type, …) so
    /// they agree on what a legal identifier is instead of each carrying its own copy.
    /// </summary>
    internal static class Identifiers
    {
        /// <summary>
        /// A syntactically valid identifier: non-empty, starts with a letter or underscore, and
        /// otherwise contains only letters, digits, or underscores (Unicode-aware, matching Python 3
        /// and — for the ASCII subset — C/C++).
        /// </summary>
        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            for (int i = 1; i < name.Length; i++)
                if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) return false;
            return true;
        }
    }
}
