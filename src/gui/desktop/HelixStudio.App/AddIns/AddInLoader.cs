namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using HelixPythonNamespace.AddIns;

    /// <summary>Where an add-in came from (spec 001, §4.4.1).</summary>
    internal enum AddInKind
    {
        /// <summary><c>&lt;exe dir&gt;\addins\&lt;Name&gt;\</c>: put there by the build or the installer.</summary>
        Bundled,

        /// <summary><c>%LOCALAPPDATA%\HelixPython\addins\&lt;Name&gt;\</c>: put there by Install.</summary>
        User,
    }

    /// <summary>One add-in the shell loaded. Read-only; the add-in manager lists these.</summary>
    internal sealed class LoadedAddIn
    {
        internal LoadedAddIn(IHelixAddIn instance, AddInKind kind, string folder, MainForm.AddInHost host)
        {
            Instance = instance;
            Kind = kind;
            Folder = folder;
            Host = host;
            Id = instance.Id;
            DisplayName = instance.DisplayName;
            Version = instance.GetType().Assembly.GetName().Version;
        }

        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public Version Version { get; private set; }
        public AddInKind Kind { get; private set; }

        /// <summary>The add-in's own folder; null for an instance handed to <c>LoadAddIn</c> directly.</summary>
        public string Folder { get; private set; }

        internal IHelixAddIn Instance { get; private set; }
        internal MainForm.AddInHost Host { get; private set; }
    }

    /// <summary>
    /// Finds add-ins at startup and hands each one to <see cref="MainForm.LoadAddIn"/> (spec 001,
    /// §4.4). A root holds one folder per add-in; every DLL directly in that folder is scanned.
    ///
    /// <para><b>One identity per shared type.</b> <c>Assembly.LoadFrom</c> binds an add-in's
    /// references through the Load context first, so the SDK and HelixStudio.UI come from the IDE
    /// folder even if an add-in folder carries its own copy. Only what the IDE folder does not have
    /// falls through to <see cref="OnAssemblyResolve"/>, which looks in the add-in folders.</para>
    /// </summary>
    internal static class AddInLoader
    {
        internal const string FolderName = "addins";

        private static readonly List<string> ResolveFolders = new List<string>();
        private static bool _resolveHooked;

        /// <summary><c>&lt;exe dir&gt;\addins</c>.</summary>
        internal static string BundledRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FolderName); }
        }

        /// <summary>
        /// <c>%LOCALAPPDATA%\HelixPython\addins</c>, or <c>&lt;HELIXSTUDIO_STATE_DIR&gt;\addins</c> when
        /// the state directory is redirected (tests), the same rule as the settings file.
        /// </summary>
        internal static string UserRoot
        {
            get { return Path.Combine(PerUserRoot, FolderName); }
        }

        /// <summary>
        /// <c>%LOCALAPPDATA%\HelixPython\addin-data</c> (same redirect): the parent of each add-in's
        /// <c>IAddInHost.DataDirectory</c>. A sibling of <see cref="UserRoot"/>, not inside it,
        /// because the loader scans every folder under the user root for add-ins.
        /// </summary>
        internal static string DataRoot
        {
            get { return Path.Combine(PerUserRoot, "addin-data"); }
        }

        private static string PerUserRoot
        {
            get
            {
                string isolatedState = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
                if (!string.IsNullOrWhiteSpace(isolatedState))
                    return Environment.ExpandEnvironmentVariables(isolatedState);
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelixPython");
            }
        }

        /// <summary>Both roots, bundled first.</summary>
        internal static KeyValuePair<string, AddInKind>[] Roots
        {
            get
            {
                return new[]
                {
                    new KeyValuePair<string, AddInKind>(BundledRoot, AddInKind.Bundled),
                    new KeyValuePair<string, AddInKind>(UserRoot, AddInKind.User),
                };
            }
        }

        /// <summary>Startup: both roots, bundled first.</summary>
        internal static void LoadAll(MainForm form)
        {
            LoadFrom(form, Roots);
        }

        /// <summary>The DLLs in an add-in folder that can hold an add-in: every DLL directly in it
        /// except those the IDE itself ships (the SDK, HelixStudio.UI, ...), which the folder
        /// merely carries; the IDE's own copy is the one in use. Empty when unreadable.</summary>
        internal static string[] CandidateDlls(string folder)
        {
            string[] dlls;
            try { dlls = Directory.GetFiles(folder, "*.dll"); }
            catch (Exception) { return new string[0]; }
            Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);
            return dlls.Where(d => !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.GetFileName(d)))).ToArray();
        }

        /// <summary>
        /// Deletes the folders queued by Remove, then loads every add-in under
        /// <paramref name="roots"/>. Nothing here throws: a bad DLL or add-in is one Output line.
        /// </summary>
        internal static void LoadFrom(MainForm form, IEnumerable<KeyValuePair<string, AddInKind>> roots)
        {
            UserSettings settings = form.AddInUserSettings;
            DeletePendingFolders(form, settings);

            foreach (KeyValuePair<string, AddInKind> root in roots)
            {
                if (string.IsNullOrEmpty(root.Key) || !Directory.Exists(root.Key)) continue;
                string[] folders;
                try { folders = Directory.GetDirectories(root.Key); }
                catch (Exception ex)
                {
                    form.AppendBuildOutputLine("Add-ins: cannot read " + root.Key + ": " + ex.Message);
                    continue;
                }
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                foreach (string folder in folders)
                    LoadFolder(form, folder, root.Value);
            }
        }

        internal static void LoadFolder(MainForm form, string folder, AddInKind kind)
        {
            try { Directory.GetFiles(folder, "*.dll"); }
            catch (Exception ex)
            {
                form.AppendBuildOutputLine("Add-ins: cannot read " + folder + ": " + ex.Message);
                return;
            }
            foreach (string dll in CandidateDlls(folder))
                LoadDll(form, dll, folder, kind);
        }

        /// <summary>Every add-in in one DLL, handed to <see cref="MainForm.LoadAddIn"/> (which skips
        /// a removed one). Install uses it directly on the DLL it copied.</summary>
        internal static void LoadDll(MainForm form, string dll, string folder, AddInKind kind)
        {
            HookAssemblyResolve(folder);
            Type[] types;
            try
            {
                types = AddInTypes(Assembly.LoadFrom(dll));
            }
            catch (Exception ex)
            {
                form.AppendBuildOutputLine("Add-in " + dll + " could not be loaded: " + Describe(ex));
                return;
            }

            foreach (Type type in types)
            {
                IHelixAddIn instance;
                try
                {
                    instance = (IHelixAddIn)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    form.AppendBuildOutputLine("Add-in " + dll + " (" + type.FullName + ") could not be created: " + Describe(ex));
                    continue;
                }
                form.LoadAddIn(instance, kind, folder, dll);
            }
        }

        /// <summary>Public, non-abstract <see cref="IHelixAddIn"/> classes with a parameterless constructor.</summary>
        internal static Type[] AddInTypes(Assembly assembly)
        {
            Type[] exported;
            try { exported = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t != null && t.IsPublic).ToArray(); }
            return exported
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IHelixAddIn).IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) != null)
                .ToArray();
        }

        internal static bool IsRemoved(UserSettings settings, string id)
        {
            return settings != null && settings.RemovedAddIns != null && id != null
                && settings.RemovedAddIns.Contains(id, StringComparer.Ordinal);
        }

        private static void DeletePendingFolders(MainForm form, UserSettings settings)
        {
            if (settings == null || settings.PendingAddInDeletes == null || settings.PendingAddInDeletes.Count == 0) return;
            foreach (string folder in settings.PendingAddInDeletes.ToArray())
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    settings.PendingAddInDeletes.Remove(folder);
                }
                catch (Exception ex)
                {
                    // Kept in the list: the next start tries again.
                    form.AppendBuildOutputLine("Add-ins: could not delete the removed add-in folder " + folder + ": " + ex.Message);
                }
            }
            form.SaveAddInSettings();
        }

        private static void HookAssemblyResolve(string folder)
        {
            lock (ResolveFolders)
            {
                if (!ResolveFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) ResolveFolders.Add(folder);
                if (_resolveHooked) return;
                _resolveHooked = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        /// <summary>The fallback after the IDE folder: an add-in's own dependencies, from its own folder.</summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string file = new AssemblyName(args.Name).Name + ".dll";
            string[] folders;
            lock (ResolveFolders) folders = ResolveFolders.ToArray();
            foreach (string folder in folders)
            {
                string candidate = Path.Combine(folder, file);
                if (!File.Exists(candidate)) continue;
                try { return Assembly.LoadFrom(candidate); }
                catch (Exception) { }
            }
            return null;
        }

        internal static string Describe(Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
