using System;
using System.IO;
using System.Reflection;
using HelixPythonNamespace.Core.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace HelixPythonNamespace.Core.Tests
{
    /// <summary>
    /// <see cref="TomlFileAssociation"/> — what Tools ▸ Register TOML Files actually writes.
    ///
    /// Every test registers into a SCRATCH classes root under
    /// <c>HKCU\Software\HelixPython\Tests\...</c> and deletes it afterwards: the live
    /// <c>Software\Classes</c> belongs to the developer running the suite, and a test that
    /// repoints their .toml files would be a defect, not coverage. The shapes asserted here are
    /// the same ones written under the real root — only the parent key differs.
    ///
    /// The claim these pin down is narrow on purpose: "the right keys and values exist". Whether
    /// Explorer then HONOURS them is a separate question the engine answers through
    /// <see cref="FileAssociationReport.BlockingUserChoiceProgId"/>, because a per-user default-app
    /// choice outranks anything written here and is hash-protected against forgery.
    /// </summary>
    [TestClass]
    public class TomlFileAssociationTests
    {
        private string _scratchRoot;

        [TestInitialize]
        public void Setup()
        {
            _scratchRoot = @"Software\HelixPython\Tests\assoc_" + Guid.NewGuid().ToString("N");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_scratchRoot, throwOnMissingSubKey: false); }
            catch { /* the test already reported what matters */ }
        }

        // A real, existing file to point the command at. Register refuses a path that is not on
        // disk, and it is right to: the alternative is a "Windows cannot find" box on every
        // double-click.
        private static string ThisAssemblyPath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        private string Value(string subKey, string valueName)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(_scratchRoot + @"\" + subKey))
                return k == null ? null : k.GetValue(valueName) as string;
        }

        private static void RequireWindows()
        {
            if (!TomlFileAssociation.IsSupportedPlatform)
                Assert.Inconclusive("No Windows registry on this host.");
        }

        [TestMethod]
        public void Register_WritesTheOpenCommandExplorerWillRun()
        {
            RequireWindows();
            string exe = ThisAssemblyPath();

            FileAssociationReport report = TomlFileAssociation.Register(exe, _scratchRoot, notifyShell: false);

            Assert.IsTrue(report.Succeeded, report.ErrorMessage);
            string command = Value(TomlFileAssociation.ProgId + @"\shell\open\command", null);
            Assert.IsNotNull(command, "no open command was written");
            // The %1 is the whole feature: without it Explorer starts the IDE with no argument and
            // the double-clicked file is silently dropped.
            StringAssert.Contains(command, "\"%1\"", "the open command must forward the clicked path");
            StringAssert.Contains(command, Path.GetFullPath(exe));
        }

        [TestMethod]
        public void Register_PointsTheExtensionAtOurProgId_AndListsUsUnderOpenWith()
        {
            RequireWindows();

            TomlFileAssociation.Register(ThisAssemblyPath(), _scratchRoot, notifyShell: false);

            Assert.AreEqual(TomlFileAssociation.ProgId, Value(TomlFileAssociation.Extension, null),
                            "the extension's default value is what Explorer resolves");
            Assert.IsNotNull(Value(TomlFileAssociation.Extension + @"\OpenWithProgids", TomlFileAssociation.ProgId),
                             "without OpenWithProgids we never appear in the Open With menu");
            Assert.IsNotNull(Value(@"Applications\" + TomlFileAssociation.ApplicationName + @"\SupportedTypes",
                                   TomlFileAssociation.Extension),
                             "SupportedTypes is what lists HelixPython under Default apps");
        }

        [TestMethod]
        public void Register_IsIdempotent_AndDoesNotBackUpItself()
        {
            RequireWindows();
            string exe = ThisAssemblyPath();

            TomlFileAssociation.Register(exe, _scratchRoot, notifyShell: false);
            FileAssociationReport second = TomlFileAssociation.Register(exe, _scratchRoot, notifyShell: false);

            Assert.IsTrue(second.Succeeded, second.ErrorMessage);
            Assert.AreEqual(TomlFileAssociation.ProgId, Value(TomlFileAssociation.Extension, null));
            // Backing up our own ProgId would make a second click "restore" HelixPython to
            // HelixPython on unregister, permanently stranding whatever was there first.
            Assert.IsNull(Value(TomlFileAssociation.ProgId, "HelixPython.BackupProgId"),
                          "re-registering must not record us as the displaced handler");
        }

        [TestMethod]
        public void Register_PreservesTheHandlerItDisplaces_AndUnregisterPutsItBack()
        {
            RequireWindows();
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(_scratchRoot + @"\" + TomlFileAssociation.Extension))
                k.SetValue(null, "SomeOtherEditor.toml");

            TomlFileAssociation.Register(ThisAssemblyPath(), _scratchRoot, notifyShell: false);
            Assert.AreEqual("SomeOtherEditor.toml", Value(TomlFileAssociation.ProgId, "HelixPython.BackupProgId"),
                            "the displaced handler must be recorded before it is overwritten");

            TomlFileAssociation.Unregister(_scratchRoot, notifyShell: false);

            Assert.AreEqual("SomeOtherEditor.toml", Value(TomlFileAssociation.Extension, null),
                            "unregistering must hand the extension back to its previous owner");
        }

        [TestMethod]
        public void Register_RefusesAnExecutableThatIsNotOnDisk_WritingNothing()
        {
            RequireWindows();
            string ghost = Path.Combine(Path.GetTempPath(), "helix_no_such_" + Guid.NewGuid().ToString("N") + ".exe");

            FileAssociationReport report = TomlFileAssociation.Register(ghost, _scratchRoot, notifyShell: false);

            Assert.IsFalse(report.Succeeded, "a command pointing at nothing is worse than no association");
            Assert.AreEqual(0, report.KeysWritten.Count);
            Assert.IsNull(Value(TomlFileAssociation.Extension, null), "the extension must be untouched");
        }

        [TestMethod]
        public void Describe_ReportsTheAssociationOnlyWhenNoUserChoiceOverridesIt()
        {
            RequireWindows();
            string exe = ThisAssemblyPath();
            TomlFileAssociation.Register(exe, _scratchRoot, notifyShell: false);

            FileAssociationReport report = TomlFileAssociation.Describe(exe, _scratchRoot);

            Assert.IsTrue(report.Succeeded, report.ErrorMessage);
            Assert.AreEqual(TomlFileAssociation.ProgId, report.CurrentProgId);
            // Written against the CONTRACT rather than this machine's state: the ProgId and the
            // command both match, so the only thing that can still deny the association is an
            // Explorer default-app choice — which the report must then name.
            Assert.AreEqual(report.BlockingUserChoiceProgId == null, report.AssociatedWithThisExe,
                            "a UserChoice is the only permitted reason to report 'not associated' here");
        }

        [TestMethod]
        public void Explain_NamesTheManualStepWhenWindowsWillIgnoreTheRegistration()
        {
            var blocked = new FileAssociationReport
            {
                PlatformSupported = true,
                Succeeded = true,
                CurrentProgId = TomlFileAssociation.ProgId,
                BlockingUserChoiceProgId = "Notepad.toml",
            };
            blocked.KeysWritten.Add(@"HKCU\Software\Classes\.toml");

            string text = TomlFileAssociation.Explain(blocked);

            // "Registered." on its own would be true and useless: double-click still goes to
            // Notepad, and the user has no way to know why.
            StringAssert.Contains(text, "Notepad.toml");
            StringAssert.Contains(text, "Default apps");
        }
    }
}
