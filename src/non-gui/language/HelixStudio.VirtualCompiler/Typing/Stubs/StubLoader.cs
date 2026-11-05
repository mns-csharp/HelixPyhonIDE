using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    /// <summary>
    /// Loads <c>.pyi</c> stubs bundled as embedded resources in
    /// <c>HelixPythonNamespace.VirtualCompiler</c> (the curated stdlib subset under
    /// <c>Resources/Typeshed/stdlib/</c>). Exposes a cached singleton
    /// <see cref="BuiltinIndex"/> so the engine doesn't re-parse on every
    /// compile call.
    /// </summary>
    public static class StubLoader
    {
        private const string ResourcePrefix =
            "HelixPythonNamespace.VirtualCompiler.Resources.Typeshed.stdlib.";

        private static readonly object _syncRoot = new object();
        private static TypeshedIndex _cachedIndex;

        /// <summary>
        /// Lazy singleton for the bundled stdlib stub set. Parses each
        /// embedded <c>.pyi</c> file exactly once per process.
        /// </summary>
        public static TypeshedIndex BuiltinIndex
        {
            get
            {
                if (_cachedIndex != null) return _cachedIndex;
                lock (_syncRoot)
                {
                    if (_cachedIndex == null)
                    {
                        _cachedIndex = LoadFromAssembly(typeof(StubLoader).Assembly);
                    }
                    return _cachedIndex;
                }
            }
        }

        public static TypeshedIndex LoadFromAssembly(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            TypeshedIndex idx = new TypeshedIndex();
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                if (!resourceName.EndsWith(".pyi", StringComparison.Ordinal)) continue;

                // "HelixPythonNamespace.VirtualCompiler.Resources.Typeshed.stdlib.builtins.pyi"
                //   → module name "builtins"
                // "HelixPythonNamespace.VirtualCompiler.Resources.Typeshed.stdlib.os.path.pyi"
                //   → module name "os.path"
                string middle = resourceName.Substring(ResourcePrefix.Length);
                string moduleName = middle.Substring(0, middle.Length - ".pyi".Length);

                string content;
                using (Stream s = assembly.GetManifestResourceStream(resourceName))
                {
                    if (s == null) continue;
                    using (StreamReader reader = new StreamReader(s))
                    {
                        content = reader.ReadToEnd();
                    }
                }

                StubModule m = StubParser.Parse(content, moduleName);
                idx.Add(m);
            }
            return idx;
        }

        /// <summary>
        /// Loads stubs from a directory tree on disk. Useful for testing or
        /// ad-hoc overrides — scans every <c>.pyi</c> under the root and uses
        /// the path (relative to root) as the dotted module name.
        /// </summary>
        public static TypeshedIndex LoadFromDirectory(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) throw new ArgumentNullException(nameof(rootPath));
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException(rootPath);

            TypeshedIndex idx = new TypeshedIndex();
            foreach (string file in Directory.GetFiles(rootPath, "*.pyi", SearchOption.AllDirectories))
            {
                string rel = file.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                rel = rel.Substring(0, rel.Length - ".pyi".Length);
                string moduleName = rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
                string content = File.ReadAllText(file);
                idx.Add(StubParser.Parse(content, moduleName));
            }
            return idx;
        }

        /// <summary>Clears the cached singleton — testing hook.</summary>
        public static void ResetCacheForTests()
        {
            lock (_syncRoot) { _cachedIndex = null; }
        }
    }
}
