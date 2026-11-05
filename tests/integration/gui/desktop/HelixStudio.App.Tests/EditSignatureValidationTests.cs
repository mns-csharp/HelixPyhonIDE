using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 308: the Change Signature dialog applied parameter renames without checking they were
    /// valid identifiers, so "123" / "my param" would land in the emitted signature. Apply now rejects
    /// the first bad parameter name. Guards that detection.
    /// </summary>
    [TestClass]
    public class EditSignatureValidationTests
    {
        private static string FirstInvalid(IEnumerable<ParameterEdit> rows)
        {
            MethodInfo m = typeof(EditSignatureDialog).GetMethod(
                "FirstInvalidParamName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "FirstInvalidParamName expected");
            return (string)m.Invoke(null, new object[] { rows });
        }

        [TestMethod]
        public void AllValidNames_ReturnNull()
        {
            var rows = new List<ParameterEdit>
            {
                new ParameterEdit { NewName = "self" },
                new ParameterEdit { NewName = "count" },
                new ParameterEdit { NewName = "_hidden" },
            };
            Assert.IsNull(FirstInvalid(rows));
        }

        [TestMethod]
        public void FirstBadName_IsReported()
        {
            var rows = new List<ParameterEdit>
            {
                new ParameterEdit { NewName = "ok" },
                new ParameterEdit { NewName = "123" },
                new ParameterEdit { NewName = "also_bad name" },
            };
            Assert.AreEqual("123", FirstInvalid(rows), "the first invalid name should be reported");
        }

        [TestMethod]
        public void EmptyOrNull_ReturnNull()
        {
            Assert.IsNull(FirstInvalid(new List<ParameterEdit>()));
            Assert.IsNull(FirstInvalid(null));
        }
    }
}
