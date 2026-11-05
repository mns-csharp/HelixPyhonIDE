using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("HelixPython")]
[assembly: AssemblyDescription("OOP Architecture Design Tool")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("HelixPython")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// The test suite drives internal seams directly (menu enable-state handlers, coordinator helpers)
// rather than forcing them public purely for testability.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HelixStudio.App.Tests")]
