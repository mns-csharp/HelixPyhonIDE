using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 417 (SPPFE Filtering): a kind dropdown narrows the tree to one symbol kind (Classes /
    /// Methods / Properties / Fields / Functions), keeping container ancestors for context and
    /// composing with the text search. "All" restores the full tree.
    ///
    /// Driven on the Solution Explorer of a real loaded solution. This used to hand-build a
    /// SolutionModel under <c>C:\ws\</c> containing a "Vehicle" class with exactly one member of
    /// each kind, then feed it to a bare ProjectTreePanel — a tree with one of everything and no
    /// packages, folders or duplicate names, which is the arrangement least able to expose a filter
    /// bug. The real fixture has Protocols, Enums, ABCs, generics and many same-named members across
    /// packages, and the symbols asserted on are whatever the parser actually produced.
    /// </summary>
    [TestClass]
    public class ProjectTreeKindFilterTests
    {
        // Loaded ONCE for the class. Opening this fixture is real work — 166 types through the real
        // loader — and doing it per test made a six-test class take minutes and intermittently time
        // out waiting for the Solution Explorer to fill. The filter is pure view state, so one
        // loaded solution serves every case; TestInitialize just resets the dropdown.
        private static MainForm _form;
        private static SolutionModel _solution;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            _form = TestMainForm.CreateComposed();
            FixtureSolution.RealizeHeadless(_form);
            _solution = FixtureSolution.OpenSolutionForTree(
                _form, FixtureSolution.Path_("python", "python-test-sample-oop"));

            int types = 0;
            foreach (ProjectModel p in _solution.Projects) types += p.Types.Count;
            Assert.IsTrue(types > 0,
                "the loaded solution has no types — the open did not complete (" +
                _solution.Projects.Count + " project(s))");

            Assert.IsNotNull(_form._projectTreePanel, "the IDE must have a Solution Explorer");
            int nodes = CountNodes(Tree(_form._projectTreePanel));
            Assert.IsTrue(nodes > 0,
                "the Solution Explorer is empty after loading " + _solution.FilePath +
                " (" + types + " type(s) in the model) — the tree was never populated");
        }

        [TestInitialize]
        public void ResetFilter()
        {
            SelectKind(Panel(), "All");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            if (_form == null) return;
            FixtureSolution.TearDown(_form);
            _form = null;
        }

        /// <summary>The IDE's own Solution Explorer, populated by the real load.</summary>
        private ProjectTreePanel Panel()
        {
            Assert.IsNotNull(_form._projectTreePanel, "the IDE must have a Solution Explorer");
            return _form._projectTreePanel;
        }

        private static void SelectKind(ProjectTreePanel panel, string label)
        {
            ComboBox c = Combo(panel);
            int i = c.FindStringExact(label);
            Assert.IsTrue(i >= 0, "the kind dropdown has no '" + label + "' entry");
            c.SelectedIndex = i;
            Application.DoEvents();
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel)
                .GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static ComboBox Combo(ProjectTreePanel panel)
        {
            return (ComboBox)typeof(ProjectTreePanel)
                .GetField("_kindCombo", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static List<string> LeafSymbolTexts(TreeView tree)
        {
            // The real Solution Explorer fills child nodes lazily, so a symbol deep in a package is
            // simply absent from the node graph until its ancestors expand. The old hand-built model
            // was a single project with one class, where everything existed up front — which is why
            // this walk needed no expansion before and does now.
            for (int pass = 0; pass < 6; pass++)
            {
                int before = CountNodes(tree);
                tree.ExpandAll();
                Application.DoEvents();
                if (CountNodes(tree) == before) break;
            }

            var texts = new List<string>();
            Action<TreeNode> walk = null;
            walk = n =>
            {
                if (n.Tag is TypeModel || n.Tag is MemberModel || n.Tag is FunctionModel) texts.Add(n.Text);
                foreach (TreeNode c in n.Nodes) walk(c);
            };
            foreach (TreeNode r in tree.Nodes) walk(r);
            return texts;
        }

        private static bool AnyContains(List<string> texts, string needle)
        {
            return texts.Any(t => t.IndexOf(needle, StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// Every visible node that carries a TypeModel, with its declared kind — so an assertion can
        /// say "no Enum is displayed" instead of "no node text contains 'Color'", which would also
        /// trip over an unrelated symbol that merely shares the substring.
        /// </summary>
        private static List<TypeModel> VisibleTypes(TreeView tree)
        {
            var types = new List<TypeModel>();
            Action<TreeNode> walk = null;
            walk = n =>
            {
                var t = n.Tag as TypeModel;
                if (t != null) types.Add(t);
                foreach (TreeNode c in n.Nodes) walk(c);
            };
            foreach (TreeNode r in tree.Nodes) walk(r);
            return types;
        }

        private static string Describe(IEnumerable<TypeModel> types)
        {
            return string.Join(", ", types.Select(t => t.Name + "(" + t.TypeKind + ")").ToArray());
        }

        private static int CountNodes(TreeView tree)
        {
            int n = 0;
            Action<TreeNode> walk = null;
            walk = t => { n++; foreach (TreeNode c in t.Nodes) walk(c); };
            foreach (TreeNode r in tree.Nodes) walk(r);
            return n;
        }

        [TestMethod]
        public void MethodsFilter_ShowsOnlyMethodsAndConstructors()
        {
            TypeModel owner;
            MemberModel method = FixtureSolution.FirstMember(_solution, MemberKind.Method, out owner);
            TypeModel propOwner;
            MemberModel property = FixtureSolution.FirstMember(_solution, MemberKind.Property, out propOwner);
            FunctionModel function = FixtureSolution.FirstFunction(_solution);

            SelectKind(Panel(), "Methods");
            var texts = LeafSymbolTexts(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, method.Name), "the real method " + method.Name + " is shown");
            Assert.IsFalse(AnyContains(texts, property.Name),
                "the real property " + property.Name + " must be hidden");
            Assert.IsFalse(AnyContains(texts, function.Name),
                "the real module function " + function.Name + " must be hidden");
        }

        [TestMethod]
        public void ClassesFilter_ShowsConcreteClasses_HidesMembersInterfacesEnums()
        {
            TypeModel concrete = FixtureSolution.FirstType(_solution, TypeKind.ConcreteClass);
            TypeModel iface = FixtureSolution.FirstType(_solution, TypeKind.Interface);
            TypeModel enumType = FixtureSolution.FirstType(_solution, TypeKind.Enum);

            SelectKind(Panel(), "Classes");
            var texts = LeafSymbolTexts(Tree(Panel()));
            List<TypeModel> shown = VisibleTypes(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, concrete.Name), "the real class " + concrete.Name + " is shown");

            var interfaces = shown.Where(t => t.TypeKind == TypeKind.Interface).ToList();
            Assert.AreEqual(0, interfaces.Count,
                "the Classes filter must not display Protocols, but shows: " + Describe(interfaces));

            var enums = shown.Where(t => t.TypeKind == TypeKind.Enum).ToList();
            Assert.AreEqual(0, enums.Count,
                "the Classes filter must not display Enums, but shows: " + Describe(enums));
        }

        [TestMethod]
        public void InterfacesFilter_ShowsOnlyProtocols()
        {
            TypeModel iface = FixtureSolution.FirstType(_solution, TypeKind.Interface);
            TypeModel concrete = FixtureSolution.FirstType(_solution, TypeKind.ConcreteClass);
            TypeModel enumType = FixtureSolution.FirstType(_solution, TypeKind.Enum);

            SelectKind(Panel(), "Interfaces");
            var texts = LeafSymbolTexts(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, iface.Name), "the real Protocol " + iface.Name + " is shown");
            Assert.IsFalse(AnyContains(texts, concrete.Name), "a concrete class is hidden");
            Assert.IsFalse(AnyContains(texts, enumType.Name), "an enum is hidden");
        }

        [TestMethod]
        public void EnumsFilter_ShowsOnlyEnums()
        {
            TypeModel enumType = FixtureSolution.FirstType(_solution, TypeKind.Enum);
            TypeModel concrete = FixtureSolution.FirstType(_solution, TypeKind.ConcreteClass);
            TypeModel iface = FixtureSolution.FirstType(_solution, TypeKind.Interface);

            SelectKind(Panel(), "Enums");
            var texts = LeafSymbolTexts(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, enumType.Name), "the real Enum " + enumType.Name + " is shown");
            Assert.IsFalse(AnyContains(texts, concrete.Name), "a concrete class is hidden");
            Assert.IsFalse(AnyContains(texts, iface.Name), "an interface is hidden");
        }

        [TestMethod]
        public void FunctionsFilter_ShowsModuleFunctionsOnly()
        {
            FunctionModel function = FixtureSolution.FirstFunction(_solution);
            TypeModel owner;
            MemberModel method = FixtureSolution.FirstMember(_solution, MemberKind.Method, out owner);

            SelectKind(Panel(), "Functions");
            var texts = LeafSymbolTexts(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, function.Name),
                "the real module function " + function.Name + " is shown");
            Assert.IsFalse(AnyContains(texts, method.Name), "methods are hidden");
        }

        [TestMethod]
        public void AllFilter_RestoresEverything()
        {
            TypeModel iface = FixtureSolution.FirstType(_solution, TypeKind.Interface);
            TypeModel enumType = FixtureSolution.FirstType(_solution, TypeKind.Enum);
            FunctionModel function = FixtureSolution.FirstFunction(_solution);
            TypeModel propOwner;
            MemberModel property = FixtureSolution.FirstMember(_solution, MemberKind.Property, out propOwner);

            SelectKind(Panel(), "Methods");   // narrow
            SelectKind(Panel(), "All");       // back to All
            var texts = LeafSymbolTexts(Tree(Panel()));

            Assert.IsTrue(AnyContains(texts, property.Name), property.Name + " is back");
            Assert.IsTrue(AnyContains(texts, function.Name), function.Name + " is back");
            Assert.IsTrue(AnyContains(texts, iface.Name), iface.Name + " is back");
            Assert.IsTrue(AnyContains(texts, enumType.Name), enumType.Name + " is back");
        }
    }
}
