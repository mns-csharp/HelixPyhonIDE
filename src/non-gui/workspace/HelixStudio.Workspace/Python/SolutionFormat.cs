namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Which file path the scaffolder returns as <c>SolutionModel.FilePath</c>.
    /// Both files are always written on disk — this just picks the user's
    /// preferred anchor for Recent Files, the window title, and persistence keys.
    /// </summary>
    public enum SolutionFormat
    {
        /// <summary>Return the <c>pyproject.toml</c> path.</summary>
        Toml = 0,

        /// <summary>Return the companion <c>&lt;name&gt;.sln</c> path.</summary>
        Sln = 1,
    }
}
