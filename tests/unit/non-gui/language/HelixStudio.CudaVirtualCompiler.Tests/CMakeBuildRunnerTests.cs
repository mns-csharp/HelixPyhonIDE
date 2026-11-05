namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaVirtualCompiler;

    /// <summary>
    /// FR-PM-04: drives the REAL cmake toolchain to configure and build a generated native project.
    /// Reports Inconclusive (not failed) when cmake / a C++ compiler isn't present, so CI without a
    /// toolchain still passes.
    /// </summary>
    [TestClass]
    public class CMakeBuildRunnerTests
    {
        [TestMethod]
        public void LanguageStandardArgumentsCoverCxx98Through26AndCuda()
        {
            CollectionAssert.AreEqual(
                new[] {
                    "-DCMAKE_CXX_STANDARD=98",
                    "-DCMAKE_CXX_STANDARD_REQUIRED=ON",
                    "-DCMAKE_CXX_EXTENSIONS=OFF"
                },
                CMakeBuildRunner.CreateLanguageStandardArguments("98", "20", false));
            var modern = CMakeBuildRunner.CreateLanguageStandardArguments("26", "23", true);
            CollectionAssert.Contains(modern, "-DCMAKE_CXX_STANDARD=26");
            CollectionAssert.Contains(modern, "-DCMAKE_CUDA_STANDARD=23");
            CollectionAssert.Contains(modern, "-DCMAKE_CUDA_STANDARD_REQUIRED=ON");
            Assert.AreEqual("20", CMakeBuildRunner.NormalizeCxxStandard("invalid", "20"));
        }
        private static string NewProjectDir(string cmakeText, string mainText, string mainName = "main.cpp")
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_cmk_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), cmakeText);
            File.WriteAllText(Path.Combine(dir, mainName), mainText);
            return dir;
        }

        private const string HelloCMake =
            "cmake_minimum_required(VERSION 3.20)\n" +
            "project(hellodemo LANGUAGES CXX)\n" +
            "add_executable(hellodemo main.cpp)\n";

        private const string HelloMain =
            "#include <iostream>\nint main(){ std::cout << \"hi\\n\"; return 0; }\n";

        [TestMethod]
        public void ConfigureAndBuild_ValidProject_ProducesExecutable()
        {
            if (!CMakeBuildRunner.IsAvailable) { Assert.Inconclusive("cmake not available"); return; }

            string src = NewProjectDir(HelloCMake, HelloMain);
            string build = Path.Combine(src, "build");
            try
            {
                CMakeRunResult cfg = CMakeBuildRunner.Configure(src, build, "Ninja");
                if (!cfg.Available) Assert.Inconclusive("cmake configure could not run: " + cfg.Reason);
                if (cfg.ExitCode != 0) Assert.Inconclusive("no working C++/Ninja toolchain: " + cfg.RawOutput);
                StringAssert.Matches(cfg.CMakeVersion, new System.Text.RegularExpressions.Regex(@"\d+\.\d+"));

                CMakeRunResult bld = CMakeBuildRunner.Build(build);
                Assert.IsTrue(bld.Available, "build should run");
                Assert.AreEqual(0, bld.ExitCode, "clean project builds: " + bld.RawOutput);
                Assert.IsTrue(bld.Succeeded);

                // Ground truth: the executable really exists on disk.
                string[] exes = Directory.GetFiles(build, "hellodemo*", SearchOption.AllDirectories);
                bool found = false;
                foreach (string e in exes) if (e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(e) == "hellodemo") found = true;
                Assert.IsTrue(found, "cmake --build produced the hellodemo executable");
            }
            finally { try { Directory.Delete(src, true); } catch { } }
        }

        [TestMethod]
        public void ConfigureBuildAndRun_ValidProject_CapturesProgramOutputAndExitCode()
        {
            if (!CMakeBuildRunner.IsAvailable) { Assert.Inconclusive("cmake not available"); return; }

            const string expected = "HELIX_NATIVE_EXECUTION_OK";
            string src = NewProjectDir(HelloCMake,
                "#include <iostream>\nint main(){ std::cout << \"" + expected + "\\n\"; return 0; }\n");
            string build = Path.Combine(src, "build");
            try
            {
                CMakeRunResult cfg = CMakeBuildRunner.Configure(src, build, "Ninja");
                if (!cfg.Available || cfg.ExitCode != 0)
                    Assert.Inconclusive("no working C++/Ninja toolchain: " + cfg.Reason + " " + cfg.RawOutput);
                CMakeRunResult bld = CMakeBuildRunner.Build(build);
                Assert.AreEqual(0, bld.ExitCode, bld.RawOutput);

                string executable = null;
                foreach (string candidate in Directory.GetFiles(build, "hellodemo*", SearchOption.AllDirectories))
                    if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(candidate), "hellodemo", StringComparison.Ordinal))
                    { executable = candidate; break; }
                Assert.IsNotNull(executable, "built executable");

                CMakeRunResult run = CMakeBuildRunner.RunExecutable(executable);
                Assert.IsTrue(run.Available, run.Reason);
                Assert.AreEqual(0, run.ExitCode, run.RawOutput);
                Assert.AreEqual("run", run.Phase);
                StringAssert.Contains(run.RawOutput, expected);
            }
            finally { try { Directory.Delete(src, true); } catch { } }
        }

        [TestMethod]
        public void Configure_SyntaxErrorInSource_BuildReportsAuthoritativeError()
        {
            if (!CMakeBuildRunner.IsAvailable) { Assert.Inconclusive("cmake not available"); return; }

            // Missing semicolon — a hard compile error that the real toolchain must surface.
            string badMain = "#include <iostream>\nint main(){ std::cout << \"x\\n\" return 0; }\n";
            string src = NewProjectDir(HelloCMake, badMain);
            string build = Path.Combine(src, "build");
            try
            {
                CMakeRunResult cfg = CMakeBuildRunner.Configure(src, build, "Ninja");
                if (!cfg.Available || cfg.ExitCode != 0)
                    Assert.Inconclusive("no working C++/Ninja toolchain: " + cfg.Reason + " " + cfg.RawOutput);

                CMakeRunResult bld = CMakeBuildRunner.Build(build);
                Assert.IsTrue(bld.Available, "build should run");
                Assert.AreNotEqual(0, bld.ExitCode, "a syntax error must fail the build");
                Assert.IsTrue(bld.ErrorCount >= 1, "at least one compiler error parsed: " + bld.RawOutput);
            }
            finally { try { Directory.Delete(src, true); } catch { } }
        }

        [TestMethod]
        public void Configure_MissingCMakeLists_FailsGracefully()
        {
            string empty = Path.Combine(Path.GetTempPath(), "helix_cmk_empty_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                CMakeRunResult cfg = CMakeBuildRunner.Configure(empty, Path.Combine(empty, "build"));
                Assert.IsFalse(cfg.Available);
                Assert.IsNotNull(cfg.Reason);
            }
            finally { try { Directory.Delete(empty, true); } catch { } }
        }

        [TestMethod]
        public void FindGccCppCompiler_ValidPreferenceWinsAndFindsSiblingCCompiler()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_gcc_discovery_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cpp = Path.Combine(dir, "g++.exe");
            string c = Path.Combine(dir, "gcc.exe");
            File.WriteAllBytes(cpp, new byte[] { 0 });
            File.WriteAllBytes(c, new byte[] { 0 });
            try
            {
                Assert.AreEqual(Path.GetFullPath(cpp), CMakeBuildRunner.FindGccCppCompiler(cpp));
                Assert.AreEqual(Path.GetFullPath(c), CMakeBuildRunner.FindGccCCompiler(cpp));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void FindGccCppCompiler_InvalidPreferenceFallsBackToInstalledCompiler()
        {
            string resolved = CMakeBuildRunner.FindGccCppCompiler(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-g++.exe"));
            if (resolved == null) Assert.Inconclusive("No GCC compiler installed for fallback test");
            Assert.IsTrue(File.Exists(resolved), "discovery must return an existing compiler");
            Assert.IsTrue(resolved.EndsWith(
                Environment.OSVersion.Platform == PlatformID.Win32NT ? "g++.exe" : "g++",
                StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void ProbeGccCppCompiler_InstalledCompilerReportsGnuVersionAnd64BitTarget()
        {
            string compiler = CMakeBuildRunner.FindGccCppCompiler();
            if (compiler == null) Assert.Inconclusive("No GCC compiler installed");
            NativeCompilerProbeResult probe = CMakeBuildRunner.ProbeGccCppCompiler(compiler);
            Assert.IsTrue(probe.Available, probe.Reason);
            Assert.IsTrue(probe.IsGnu, probe.RawVersion);
            Assert.IsTrue(probe.Is64Bit, probe.Target);
            Assert.IsTrue(probe.Usable, probe.Reason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(probe.Version));
            Assert.IsFalse(string.IsNullOrWhiteSpace(probe.Target));
        }

        [TestMethod]
        public void ProbeGccCppCompiler_NonExecutablePreferenceIsRejected()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_bad_gcc_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string fake = Path.Combine(dir, "g++.exe");
            File.WriteAllText(fake, "not an executable");
            try
            {
                NativeCompilerProbeResult probe = CMakeBuildRunner.ProbeGccCppCompiler(fake);
                Assert.IsFalse(probe.Usable);
                Assert.IsFalse(string.IsNullOrWhiteSpace(probe.Reason));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void FindMsvcEnvironmentScript_ValidCustomVcVars64Wins()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_vcvars_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "vcvars64.bat");
            File.WriteAllText(script, "@echo off\r\n");
            try
            {
                Assert.AreEqual(Path.GetFullPath(script),
                    CMakeBuildRunner.FindMsvcEnvironmentScript(script));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void FindMsvcEnvironmentScript_InvalidCustomPathDoesNotSilentlyFallback()
        {
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"),
                "vcvars64.bat");
            Assert.IsNull(CMakeBuildRunner.FindMsvcEnvironmentScript(missing));
        }
    }
}
