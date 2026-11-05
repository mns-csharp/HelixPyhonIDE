namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using HelixPythonNamespace.AddIns;

    /// <summary>What one DLL offers, read without loading it into the running IDE.</summary>
    internal sealed class AddInProbe
    {
        /// <summary>Why the DLL is not an add-in; null when it is one.</summary>
        public string Error;
        public string AssemblyName;
        public Version Version;
        public readonly List<KeyValuePair<string, string>> AddIns = new List<KeyValuePair<string, string>>();   // Id, DisplayName
    }

    /// <summary>
    /// Preferences ▸ Install Add-in ▸ Install (spec 001, §4.6). The chosen DLL is checked before
    /// anything is copied; a refusal changes nothing on disk or in settings.
    /// </summary>
    internal static class AddInInstaller
    {
        private const string AddInInterface = "HelixPythonNamespace.AddIns.IHelixAddIn";

        // Keyed by path + write time: a probe executes the DLL's constructors in a throw-away
        // assembly copy, and the catalog asks again on every refresh.
        private static readonly Dictionary<string, AddInProbe> ProbeCache = new Dictionary<string, AddInProbe>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Installs the add-in in <paramref name="dll"/> and loads it at once. Returns null on
        /// success, otherwise the reason it was refused (nothing was copied or changed then).
        /// </summary>
        internal static string Install(MainForm form, string dll)
        {
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) return "The file " + dll + " does not exist.";
            dll = Path.GetFullPath(dll);

            AddInProbe probe = Probe(dll);
            if (probe.Error != null) return probe.Error;

            foreach (KeyValuePair<string, string> addIn in probe.AddIns)
            {
                LoadedAddIn loaded = form.LoadedAddIns.FirstOrDefault(a => string.Equals(a.Id, addIn.Key, StringComparison.Ordinal));
                if (loaded != null)
                    return "An add-in with Id '" + addIn.Key + "' (" + loaded.DisplayName + ") is already loaded. Remove it first.";
            }

            UserSettings settings = form.AddInUserSettings;
            string[] ids = probe.AddIns.Select(a => a.Key).ToArray();

            // A removed bundled add-in is still in its folder: take it off the list and load it.
            string bundled = FindFolderWith(AddInLoader.BundledRoot, ids);
            if (bundled != null && ids.Any(id => AddInLoader.IsRemoved(settings, id)))
            {
                Unremove(form, settings, ids);
                AddInLoader.LoadFolder(form, bundled, AddInKind.Bundled);
                return LoadedOrReason(form, ids, dll);
            }

            string target = Path.Combine(AddInLoader.UserRoot, probe.AssemblyName);
            if (settings != null && settings.PendingAddInDeletes != null
                && settings.PendingAddInDeletes.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                // Removed earlier in this session: its files are still there (Windows locks a loaded
                // DLL) and its assembly is still in memory, so the copy would change nothing until
                // a restart. Keep the folder instead of deleting it at the next start.
                settings.PendingAddInDeletes.RemoveAll(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                string copyError = CopyAddIn(dll, target);
                if (copyError != null) return copyError;
            }

            Unremove(form, settings, ids);
            AddInLoader.LoadDll(form, Path.Combine(target, Path.GetFileName(dll)), target, AddInKind.User);
            return LoadedOrReason(form, ids, dll);
        }

        private static string LoadedOrReason(MainForm form, string[] ids, string dll)
        {
            if (ids.All(id => form.LoadedAddIns.Any(a => a.Id == id))) return null;
            return "The add-in in " + dll + " was installed but did not start. The Output panel says why.";
        }

        private static void Unremove(MainForm form, UserSettings settings, string[] ids)
        {
            if (settings == null) return;
            if (settings.RemovedAddIns != null) settings.RemovedAddIns.RemoveAll(id => ids.Contains(id));
            form.SaveAddInSettings();
        }

        /// <summary>The DLL and every file in its folder (subfolders included) that the IDE folder
        /// does not already have, to <paramref name="target"/>. On failure, what was copied is
        /// taken away again as far as possible.</summary>
        private static string CopyAddIn(string dll, string target)
        {
            string source = Path.GetDirectoryName(dll);
            string ide = AppDomain.CurrentDomain.BaseDirectory;
            bool existed = Directory.Exists(target);
            try
            {
                Directory.CreateDirectory(target);
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(source.Length).TrimStart('\\', '/');
                    bool isTheAddIn = string.Equals(file, dll, StringComparison.OrdinalIgnoreCase);
                    if (!isTheAddIn && File.Exists(Path.Combine(ide, relative))) continue;
                    string to = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, true);
                }
                return null;
            }
            catch (Exception ex)
            {
                if (!existed)
                {
                    try { Directory.Delete(target, true); }
                    catch (Exception) { }
                }
                return "Could not copy the add-in to " + target + ": " + ex.Message;
            }
        }

        /// <summary>A folder in <paramref name="root"/> holding an add-in with one of <paramref name="ids"/>.</summary>
        internal static string FindFolderWith(string root, string[] ids)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            foreach (string folder in Directory.GetDirectories(root))
                foreach (string dll in AddInLoader.CandidateDlls(folder))
                    if (Probe(dll).AddIns.Any(a => ids.Contains(a.Key))) return folder;
            return null;
        }

        internal static bool IsInUserRoot(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string root = Path.GetFullPath(AddInLoader.UserRoot).TrimEnd('\\') + "\\";
            return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks a DLL in two steps. First by reflection-only load (resolving from the DLL's folder
        /// and the IDE folder), so a DLL with no add-in never runs any code: it must have a public
        /// non-abstract <see cref="IHelixAddIn"/> class with a parameterless constructor. Then each
        /// such class is created from a throw-away in-memory copy of the assembly to read its Id
        /// and DisplayName; the copy locks no file and is never initialised.
        /// </summary>
        internal static AddInProbe Probe(string dll)
        {
            string key;
            try { key = Path.GetFullPath(dll) + "|" + File.GetLastWriteTimeUtc(dll).Ticks; }
            catch (Exception ex) { return new AddInProbe { Error = "Cannot read " + dll + ": " + ex.Message }; }
            lock (ProbeCache)
            {
                AddInProbe cached;
                if (ProbeCache.TryGetValue(key, out cached)) return cached;
            }
            AddInProbe probe = ProbeUncached(dll);
            lock (ProbeCache) ProbeCache[key] = probe;
            return probe;
        }

        private static AddInProbe ProbeUncached(string dll)
        {
            var probe = new AddInProbe();
            AssemblyName name;
            try { name = System.Reflection.AssemblyName.GetAssemblyName(dll); }
            catch (Exception)
            {
                probe.Error = Path.GetFileName(dll) + " is not a .NET assembly, so it cannot be an add-in.";
                return probe;
            }
            probe.AssemblyName = name.Name;
            probe.Version = name.Version;

            string folder = Path.GetDirectoryName(dll);
            if (!HasAddInType(dll, name, folder))
            {
                probe.Error = Path.GetFileName(dll) + " contains no add-in: no public, non-abstract class "
                    + "implementing IHelixAddIn with a parameterless constructor.";
                return probe;
            }

            ResolveEventHandler resolve = (s, e) => LoadBytes(Path.Combine(folder, new AssemblyName(e.Name).Name + ".dll"));
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try
            {
                Assembly copy = Assembly.Load(File.ReadAllBytes(dll));
                foreach (Type type in AddInLoader.AddInTypes(copy))
                {
                    var instance = (IHelixAddIn)Activator.CreateInstance(type);
                    if (string.IsNullOrEmpty(instance.Id))
                    {
                        probe.Error = type.FullName + " in " + Path.GetFileName(dll) + " has no Id.";
                        return probe;
                    }
                    probe.AddIns.Add(new KeyValuePair<string, string>(instance.Id, instance.DisplayName ?? instance.Id));
                }
            }
            catch (Exception ex)
            {
                probe.Error = Path.GetFileName(dll) + " could not be read: " + AddInLoader.Describe(ex);
                return probe;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolve;
            }
            if (probe.AddIns.Count == 0)
                probe.Error = Path.GetFileName(dll) + " contains no add-in that the IDE can create.";
            return probe;
        }

        private static Assembly LoadBytes(string path)
        {
            try { return File.Exists(path) ? Assembly.Load(File.ReadAllBytes(path)) : null; }
            catch (Exception) { return null; }
        }

        private static bool HasAddInType(string dll, AssemblyName name, string folder)
        {
            string ide = AppDomain.CurrentDomain.BaseDirectory;
            ResolveEventHandler resolve = (s, e) =>
            {
                Assembly known = AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies().FirstOrDefault(a => a.FullName == e.Name);
                if (known != null) return known;
                string file = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { ide, folder })
                    if (File.Exists(Path.Combine(dir, file))) return Assembly.ReflectionOnlyLoadFrom(Path.Combine(dir, file));
                try { return Assembly.ReflectionOnlyLoad(e.Name); }   // the framework, from the GAC
                catch (Exception) { return null; }
            };
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolve;
            try
            {
                // One identity per reflection-only context: a second DLL with the same name from
                // another folder would throw, and it is the same assembly anyway.
                Assembly assembly = AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies()
                    .FirstOrDefault(a => a.FullName == name.FullName) ?? Assembly.ReflectionOnlyLoadFrom(dll);
                Type[] types;
                try { types = assembly.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null && t.IsPublic).ToArray(); }
                return types.Any(t => t.IsClass && !t.IsAbstract
                    && t.GetInterfaces().Any(i => i.FullName == AddInInterface)
                    && t.GetConstructor(Type.EmptyTypes) != null);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolve;
            }
        }
    }
}
