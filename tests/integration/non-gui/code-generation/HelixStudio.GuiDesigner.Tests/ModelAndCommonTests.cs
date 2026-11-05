namespace HelixPythonNamespace.GuiDesigner.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.GuiDesigner.Emit;
    using HelixPythonNamespace.GuiDesigner.Model;

    [TestClass]
    public class ModelTests
    {
        [TestMethod]
        public void KindTokenRoundTrips()
        {
            foreach (GuiControlKind k in System.Enum.GetValues(typeof(GuiControlKind)))
                Assert.AreEqual(k, GuiControlKinds.FromToken(GuiControlKinds.ToToken(k)));
        }

        [TestMethod]
        public void LegacyTkinterAliasesAccepted()
        {
            Assert.AreEqual(GuiControlKind.Panel, GuiControlKinds.FromToken("Frame"));
            Assert.AreEqual(GuiControlKind.GroupBox, GuiControlKinds.FromToken("LabelFrame"));
            Assert.AreEqual(GuiControlKind.TextBox, GuiControlKinds.FromToken("Entry"));
            Assert.AreEqual(GuiControlKind.RichTextBox, GuiControlKinds.FromToken("Text"));
            Assert.AreEqual(GuiControlKind.ComboBox, GuiControlKinds.FromToken("Combobox"));
            Assert.AreEqual(GuiControlKind.TrackBar, GuiControlKinds.FromToken("Scale"));
        }

        [TestMethod]
        public void UnknownTokenFallsBackToButton()
        {
            Assert.AreEqual(GuiControlKind.Button, GuiControlKinds.FromToken("Nonexistent"));
        }

        [TestMethod]
        public void FrameworkTokens()
        {
            Assert.AreEqual("tkinter", GuiFrameworks.ToToken(GuiFramework.Tkinter));
            Assert.AreEqual("pyqt6", GuiFrameworks.ToToken(GuiFramework.PyQt6));
            Assert.AreEqual("pyside6", GuiFrameworks.ToToken(GuiFramework.PySide6));
            Assert.AreEqual("wxpython", GuiFrameworks.ToToken(GuiFramework.WxPython));
            Assert.AreEqual(GuiFramework.WxPython, GuiFrameworks.FromToken("wxpython"));
            Assert.AreEqual(GuiFramework.Tkinter, GuiFrameworks.FromToken("unknown"));
        }

        [TestMethod]
        public void NonVisualClassification()
        {
            Assert.IsTrue(GuiControlKinds.IsNonVisualComponent(GuiControlKind.Timer));
            Assert.IsTrue(GuiControlKinds.IsNonVisualComponent(GuiControlKind.OpenFileDialog));
            Assert.IsFalse(GuiControlKinds.IsNonVisualComponent(GuiControlKind.Button));
        }
    }

    [TestClass]
    public class EmitterCommonTests
    {
        [TestMethod]
        public void PyEscapeHandlesQuotesAndControls()
        {
            Assert.AreEqual("a\\'b", EmitterCommon.PyEscape("a'b"));
            Assert.AreEqual("a\\\\b", EmitterCommon.PyEscape("a\\b"));
            Assert.AreEqual("a\\nb", EmitterCommon.PyEscape("a\nb"));
        }

        [TestMethod]
        public void SafeMemberSanitizes()
        {
            Assert.AreEqual("widget", EmitterCommon.SafeMember(""));
            Assert.AreEqual("c_1btn", EmitterCommon.SafeMember("1btn"));
            Assert.AreEqual("my_btn", EmitterCommon.SafeMember("my btn"));
            Assert.AreEqual("a_b", EmitterCommon.SafeMember("a.b"));
        }

        [TestMethod]
        public void IntPropOrParsesLeadingInteger()
        {
            GuiControl c = new GuiControl();
            c.Props["a"] = "42";
            c.Props["b"] = "7px";
            c.Props["c"] = "notnum";
            Assert.AreEqual(42, EmitterCommon.IntPropOr(c, "a", -1));
            Assert.AreEqual(7, EmitterCommon.IntPropOr(c, "b", -1));
            Assert.AreEqual(-1, EmitterCommon.IntPropOr(c, "c", -1));
            Assert.AreEqual(99, EmitterCommon.IntPropOr(c, "missing", 99));
        }

        [TestMethod]
        public void SplitCsvTrims()
        {
            System.Collections.Generic.List<string> r = EmitterCommon.SplitCsv("a, b ,c,,  ");
            CollectionAssert.AreEqual(new System.Collections.Generic.List<string> { "a", "b", "c" }, r);
        }
    }

    [TestClass]
    public class GuiFrameworkCapabilityTests
    {
        [TestMethod]
        public void ToolboxOnlyOffersControlsImplementedByTheBinding()
        {
            Assert.IsTrue(GuiFrameworkCapabilities.Supports(GuiFramework.CppWin32, GuiControlKind.Button));
            Assert.IsFalse(GuiFrameworkCapabilities.Supports(GuiFramework.CppWin32, GuiControlKind.DataGridView));
        }
    }

    [TestClass]
    public class GuiFormHistoryTests
    {
        [TestMethod]
        public void UndoAndRedoRestoreDeepControlState()
        {
            GuiForm form = new GuiForm();
            GuiControl control = new GuiControl { Name = "button1", X = 10 };
            control.Props["Text"] = "Before";
            form.Controls.Add(control);
            GuiFormHistory history = new GuiFormHistory();
            history.Record(form);
            control.X = 90; control.Props["Text"] = "After";

            Assert.IsTrue(history.Undo(form));
            Assert.AreEqual(10, form.Controls[0].X);
            Assert.AreEqual("Before", form.Controls[0].Props["Text"]);
            Assert.IsTrue(history.Redo(form));
            Assert.AreEqual(90, form.Controls[0].X);
            Assert.AreEqual("After", form.Controls[0].Props["Text"]);
        }
    }

    [TestClass]
    public class CodeRegionPreserverTests
    {
        [TestMethod]
        public void ParsesRegionBody()
        {
            string src = "x = 1\n# region UserCode-foo\n    def foo(self):\n        return 7\n# endregion\ny = 2\n";
            System.Collections.Generic.Dictionary<string, string> regions = CodeRegionPreserver.Parse(src);
            Assert.IsTrue(regions.ContainsKey("UserCode-foo"));
            Assert.IsTrue(regions["UserCode-foo"].Contains("return 7"));
        }

        [TestMethod]
        public void GetOrDefaultFallsBack()
        {
            System.Collections.Generic.Dictionary<string, string> empty = new System.Collections.Generic.Dictionary<string, string>();
            Assert.AreEqual("DEFAULT", CodeRegionPreserver.GetOrDefault(empty, "x", "DEFAULT"));
        }

        [TestMethod]
        public void EmptySourceYieldsNoRegions()
        {
            Assert.AreEqual(0, CodeRegionPreserver.Parse("").Count);
            Assert.AreEqual(0, CodeRegionPreserver.Parse(null).Count);
        }
    }
}
