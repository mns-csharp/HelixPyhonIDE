namespace HelixPythonNamespace.AddIns
{
    /// <summary>
    /// An add-in the IDE loads at startup from <c>addins\&lt;Name&gt;\*.dll</c> beside
    /// HelixPython.exe. The loader creates every public, non-abstract implementation that has a
    /// parameterless constructor and calls <see cref="Initialize"/> once.
    /// </summary>
    public interface IHelixAddIn
    {
        /// <summary>A stable key, for example "cg-designer". The host keys the add-in's settings on it.</summary>
        string Id { get; }

        string DisplayName { get; }

        /// <summary>Register tabs, menu items, file openers and commands with the host.</summary>
        void Initialize(IAddInHost host);

        /// <summary>Called when the IDE closes. Each add-in's call is isolated from the others.</summary>
        void Shutdown();
    }
}
