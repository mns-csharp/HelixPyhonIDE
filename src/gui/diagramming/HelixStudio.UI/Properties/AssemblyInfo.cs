using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("HelixStudio.UI")]
[assembly: AssemblyDescription("OOP Architecture Design Tool")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("HelixPython")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
// The tree-behavior suites exercise ProjectTreePanel's internal surface
// (BuildContextMenuForNode, the tree clipboard) directly instead of via reflection.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HelixStudio.App.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HelixStudio.UI.Tests")]
