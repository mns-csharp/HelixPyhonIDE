using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CallHierarchyServiceTests
    {
        private const string Src =
            "def helper():\n" +          // line 1
            "    return 1\n" +           // 2
            "\n" +                       // 3
            "def caller():\n" +          // 4
            "    x = helper()\n" +       // 5  <- enclosed by caller
            "    return x\n" +           // 6
            "\n" +                       // 7
            "class Service:\n" +         // 8
            "    def run(self):\n" +     // 9
            "        return helper()\n" +// 10 <- enclosed by Service.run
            "\n" +                       // 11
            "helper()\n";                // 12 <- module scope

        [TestMethod]
        public void EnclosingFunction_TopLevelFunction()
        {
            Assert.AreEqual("caller", CallHierarchyService.FindEnclosingFunction(Src, 5));
        }

        [TestMethod]
        public void EnclosingFunction_Method_IsClassQualified()
        {
            Assert.AreEqual("Service.run", CallHierarchyService.FindEnclosingFunction(Src, 10));
        }

        [TestMethod]
        public void EnclosingFunction_ModuleScope_IsNull()
        {
            Assert.IsNull(CallHierarchyService.FindEnclosingFunction(Src, 12));
        }

        [TestMethod]
        public void EnclosingFunction_NestedDef_ReturnsInnermost()
        {
            string src =
                "def outer():\n" +
                "    def inner():\n" +
                "        return helper()\n" +  // line 3 -> outer.inner
                "    return inner\n";
            Assert.AreEqual("outer.inner", CallHierarchyService.FindEnclosingFunction(src, 3));
        }
    }
}
