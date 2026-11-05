using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// What the machine scan is willing to look for. The catalog is data, so these are assertions
    /// about the data — cheap, and they are the only thing standing between "we support that kit"
    /// and a row nobody ever added.
    /// </summary>
    [TestClass]
    public class ToolProbeCatalogTests
    {
        private static IList<ToolProbe> ProbesFor(string language)
        {
            var kit = new List<ToolProbe>();
            foreach (ToolProbe p in ToolProbeCatalog.All())
                if (string.Equals(p.Language, language, StringComparison.Ordinal)) kit.Add(p);
            return kit;
        }

        private static bool AnyDirectoryContains(IList<ToolProbe> probes, string fragment)
        {
            foreach (ToolProbe p in probes)
                foreach (string dir in p.ExtraDirectories)
                    if (dir.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        [TestMethod]
        public void RustIsLookedForWhereRustupActuallyPutsIt()
        {
            IList<ToolProbe> rust = ProbesFor("Rust");
            CollectionAssert.AreEquivalent(
                new[] { "rustc", "cargo", "rustup" },
                Executables(rust), "the Rust kit is its compiler, its build tool and its toolchain manager");

            Assert.IsTrue(AnyDirectoryContains(rust, ".cargo"),
                "rustup installs into ~/.cargo/bin and does not touch the machine PATH — without "
                + "that hint an installed Rust is reported as 'not found on this machine'");
        }

        [TestMethod]
        public void ZigIsLookedForInAnUnzippedFolder_NotJustOnPath()
        {
            IList<ToolProbe> zig = ProbesFor("Zig");
            Assert.AreEqual(1, zig.Count);
            Assert.IsTrue(AnyDirectoryContains(zig, "zig"),
                "Zig ships as a zip you unpack somewhere — PATH is the exception, not the rule");
        }

        [TestMethod]
        public void TheDataTierIsProbedToo()
        {
            IList<ToolProbe> databases = ProbesFor("Databases (RDBMS)");
            Assert.IsTrue(databases.Count >= 5,
                "an RDBMS client is as much a part of a project's environment as its compiler");

            var executables = new List<string>(Executables(databases));
            foreach (string client in new[] { "sqlite3", "psql", "mysql", "sqlcmd", "sqlplus" })
                Assert.IsTrue(executables.Contains(client), client + " is not probed for");
        }

        [TestMethod]
        public void TheCandidateKitsAreAllPresent()
        {
            // The languages the authoring corpus covers. Each is a kit HelixPython may grow to
            // open, and the first question every one of them raises is whether the toolchain is
            // already here — so the inventory answers it before the IDE can do anything else.
            string[] candidates =
            {
                "Ada", "Bash", "C3", "COBOL", "Clojure", "D", "Dart", "Elixir", "Erlang", "F#",
                "Fortran", "Groovy", "Haskell", "Kotlin", "Lua", "OCaml", "Octave", "Perl", "PHP",
                "PowerShell", "Prolog", "R", "Ruby", "Scala", "Scheme", "Solidity", "Swift", "Tcl",
                "Zig",
            };

            IList<string> languages = ToolProbeCatalog.Languages();
            var missing = new List<string>();
            foreach (string candidate in candidates)
                if (!languages.Contains(candidate)) missing.Add(candidate);

            Assert.AreEqual(0, missing.Count, "no probe for: " + string.Join(", ", missing.ToArray()));
        }

        [TestMethod]
        public void BashDoesNotResolveToTheWslLauncher()
        {
            IList<ToolProbe> bash = ProbesFor("Bash");
            Assert.AreEqual(1, bash.Count);

            bool excludesSystem32 = false;
            foreach (string dir in bash[0].ExcludedDirectories)
                if (dir.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0) excludesSystem32 = true;

            Assert.IsTrue(excludesSystem32,
                "System32's bash.exe is the WSL launcher: it precedes Git's bash on PATH and, with "
                + "no distro installed, never answers — measured as a 6-second timeout and a row "
                + "reading 'present, but did not answer in time' for a working Bash");
        }

        [TestMethod]
        public void EveryProbeIsWellFormed()
        {
            foreach (ToolProbe probe in ToolProbeCatalog.All())
            {
                string who = probe.Language + " / " + probe.Component;
                Assert.IsFalse(string.IsNullOrEmpty(probe.Language), "a probe with no kit: " + who);
                Assert.IsFalse(string.IsNullOrEmpty(probe.Component), "a probe with no tool: " + who);
                Assert.IsFalse(string.IsNullOrEmpty(probe.Executable), "a probe with nothing to run: " + who);
                Assert.IsNotNull(probe.ExtraDirectories, who);
                Assert.IsNotNull(probe.ExtraDirectoryEnvironmentVariables, who);
                Assert.IsNotNull(probe.ExcludedDirectories, who);

                // A pattern that does not compile silently yields "installed, version unknown" for
                // a tool that answered perfectly well.
                if (probe.VersionPattern == null) continue;
                try { new Regex(probe.VersionPattern); }
                catch (ArgumentException ex)
                {
                    Assert.Fail("unparsable version pattern for " + who + ": " + ex.Message);
                }
            }
        }

        [TestMethod]
        public void AnInterpreterWithNoVersionFlag_CarriesTheQuestionInstead()
        {
            // tclsh has no version switch at all: run bare, it waits at its REPL and the probe
            // times out. The catalog answers that with a one-line script, not with a missing row.
            foreach (ToolProbe probe in ToolProbeCatalog.All())
            {
                if (probe.Executable != "tclsh") continue;
                Assert.IsFalse(string.IsNullOrEmpty(probe.StandardInput),
                    "tclsh must be asked in Tcl, or it sits at a prompt until the timeout");
                return;
            }
            Assert.Fail("no tclsh probe");
        }

        private static string[] Executables(IList<ToolProbe> probes)
        {
            var names = new List<string>();
            foreach (ToolProbe p in probes) names.Add(p.Executable);
            return names.ToArray();
        }
    }
}
