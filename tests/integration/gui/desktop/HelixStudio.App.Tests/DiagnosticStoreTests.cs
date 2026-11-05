namespace HelixPythonNamespace.App.Tests
{
    using System.Collections.Generic;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.App.Services;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public sealed class DiagnosticStoreTests
    {
        [TestMethod]
        public void Replace_PreservesOtherSources_AndReplacesNamedBucket()
        {
            var store = new DiagnosticStore();
            store.Replace("Build", Rows("error", "CS1"));
            store.Replace("Analyzer", Rows("warning", "A1"));
            store.Replace("Build", Rows("warning", "CS2"));

            Assert.AreEqual(2, store.Snapshot().Count);
            Assert.AreEqual("CS2", store.Snapshot("Build")[0].Code);
            Assert.AreEqual("Analyzer", store.Snapshot("Analyzer")[0].Source);
        }

        [TestMethod]
        public void Snapshot_FiltersSeverityAndTextCaseInsensitively()
        {
            var store = new DiagnosticStore();
            store.Replace("Build", new[]
            {
                new BuildResultItem { Severity = "error", Code = "CS1000", Message = "Missing brace", File = "Program.cs" },
                new BuildResultItem { Severity = "warning", Code = "CS2000", Message = "Unused", File = "Other.cs" }
            });

            Assert.AreEqual(1, store.Snapshot(null, "ERROR", "program").Count);
            Assert.AreEqual(1, store.Snapshot(null, "warning", "unused").Count);
        }

        [TestMethod]
        public void ErrorList_RendersMultipleSourcesAndAppliesContextFilterState()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form._diagnosticStore.Replace("Build", Rows("error", "B1"));
                form._diagnosticStore.Replace("Compiler", Rows("warning", "C1"));
                form.RenderDiagnosticStore();
                Assert.AreEqual(2, form._buildErrorsListView.Items.Count);
                CollectionAssert.AreEquivalent(new[] { "Build", "Compiler" },
                    new[] { form._buildErrorsListView.Items[0].SubItems[1].Text, form._buildErrorsListView.Items[1].SubItems[1].Text });
                form._diagnosticSourceFilter = "Compiler";
                form.RenderDiagnosticStore();
                Assert.AreEqual(1, form._buildErrorsListView.Items.Count);
                Assert.AreEqual("Compiler", form._buildErrorsListView.Items[0].SubItems[1].Text);
            }
        }

        private static IEnumerable<BuildResultItem> Rows(string severity, string code)
        {
            yield return new BuildResultItem { Severity = severity, Code = code };
        }
    }
}
