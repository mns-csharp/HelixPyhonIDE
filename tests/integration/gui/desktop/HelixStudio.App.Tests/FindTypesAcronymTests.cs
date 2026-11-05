using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 294: the Find Type quick-open matched by exact/prefix/substring/FQN only — typing an
    /// acronym like "SPB" didn't find "SolutionProjectionBuilder". It now also matches word-initial
    /// acronyms (VS/VS Code parity). Guards the acronym extraction across the common casing styles.
    /// </summary>
    [TestClass]
    public class FindTypesAcronymTests
    {
        private static string Acronym(string name)
        {
            MethodInfo m = typeof(FindTypesDialog).GetMethod(
                "ComputeAcronym", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "FindTypesDialog.ComputeAcronym(string) expected");
            return (string)m.Invoke(null, new object[] { name });
        }

        [TestMethod]
        public void PascalCase_TakesEachHump()
        {
            Assert.AreEqual("spb", Acronym("SolutionProjectionBuilder"));
            Assert.AreEqual("mc", Acronym("MyClass"));
        }

        [TestMethod]
        public void SnakeCase_TakesEachWord()
        {
            Assert.AreEqual("mc", Acronym("my_class"));
            Assert.AreEqual("hrp", Acronym("http_request_parser"));
        }

        [TestMethod]
        public void SingleWord_AndEmpty()
        {
            Assert.AreEqual("f", Acronym("Foo"));
            Assert.AreEqual("", Acronym(""));
            Assert.AreEqual("", Acronym(null));
        }

        [TestMethod]
        public void AcronymPrefix_IsWhatTheUserTypes()
        {
            // The user types a prefix of the acronym; "sp" should still lead to SolutionProjectionBuilder.
            string acr = Acronym("SolutionProjectionBuilder");
            Assert.IsTrue(acr.StartsWith("sp"), "acronym should start with the typed prefix");
        }
    }
}
