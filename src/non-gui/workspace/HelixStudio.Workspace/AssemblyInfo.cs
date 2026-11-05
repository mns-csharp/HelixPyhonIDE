using System.Runtime.CompilerServices;

// CppDebugRunner.SimulateNoDebuggerForTests: the "no gdb, no lldb" branch must be testable on a
// machine that has both installed.
[assembly: InternalsVisibleTo("HelixStudio.Workspace.Tests")]
