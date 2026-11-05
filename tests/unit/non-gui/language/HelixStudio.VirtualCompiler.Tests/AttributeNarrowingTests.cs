using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 370 (click stress-test): `if self.metavar is not None: return self.metavar` false-flagged
    /// PY6005 "return type 'str | None' is not assignable to annotated 'str'" — the checker narrowed
    /// local NAMES but not ATTRIBUTE paths, so an Optional attribute stayed Optional inside its own
    /// `is not None` guard. Attribute paths ("self.metavar") are now narrowed like names.
    /// </summary>
    [TestClass]
    public class AttributeNarrowingTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Cls =
            "class P:\n" +
            "    metavar: str | None\n";

        [TestMethod]
        public void IsNotNoneGuard_NarrowsTheAttribute()
        {
            var r = VirtualCompiler.CompileSource(Cls +
                "    def make(self) -> str:\n" +
                "        if self.metavar is not None:\n" +
                "            return self.metavar\n" +
                "        return 'x'\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void TruthyGuard_NarrowsTheAttribute()
        {
            var r = VirtualCompiler.CompileSource(Cls +
                "    def make(self) -> str:\n" +
                "        if self.metavar:\n" +
                "            return self.metavar\n" +
                "        return 'y'\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        /// <summary>The narrowing is real, not blanket leniency: an un-guarded Optional return still fails.</summary>
        [TestMethod]
        public void ReturningTheOptionalAttribute_UnguardedStillFails()
        {
            var r = VirtualCompiler.CompileSource(Cls +
                "    def bad(self) -> str:\n" +
                "        return self.metavar\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }

        /// <summary>
        /// An early-return `is None` guard narrows the attribute for the code AFTER it (iter 460): once
        /// `if self.metavar is None: return ...` has run, self.metavar is non-None, so the later
        /// `return self.metavar` is valid — exactly as mypy narrows it. (Returning self.metavar from
        /// inside the None branch is a None value, which this checker deliberately exempts as the
        /// under-annotated-Optional pattern, so the whole method is clean.)
        /// </summary>
        [TestMethod]
        public void EarlyReturnOnNone_NarrowsTheAttributeAfterTheGuard()
        {
            var r = VirtualCompiler.CompileSource(Cls +
                "    def make(self) -> str:\n" +
                "        if self.metavar is None:\n" +
                "            return 'default'\n" +
                "        return self.metavar\n");   // metavar is non-None (str) here -> valid
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        /// <summary>A missing attribute must not be double-reported by the extra narrowing inference.</summary>
        [TestMethod]
        public void MissingAttributeInAGuard_ReportsExactlyOnce()
        {
            var r = VirtualCompiler.CompileSource(Cls +
                "    def m(self) -> None:\n" +
                "        if self.nope is not None:\n" +
                "            pass\n");
            int count = r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                                 && d.Message.Contains("'nope'"));
            Assert.AreEqual(1, count, "the missing attribute should be reported once, not twice: " + Dump(r));
        }
    }
}
