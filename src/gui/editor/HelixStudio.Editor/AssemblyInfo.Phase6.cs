using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HelixStudio.Editor.Tests")]
[assembly: InternalsVisibleTo("HelixStudio.Editor.IntegrationTests")]
// The notebook surface is driven from the shell, so its shell-level tests live in App.Tests and
// need the same view into it (the measured cell layout, the shared cell editor) that the editor's
// own suites have.
[assembly: InternalsVisibleTo("HelixStudio.App.Tests")]
