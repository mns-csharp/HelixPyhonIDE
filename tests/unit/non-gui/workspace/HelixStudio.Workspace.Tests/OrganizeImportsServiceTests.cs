using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class OrganizeImportsServiceTests
    {
        [TestMethod]
        public void RemovesFullyUnusedImportLine()
        {
            string src = "import os\nimport sys\n\nprint(sys.argv)\n";
            // os is unused; sys is used.
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual("import sys\n\nprint(sys.argv)\n", outp);
        }

        [TestMethod]
        public void DropsUnusedNameFromMultiImport()
        {
            string src = "from math import sin, cos, tan\n\nx = cos(0)\n";
            // only cos is used.
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual("from math import cos\n\nx = cos(0)\n", outp);
        }

        [TestMethod]
        public void KeepsAllWhenAllUsed()
        {
            string src = "import os\nimport sys\n\nprint(os.getcwd(), sys.argv)\n";
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual(src, outp, "nothing to remove → source unchanged");
        }

        [TestMethod]
        public void RespectsAlias()
        {
            string src = "import numpy as np\nimport pandas as pd\n\na = np.array([1])\n";
            // pd (pandas alias) is unused; np is used.
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual("import numpy as np\n\na = np.array([1])\n", outp);
        }

        [TestMethod]
        public void UsageInsideFunctionCounts()
        {
            string src = "import os\n\ndef f():\n    return os.getcwd()\n";
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual(src, outp, "used inside a function body → kept");
        }

        [TestMethod]
        public void LeavesStarImportUntouched()
        {
            string src = "from os import *\n\nx = 1\n";
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual(src, outp, "import * names are unknown → left alone");
        }

        [TestMethod]
        public void FromImport_FullyUnused_RemovesLine()
        {
            string src = "from typing import List\nimport os\n\nprint(os.name)\n";
            string outp = OrganizeImportsService.RemoveUnusedImports(src);
            Assert.AreEqual("import os\n\nprint(os.name)\n", outp);
        }
    }
}
