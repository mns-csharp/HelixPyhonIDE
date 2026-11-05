using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HelixPythonNamespace.Core.Shell
{
    /// <summary>
    /// What a query or a registration of the <c>.toml</c> shell association actually found or did.
    /// Every field is filled on both paths so the caller can report the truth rather than "done":
    /// a registration that wrote every key can STILL leave double-click going elsewhere (see
    /// <see cref="BlockingUserChoiceProgId"/>), and saying otherwise would be a claim the registry
    /// does not support.
    /// </summary>
    public sealed class FileAssociationReport
    {
        /// <summary>False on any non-Windows host: there is no registry to write.</summary>
        public bool PlatformSupported { get; set; }

        /// <summary>True when every intended key was written (registration) or read (query).</summary>
        public bool Succeeded { get; set; }

        /// <summary>True when <c>.toml</c> currently resolves to THIS installation.</summary>
        public bool AssociatedWithThisExe { get; set; }

        /// <summary>ProgId currently sitting on <c>HKCU\Software\Classes\.toml</c>, or null.</summary>
        public string CurrentProgId { get; set; }

        /// <summary>
        /// A per-user "always open with" choice made through Explorer, when one exists and points
        /// somewhere other than us. Windows resolves this BEFORE the ProgId we write, and its value
        /// is paired with a machine+user+ProgId hash that Windows verifies — no application may
        /// forge it. When this is non-null the association is installed but double-click still goes
        /// to that app until the user changes the default themselves.
        /// </summary>
        public string BlockingUserChoiceProgId { get; set; }

        /// <summary>Registry paths created or updated, for the caller to show verbatim.</summary>
        public List<string> KeysWritten { get; } = new List<string>();

        /// <summary>Exception text when the operation failed; null on success.</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Registers this HelixPython build as a handler for <c>.toml</c> files in the per-user portion
    /// of the Windows registry, so double-clicking a <c>pyproject.toml</c> in Explorer starts the
    /// IDE with that path on its command line — which
    /// <c>HelixPythonNamespace.Workspace.Services.SolutionAnchorResolver</c> then widens to the whole
    /// solution.
    ///
    /// Everything is written under <c>HKEY_CURRENT_USER\Software\Classes</c>, the per-user half of
    /// <c>HKEY_CLASSES_ROOT</c>. That is deliberate: it needs no elevation, affects only the
    /// signed-in user, and is undone by deleting the keys listed in
    /// <see cref="FileAssociationReport.KeysWritten"/>.
    ///
    /// Windows associates by EXTENSION, not by file name — there is no registry shape meaning "only
    /// the file called pyproject.toml". So the extension is claimed once here, and the
    /// name-specific behaviour lives at open time, in the IDE.
    /// </summary>
    public static class TomlFileAssociation
    {
        /// <summary>The extension Windows keys the association on.</summary>
        public const string Extension = ".toml";

        /// <summary>Our file-type identifier. Versionless and product-specific by convention.</summary>
        public const string ProgId = "HelixPython.TomlSolution";

        /// <summary>Shown in Explorer's "Type" column and in the Open With list.</summary>
        public const string FriendlyTypeName = "HelixPython Solution Manifest";

        /// <summary>Application name used for the Open With registration.</summary>
        public const string ApplicationName = "HelixPython.exe";

        /// <summary>Where the per-user class registrations live. Overridden only by tests.</summary>
        public const string DefaultClassesRoot = @"Software\Classes";

        // Explorer's per-user default-app override, read-only for us (see BlockingUserChoiceProgId).
        private const string UserChoiceKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + Extension + @"\UserChoice";

        /// <summary>True when this process is running on a platform that has a Windows registry.</summary>
        public static bool IsSupportedPlatform
        {
            get
            {
                PlatformID id = Environment.OSVersion.Platform;
                return id == PlatformID.Win32NT || id == PlatformID.Win32Windows
                    || id == PlatformID.Win32S || id == PlatformID.WinCE;
            }
        }

        /// <summary>Read the current state without changing anything.</summary>
        public static FileAssociationReport Describe(string exePath)
        {
            return Describe(exePath, DefaultClassesRoot);
        }

        /// <summary>
        /// Read the current state without changing anything, under an explicit classes root. The
        /// root parameter exists so tests can point at a scratch key instead of the live
        /// <c>Software\Classes</c>; production callers use the single-argument overload.
        /// </summary>
        public static FileAssociationReport Describe(string exePath, string classesRoot)
        {
            var report = new FileAssociationReport { PlatformSupported = IsSupportedPlatform };
            if (!report.PlatformSupported)
            {
                report.ErrorMessage = "File associations are a Windows feature; this host has no registry.";
                return report;
            }

            try
            {
                using (RegistryKey ext = Registry.CurrentUser.OpenSubKey(Combine(classesRoot, Extension)))
                    report.CurrentProgId = ext == null ? null : ext.GetValue(null) as string;

                report.BlockingUserChoiceProgId = ReadBlockingUserChoice();
                report.AssociatedWithThisExe =
                    string.Equals(report.CurrentProgId, ProgId, StringComparison.OrdinalIgnoreCase)
                    && report.BlockingUserChoiceProgId == null
                    && CommandPointsAt(classesRoot, exePath);
                report.Succeeded = true;
            }
            catch (Exception ex)
            {
                report.ErrorMessage = ex.Message;
            }
            return report;
        }

        /// <summary>
        /// Claim <c>.toml</c> for <paramref name="exePath"/> in HKCU and ask Explorer to refresh.
        /// Idempotent: running it twice writes the same values. A pre-existing ProgId belonging to
        /// another application is preserved under our key (value <c>HelixPython.BackupProgId</c>)
        /// rather than discarded.
        /// </summary>
        public static FileAssociationReport Register(string exePath)
        {
            return Register(exePath, DefaultClassesRoot, notifyShell: true);
        }

        /// <summary>
        /// Registration with the classes root and the shell notification under the caller's
        /// control. Tests pass a scratch root and suppress the notification; production uses the
        /// single-argument overload.
        /// </summary>
        public static FileAssociationReport Register(string exePath, string classesRoot, bool notifyShell)
        {
            var report = new FileAssociationReport { PlatformSupported = IsSupportedPlatform };
            if (!report.PlatformSupported)
            {
                report.ErrorMessage = "File associations are a Windows feature; this host has no registry.";
                return report;
            }
            if (string.IsNullOrWhiteSpace(exePath))
            {
                report.ErrorMessage = "No executable path was supplied to associate with.";
                return report;
            }
            if (!File.Exists(exePath))
            {
                // A command line pointing at a file that is not there produces the "Windows cannot
                // find" dialog on every double-click, which reads as a broken IDE rather than a
                // broken registration. Refuse before writing anything.
                report.ErrorMessage = "The executable to associate does not exist: " + exePath;
                return report;
            }

            string fullExe = Path.GetFullPath(exePath);
            string command = "\"" + fullExe + "\" \"%1\"";
            string progIdKey = Combine(classesRoot, ProgId);
            string extKey = Combine(classesRoot, Extension);
            string appKey = Combine(classesRoot, "Applications", ApplicationName);

            try
            {
                // 1. The file type itself: display name, icon, and the command Explorer runs.
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(progIdKey))
                    k.SetValue(null, FriendlyTypeName);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(progIdKey + @"\DefaultIcon"))
                    k.SetValue(null, fullExe + ",0");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(progIdKey + @"\shell\open"))
                    k.SetValue("FriendlyAppName", "HelixPython");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(progIdKey + @"\shell\open\command"))
                    k.SetValue(null, command);
                report.KeysWritten.Add(@"HKCU\" + progIdKey);

                // 2. The extension. Preserve whoever held it, then point it at us, and also list
                //    ourselves under OpenWithProgids so we appear in Explorer's Open With menu even
                //    when a UserChoice keeps another app as the default.
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(extKey))
                {
                    string previous = k.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(previous)
                        && !string.Equals(previous, ProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        using (RegistryKey p = Registry.CurrentUser.CreateSubKey(progIdKey))
                            p.SetValue("HelixPython.BackupProgId", previous);
                    }
                    k.SetValue(null, ProgId);
                    report.CurrentProgId = ProgId;
                }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(extKey + @"\OpenWithProgids"))
                    k.SetValue(ProgId, string.Empty, RegistryValueKind.String);
                report.KeysWritten.Add(@"HKCU\" + extKey);

                // 3. The application registration, which is what puts "HelixPython" into the
                //    Open With list and into Settings > Apps > Default apps.
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey))
                    k.SetValue("FriendlyAppName", "HelixPython");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey + @"\shell\open\command"))
                    k.SetValue(null, command);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(appKey + @"\SupportedTypes"))
                    k.SetValue(Extension, string.Empty, RegistryValueKind.String);
                report.KeysWritten.Add(@"HKCU\" + appKey);

                report.BlockingUserChoiceProgId = ReadBlockingUserChoice();
                report.AssociatedWithThisExe = report.BlockingUserChoiceProgId == null;
                report.Succeeded = true;

                if (notifyShell) NotifyShellAssociationsChanged();
            }
            catch (Exception ex)
            {
                // UnauthorizedAccess (locked-down HKCU), SecurityException, IOException — all mean
                // the same thing to the caller: nothing usable was installed.
                report.Succeeded = false;
                report.ErrorMessage = ex.Message;
            }
            return report;
        }

        /// <summary>
        /// Remove the registration this class installs, restoring any ProgId it displaced. Present
        /// so the registry edit is reversible from code as well as by hand.
        /// </summary>
        public static FileAssociationReport Unregister()
        {
            return Unregister(DefaultClassesRoot, notifyShell: true);
        }

        /// <summary>Unregistration under an explicit classes root (see <see cref="Register(string,string,bool)"/>).</summary>
        public static FileAssociationReport Unregister(string classesRoot, bool notifyShell)
        {
            var report = new FileAssociationReport { PlatformSupported = IsSupportedPlatform };
            if (!report.PlatformSupported)
            {
                report.ErrorMessage = "File associations are a Windows feature; this host has no registry.";
                return report;
            }

            string progIdKey = Combine(classesRoot, ProgId);
            string extKey = Combine(classesRoot, Extension);
            string appKey = Combine(classesRoot, "Applications", ApplicationName);
            try
            {
                string backup = null;
                using (RegistryKey p = Registry.CurrentUser.OpenSubKey(progIdKey))
                    if (p != null) backup = p.GetValue("HelixPython.BackupProgId") as string;

                using (RegistryKey ext = Registry.CurrentUser.OpenSubKey(extKey, writable: true))
                {
                    if (ext != null)
                    {
                        string current = ext.GetValue(null) as string;
                        if (string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(backup)) ext.DeleteValue(string.Empty, throwOnMissingValue: false);
                            else ext.SetValue(null, backup);
                        }
                        using (RegistryKey owp = ext.OpenSubKey("OpenWithProgids", writable: true))
                            if (owp != null) owp.DeleteValue(ProgId, throwOnMissingValue: false);
                    }
                }

                Registry.CurrentUser.DeleteSubKeyTree(progIdKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(appKey, throwOnMissingSubKey: false);
                report.CurrentProgId = backup;
                report.Succeeded = true;
                if (notifyShell) NotifyShellAssociationsChanged();
            }
            catch (Exception ex)
            {
                report.Succeeded = false;
                report.ErrorMessage = ex.Message;
            }
            return report;
        }

        /// <summary>
        /// A user-readable account of <paramref name="report"/> — the same text whether it came
        /// from <see cref="Describe(string)"/> or <see cref="Register(string)"/>.
        /// </summary>
        public static string Explain(FileAssociationReport report)
        {
            if (report == null) return "No result.";
            if (!report.PlatformSupported) return report.ErrorMessage ?? "Not supported on this platform.";
            if (!report.Succeeded)
                return "Could not register " + Extension + " files: " + (report.ErrorMessage ?? "unknown error") + ".";

            string text;
            if (report.KeysWritten.Count == 0)
            {
                text = Extension + " is currently handled by: " + (report.CurrentProgId ?? "(nothing)") + ".";
            }
            else
            {
                text = Extension + " files are now registered to HelixPython."
                     + Environment.NewLine + Environment.NewLine
                     + "Keys written (per-user, no elevation needed):" + Environment.NewLine
                     + "    " + string.Join(Environment.NewLine + "    ", report.KeysWritten.ToArray());
            }

            if (report.BlockingUserChoiceProgId != null)
            {
                text += Environment.NewLine + Environment.NewLine
                     + "Windows still opens " + Extension + " files with \""
                     + report.BlockingUserChoiceProgId + "\", because a default app was chosen for this"
                     + " extension earlier. That choice is hash-protected and no program can overwrite"
                     + " it. Change it in Settings > Apps > Default apps, or right-click a .toml file >"
                     + " Open with > Choose another app > HelixPython > Always use this app.";
            }
            return text;
        }

        // ---- internals -------------------------------------------------------------------

        private static string Combine(params string[] parts)
        {
            return string.Join(@"\", parts);
        }

        private static string ReadBlockingUserChoice()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UserChoiceKey))
                {
                    string chosen = k == null ? null : k.GetValue("ProgId") as string;
                    if (string.IsNullOrEmpty(chosen)) return null;
                    return string.Equals(chosen, ProgId, StringComparison.OrdinalIgnoreCase) ? null : chosen;
                }
            }
            catch { return null; }   // an unreadable UserChoice is not a registration failure
        }

        private static bool CommandPointsAt(string classesRoot, string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                           Combine(classesRoot, ProgId) + @"\shell\open\command"))
                {
                    string cmd = k == null ? null : k.GetValue(null) as string;
                    if (string.IsNullOrEmpty(cmd)) return false;
                    return cmd.IndexOf(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll", SetLastError = false)]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>
        /// Tell Explorer the association table changed. Without this the new icon and the new
        /// double-click target only appear after the next sign-in, which reads as "it did not work".
        /// </summary>
        private static void NotifyShellAssociationsChanged()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
            catch { /* the registration itself already succeeded; a stale icon is not a failure */ }
        }
    }
}
