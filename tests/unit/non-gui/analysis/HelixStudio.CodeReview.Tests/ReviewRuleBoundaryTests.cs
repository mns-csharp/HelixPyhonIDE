namespace HelixPythonNamespace.CodeReview.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Each test here pins one rule's BOUNDARY: the shape it must report, paired with the
    /// neighbouring shape it must stay silent on. The silent half is the one that matters — a
    /// rule with no carve-out fires on correct code, and a category that fires on correct code
    /// stops being read.
    /// </summary>
    [TestClass]
    public class ReviewRuleBoundaryTests
    {
        [TestMethod]
        public void ModuleInitializationFires_ButSuperInitializationDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn as nn\nclass Net(nn.Module):\n    def __init__(self):\n        self.layer = nn.Linear(2, 2)\n");
            Assert.IsTrue(bad.Has("CR-ML-001"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn as nn\nclass Net(nn.Module):\n    def __init__(self):\n        super().__init__()\n        self.layer = nn.Linear(2, 2)\n");
            Assert.IsFalse(good.Has("CR-ML-001"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void LayerInForwardFires_ButRegisteredLayerDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn as nn\nclass Net(nn.Module):\n    def forward(self, x):\n        return nn.Linear(2, 2)(x)\n");
            Assert.IsTrue(bad.Has("CR-ML-002"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn as nn\nclass Net(nn.Module):\n    def __init__(self):\n        super().__init__()\n        self.layer = nn.Linear(2, 2)\n    def forward(self, x):\n        return self.layer(x)\n");
            Assert.IsFalse(good.Has("CR-ML-002"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void TensorDataFires_ButDetachDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("train.py", "import torch\ndef f(x):\n    return x.data\n");
            Assert.IsTrue(bad.Has("CR-ML-003"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("train.py", "import torch\ndef f(x):\n    return x.detach()\n");
            Assert.IsFalse(good.Has("CR-ML-003"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void BackwardWithoutResetFires_ButResetBeforeBackwardDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("train.py", "def train(loss, optimizer):\n    loss.backward()\n    optimizer.step()\n");
            Assert.IsTrue(bad.Has("CR-ML-004"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("train.py", "def train(loss, optimizer):\n    optimizer.zero_grad()\n    loss.backward()\n    optimizer.step()\n");
            Assert.IsFalse(good.Has("CR-ML-004"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void FunctionalDropoutWithoutModeFires_ButTrainingStateDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn.functional as F\nclass Net:\n    def forward(self, x):\n        return F.dropout(x)\n");
            Assert.IsTrue(bad.Has("CR-ML-005"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("model.py",
                "import torch.nn.functional as F\nclass Net:\n    def forward(self, x):\n        return F.dropout(x, training=self.training)\n");
            Assert.IsFalse(good.Has("CR-ML-005"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void DirectNumpyConversionFires_ButDetachedCpuConversionDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("predict.py", "import torch\ndef f(x):\n    return x.numpy()\n");
            Assert.IsTrue(bad.Has("CR-ML-006"), ReviewFixture.Describe(bad));
            ReviewReport good = ReviewFixture.ReviewSnippet("predict.py", "import torch\ndef f(x):\n    return x.detach().cpu().numpy()\n");
            Assert.IsFalse(good.Has("CR-ML-006"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void ReturnInFinallyFires_ButABreakBelongingToALoopInsideItDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "def f():\n" +
                "    try:\n" +
                "        return 1\n" +
                "    finally:\n" +
                "        return 2\n");
            Assert.IsTrue(bad.Has("CR-COR-003"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "def f(items):\n" +
                "    try:\n" +
                "        return 1\n" +
                "    finally:\n" +
                "        for item in items:\n" +
                "            break\n");
            Assert.IsFalse(good.Has("CR-COR-003"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void DuplicateDefinitionFires_ButAPropertyAndItsSetterDoNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "class A:\n" +
                "    def m(self):\n" +
                "        return 1\n" +
                "    def m(self):\n" +
                "        return 2\n");
            Assert.IsTrue(bad.Has("CR-COR-011"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "class A:\n" +
                "    @property\n" +
                "    def m(self):\n" +
                "        return self._m\n" +
                "    @m.setter\n" +
                "    def m(self, value):\n" +
                "        self._m = value\n");
            Assert.IsFalse(good.Has("CR-COR-011"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void SelfComparisonIsMajor_ButTheNaNIdiomIsOnlyAdvisory()
        {
            ReviewReport equality = ReviewFixture.ReviewSnippet("a.py",
                "def f(x):\n" +
                "    if x == x:\n" +
                "        return 1\n" +
                "    return 0\n");
            Assert.AreEqual(ReviewSeverity.Major, equality.First("CR-COR-009").Severity);

            ReviewReport nan = ReviewFixture.ReviewSnippet("b.py",
                "def f(x):\n" +
                "    if x != x:\n" +
                "        return 1\n" +
                "    return 0\n");
            Assert.AreEqual(ReviewSeverity.Info, nan.First("CR-COR-009").Severity);
        }

        [TestMethod]
        public void UnsafeYamlLoadFires_ButAnExplicitSafeLoaderDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "import yaml\n" +
                "def f(text):\n" +
                "    return yaml.load(text)\n");
            Assert.IsTrue(bad.Has("CR-SEC-003"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "import yaml\n" +
                "def f(text):\n" +
                "    return yaml.load(text, Loader=yaml.SafeLoader)\n");
            Assert.IsFalse(good.Has("CR-SEC-003"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void EvalOfUserDataFires_ButEvalOfALiteralDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "def f(expression):\n" +
                "    return eval(expression)\n");
            Assert.AreEqual(ReviewSeverity.Blocker, bad.First("CR-SEC-001").Severity);

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "def f():\n" +
                "    return eval(\"1 + 1\")\n");
            Assert.IsFalse(good.Has("CR-SEC-001"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void AssertOnAnArgumentFiresInProductionCode_ButNotInATest()
        {
            ReviewReport production = ReviewFixture.ReviewSnippet("service.py",
                "def f(value):\n" +
                "    assert value > 0\n" +
                "    return value\n");
            Assert.IsTrue(production.Has("CR-SEC-010"), ReviewFixture.Describe(production));

            ReviewReport test = ReviewFixture.ReviewSnippet("test_service.py",
                "def test_f(value):\n" +
                "    assert value > 0\n");
            Assert.IsFalse(test.Has("CR-SEC-010"), ReviewFixture.Describe(test));
        }

        [TestMethod]
        public void ACallInADefaultArgumentFires_ButADeclarativeFrameworkDefaultDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "import time\n" +
                "def f(stamp=time.time()):\n" +
                "    return stamp\n");
            Assert.IsTrue(bad.Has("CR-COR-010"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "from dataclasses import field\n" +
                "def f(items=field(default_factory=list)):\n" +
                "    return items\n");
            Assert.IsFalse(good.Has("CR-COR-010"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void MutatingTheIteratedListFires_ButIteratingACopyDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "def f(items):\n" +
                "    for item in items:\n" +
                "        items.remove(item)\n");
            Assert.IsTrue(bad.Has("CR-COR-001"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "def f(items):\n" +
                "    for item in list(items):\n" +
                "        items.remove(item)\n");
            Assert.IsFalse(good.Has("CR-COR-001"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void StringAccumulationInALoopFires_ButNumericAccumulationDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "def f(items):\n" +
                "    text = \"\"\n" +
                "    for item in items:\n" +
                "        text += str(item)\n" +
                "    return text\n");
            Assert.IsTrue(bad.Has("CR-PERF-001"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "def f(items):\n" +
                "    total = 0\n" +
                "    for item in items:\n" +
                "        total += item\n" +
                "    return total\n");
            Assert.IsFalse(good.Has("CR-PERF-001"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void MutableModuleStateFires_ButAnAllCapsConstantDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "cache = {}\n" +
                "def remember(key, value):\n" +
                "    cache.update({key: value})\n");
            Assert.IsTrue(bad.Has("CR-DES-009"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "DEFAULTS = {}\n" +
                "def remember(key, value):\n" +
                "    DEFAULTS.update({key: value})\n");
            Assert.IsFalse(good.Has("CR-DES-009"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void AnObjectChainFires_ButAModulePathDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "def f(order):\n" +
                "    return order.customer.account.balance()\n");
            Assert.IsTrue(bad.Has("CR-DES-006"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "import os\n" +
                "def f(a, b):\n" +
                "    return os.path.sep.join([a, b])\n");
            Assert.IsFalse(good.Has("CR-DES-006"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void AnEmptyBodyFires_ButAnAbstractMethodDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "class A:\n" +
                "    def handle(self, event):\n" +
                "        pass\n");
            Assert.IsTrue(bad.Has("CR-DES-012"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "from abc import ABC, abstractmethod\n" +
                "class A(ABC):\n" +
                "    @abstractmethod\n" +
                "    def handle(self, event):\n" +
                "        pass\n");
            Assert.IsFalse(good.Has("CR-DES-012"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void ACatchAllThatHandlesFires_ButOneThatReRaisesDoesNot()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("a.py",
                "import logging\n" +
                "def f(step):\n" +
                "    try:\n" +
                "        step()\n" +
                "    except Exception:\n" +
                "        logging.exception(\"failed\")\n" +
                "        return None\n");
            Assert.IsTrue(bad.Has("CR-ERR-002"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("b.py",
                "import logging\n" +
                "def f(step):\n" +
                "    try:\n" +
                "        step()\n" +
                "    except Exception:\n" +
                "        logging.exception(\"failed\")\n" +
                "        raise\n");
            Assert.IsFalse(good.Has("CR-ERR-002"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void ATestWithNoAssertionFires_ButPytestRaisesCounts()
        {
            ReviewReport bad = ReviewFixture.ReviewSnippet("test_a.py",
                "def test_it(subject):\n" +
                "    subject.run()\n");
            Assert.IsTrue(bad.Has("CR-TST-001"), ReviewFixture.Describe(bad));

            ReviewReport good = ReviewFixture.ReviewSnippet("test_b.py",
                "import pytest\n" +
                "def test_it(subject):\n" +
                "    with pytest.raises(ValueError):\n" +
                "        subject.run()\n");
            Assert.IsFalse(good.Has("CR-TST-001"), ReviewFixture.Describe(good));
        }

        [TestMethod]
        public void MutualImportsFire_ButAOneWayImportDoesNot()
        {
            var cycle = new List<ReviewSource>
            {
                new ReviewSource("alpha.py", "import beta\n\ndef a():\n    return beta.b()\n"),
                new ReviewSource("beta.py", "import alpha\n\ndef b():\n    return alpha.a()\n"),
            };
            ReviewReport bad = new CodeReviewEngine().Review(cycle);
            Assert.IsTrue(bad.Has("CR-XF-001"), ReviewFixture.Describe(bad));

            var oneWay = new List<ReviewSource>
            {
                new ReviewSource("alpha.py", "import beta\n\ndef a():\n    return beta.b()\n"),
                new ReviewSource("beta.py", "def b():\n    return 1\n"),
            };
            ReviewReport good = new CodeReviewEngine().Review(oneWay);
            Assert.IsFalse(good.Has("CR-XF-001"), ReviewFixture.Describe(good));
        }

        /// <summary>
        /// The "untested public type" rule has nothing to say when the submission contains no
        /// tests at all — "nothing is tested" is not a comment about any particular class.
        /// </summary>
        [TestMethod]
        public void TheUntestedTypeRuleStaysSilentWhenNoTestsWereSubmitted()
        {
            var withoutTests = new List<ReviewSource>
            {
                new ReviewSource("service.py", "class Service:\n    def run(self):\n        return 1\n"),
            };
            Assert.IsFalse(new CodeReviewEngine().Review(withoutTests).Has("CR-XF-003"));

            var withTests = new List<ReviewSource>
            {
                new ReviewSource("service.py", "class Service:\n    def run(self):\n        return 1\n"),
                new ReviewSource("test_other.py", "def test_nothing():\n    assert True\n"),
            };
            Assert.IsTrue(new CodeReviewEngine().Review(withTests).Has("CR-XF-003"));
        }

        [TestMethod]
        public void PublicTypeReferencedThroughPackageAliasCountsAsTested()
        {
            var sources = new List<ReviewSource>
            {
                new ReviewSource("service.py", "class Service:\n    def run(self):\n        return 1\n"),
                new ReviewSource("test_service.py", "import package as api\ndef test_service():\n    assert api.Service().run() == 1\n")
            };
            ReviewReport report = new CodeReviewEngine().Review(sources);
            Assert.IsFalse(report.Has("CR-XF-003"), ReviewFixture.Describe(report));
        }
    }
}
