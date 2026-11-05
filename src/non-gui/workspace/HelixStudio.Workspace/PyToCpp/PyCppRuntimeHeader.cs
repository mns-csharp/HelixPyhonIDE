using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    /// <summary>
    /// Reads the embedded pyrt.hpp resource (the real, hand-maintained file at
    /// PyToCpp/runtime/pyrt.hpp, embedded via the .csproj — see that file for the runtime
    /// library itself). Embedding rather than reading from a repo-relative path means a
    /// shipped build doesn't depend on the source tree still being on disk next to the exe.
    /// </summary>
    public static class PyCppRuntimeHeader
    {
        public const string FileName = "pyrt.hpp";

        private static readonly Lazy<string> _content = new Lazy<string>(Load);

        public static string Content => _content.Value;

        private static string Load()
        {
            Assembly asm = typeof(PyCppRuntimeHeader).Assembly;
            string resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".pyrt.hpp", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
                throw new InvalidOperationException("pyrt.hpp embedded resource not found in " + asm.FullName + " — check the EmbeddedResource entry in HelixStudio.Workspace.csproj");
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }
}
