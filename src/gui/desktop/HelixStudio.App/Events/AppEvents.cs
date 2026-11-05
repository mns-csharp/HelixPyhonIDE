namespace HelixPythonNamespace.App.Events
{
    using HelixPythonNamespace.Core.Model;

    // Lightweight notification records carried on the IAppEventBus. Kept as simple
    // classes (C# 7.3 — no record types) with read-only fields set via constructor.
    // New shell events are added here as coordinators are extracted from MainForm.

    /// <summary>Raised after a solution has been loaded and projected onto the canvas.</summary>
    public sealed class SolutionOpenedEvent
    {
        public SolutionOpenedEvent(SolutionModel solution)
        {
            Solution = solution;
        }

        public SolutionModel Solution { get; private set; }
    }

    /// <summary>Raised after the current solution has been closed.</summary>
    public sealed class SolutionClosedEvent
    {
    }

    /// <summary>Raised when a top-level panel/tab is shown or hidden.</summary>
    public sealed class TabToggledEvent
    {
        public TabToggledEvent(string tabKey, bool visible)
        {
            TabKey = tabKey;
            Visible = visible;
        }

        public string TabKey { get; private set; }

        public bool Visible { get; private set; }
    }

    /// <summary>Raised to request navigation to a source location (file + 1-based line).</summary>
    public sealed class NavigateToFileEvent
    {
        public NavigateToFileEvent(string filePath, int line)
        {
            FilePath = filePath;
            Line = line;
        }

        public string FilePath { get; private set; }

        public int Line { get; private set; }
    }
}
