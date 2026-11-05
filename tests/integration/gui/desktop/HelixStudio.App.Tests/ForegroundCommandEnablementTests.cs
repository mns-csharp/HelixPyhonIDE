using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class ForegroundCommandEnablementTests
    {
        [TestMethod]
        public void JobLifecycle_DisablesConflictingCommands_ButKeepsStopReachable()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form);
                JobRunner runner = Field<JobRunner>(form, "_jobRunner");
                var buildTop = Field<ToolStripMenuItem>(form, "buildTopMenuItem");
                var stop = Field<ToolStripMenuItem>(form, "_stopExecutionMenuItem");
                var run = Field<ToolStripButton>(form, "_runButton");
                var build = Field<ToolStripButton>(form, "_buildButton");
                var analysis = Field<ToolStripMenuItem>(form, "analysisMenuItem");

                Assert.IsTrue(runner.BeginExternal("Building workspace", delegate { }));

                Assert.IsFalse(run.Enabled);
                Assert.IsFalse(build.Enabled);
                Assert.IsFalse(analysis.Enabled);
                Assert.IsTrue(buildTop.Enabled, "Build menu must remain reachable for Stop Execution");
                Assert.IsTrue(stop.Enabled, "the lifecycle owns and enables Stop Execution");
                foreach (ToolStripItem child in buildTop.DropDownItems)
                    if (!ReferenceEquals(child, stop))
                        Assert.IsFalse(child.Enabled, "conflicting Build command remained enabled: " + child.Text);

                runner.Finish("Done");

                Assert.IsTrue(run.Enabled);
                Assert.IsTrue(build.Enabled);
                Assert.IsTrue(analysis.Enabled);
                Assert.IsFalse(stop.Enabled, "Stop must return to its idle state");
            }
        }

        private static void SetSolution(MainForm form)
        {
            FieldInfo field = typeof(MainForm).GetField("_currentSolution",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field);
            field.SetValue(form, new HelixPythonNamespace.Core.Model.SolutionModel("Commands", @"C:\work\commands.sln"));
        }

        private static T Field<T>(MainForm form, string name) where T : class
        {
            FieldInfo field = typeof(MainForm).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(field, name);
            return field.GetValue(form) as T;
        }
    }
}
