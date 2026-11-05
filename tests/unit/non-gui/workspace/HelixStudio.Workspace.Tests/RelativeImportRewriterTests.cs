using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class RelativeImportRewriterTests
    {
        [TestMethod]
        public void Rewrite_BareDotImport_BecomesAbsolute()
        {
            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(
                "from . import shared\n", "app_lib");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from app_lib import shared\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_DottedModuleImport_BecomesAbsolute()
        {
            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(
                "from .logging_setup import configure_logging\n", "app_lib");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from app_lib.logging_setup import configure_logging\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_MultipleImports_AllRewritten_OrderAndSpacingPreserved()
        {
            string source =
                "from . import io_utils\n" +
                "from . import chain\n" +
                "from .config import SimConfig\n" +
                "import argparse\n";

            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(source, "mylib");

            Assert.AreEqual(3, r.Count);
            Assert.AreEqual(
                "from mylib import io_utils\n" +
                "from mylib import chain\n" +
                "from mylib.config import SimConfig\n" +
                "import argparse\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_AbsoluteImport_IsUntouched()
        {
            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(
                "import os\nfrom collections import OrderedDict\n", "mylib");
            Assert.AreEqual(0, r.Count);
        }

        [TestMethod]
        public void Rewrite_DeferredImportInsideFunctionBody_IsRewritten()
        {
            // A relative import deferred inside a function (a common pattern to avoid
            // circular imports, or a heavy/optional import) is just as much a
            // sibling-relative import as one at module level. This is the exact real-world
            // shape that used to raise "ImportError: attempted relative import with no
            // known parent package" at actual run time, because the split-out entry file's
            // deferred import kept pointing at its now-nonexistent original package.
            string source =
                "def main(argv=None):\n" +
                "    from . import observables as _obs\n" +
                "    return _obs.compute()\n";

            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(source, "app_lib");

            Assert.AreEqual(1, r.Count);
            Assert.AreEqual(
                "def main(argv=None):\n" +
                "    from app_lib import observables as _obs\n" +
                "    return _obs.compute()\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_DeferredImportNestedInIfTryFor_AllRewritten()
        {
            string source =
                "def main():\n" +
                "    if True:\n" +
                "        from . import a\n" +
                "    try:\n" +
                "        from . import b\n" +
                "    except ImportError:\n" +
                "        from . import c\n" +
                "    for _ in range(1):\n" +
                "        from . import d\n";

            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(source, "lib");

            Assert.AreEqual(4, r.Count);
            StringAssert.Contains(r.Text, "from lib import a");
            StringAssert.Contains(r.Text, "from lib import b");
            StringAssert.Contains(r.Text, "from lib import c");
            StringAssert.Contains(r.Text, "from lib import d");
            Assert.IsFalse(r.Text.Contains("from . import"));
        }

        [TestMethod]
        public void Rewrite_DeeperRelativeLevels_AreLeftUntouched()
        {
            // ".." (RelativeDots == 2) is out of scope — a flat single-level package
            // (the only shape PackageSplitter extracts from) never produces one.
            var r = RelativeImportRewriter.RewriteSameLevelRelativeImports(
                "from .. import shared\n", "mylib");
            Assert.AreEqual(0, r.Count);
        }

        [TestMethod]
        public void Rewrite_NullOrEmptySource_IsSafe()
        {
            Assert.AreEqual(0, RelativeImportRewriter.RewriteSameLevelRelativeImports(null, "mylib").Count);
            Assert.AreEqual(0, RelativeImportRewriter.RewriteSameLevelRelativeImports("", "mylib").Count);
        }
    }
}
