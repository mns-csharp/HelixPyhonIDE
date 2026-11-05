using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    /// <summary>
    /// Locate and invoke MSVC (cl.exe) on Windows via vswhere → vcvars64.bat.
    /// Produces a 64-bit native PE executable from one or more .cpp files.
    /// </summary>
    public static class MsvcToolchain
    {
        /// <summary>Well-known vswhere.exe path installed with every recent VS (≥2017).</summary>
        public const string VswherePath =
            @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";

        /// <summary>
        /// Return the path to the latest VS install that includes the VC toolset, or
        /// <c>null</c> if none is found (vswhere missing, or no VS with C++ tools).
        /// </summary>
        public static string LocateVsInstall()
        {
            if (!File.Exists(VswherePath)) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = VswherePath,
                    Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    return string.IsNullOrWhiteSpace(output) ? null : output;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Compile <paramref name="cppFiles"/> into <paramref name="outExePath"/> using MSVC
        /// in the x64 host/target environment, C++20 mode with exceptions enabled (/EHsc —
        /// required for pyrt's exception hierarchy and FinallyGuard). Writes a helper batch
        /// file that sources <c>vcvars64.bat</c> and invokes <c>cl.exe</c>; output is
        /// captured. Returns (exitCode, combinedLog). exitCode == 0 means success.
        /// </summary>
        public static (int exitCode, string log) Compile(
            IEnumerable<string> cppFiles, string outExePath, string objDir)
        {
            if (outExePath == null) throw new ArgumentNullException(nameof(outExePath));
            var log = new StringBuilder();
            string vsInstall = LocateVsInstall();
            if (string.IsNullOrEmpty(vsInstall))
            {
                log.AppendLine("[msvc] ERROR: could not locate a Visual Studio install with VC tools.");
                log.AppendLine("[msvc]        vswhere path probed: " + VswherePath);
                return (1, log.ToString());
            }
            string vcvars = Path.Combine(vsInstall, @"VC\Auxiliary\Build\vcvars64.bat");
            if (!File.Exists(vcvars))
            {
                log.AppendLine("[msvc] ERROR: vcvars64.bat not found at " + vcvars);
                return (1, log.ToString());
            }

            Directory.CreateDirectory(objDir);
            Directory.CreateDirectory(Path.GetDirectoryName(outExePath));

            // Build the cl.exe command line. Quoted /Fe: and /Fo: values; one positional per .cpp.
            // /TP forces C++ compilation mode regardless of the .cpp extension (belt-and-braces —
            // cl.exe already infers C++ from ".cpp", but /TP makes the intent explicit).
            var clArgs = new StringBuilder();
            // C++17, not C++20, because C++17 is what the emitted code actually needs and C++20 is
            // what silently breaks it on an older toolchain. pyrt.hpp uses std::optional,
            // std::string_view and <charconv>; nothing in it or in PythonToCppEmitter is newer than
            // C++17. MSVC 14.16 (the VC tools that ship with VS2017) does not accept /std:c++20 --
            // it prints "D9002: ignoring unknown option", falls back to C++14, and every one of
            // those C++17 headers then fails to compile. Asking for the standard we use, rather
            // than the newest one we can name, builds on every toolchain from VS2017 15.3 onwards.
            clArgs.Append("/std:c++17 /EHsc /nologo /TP ");
            // /Fe specifies the output executable path; /Fo specifies the object-file output dir.
            clArgs.Append("/Fe:\"").Append(outExePath).Append("\" ");
            // A quoted argument ending in a single backslash-then-quote (..."\") is misparsed by
            // the C runtime's argv parser: the backslash escapes the quote instead of closing the
            // string, so the rest of the command line (including the source file) gets absorbed
            // into this one argument and cl.exe sees no source filename at all. /Fo requires the
            // trailing backslash to mark it as a directory, so double it instead of dropping it —
            // an even run of backslashes before a quote is CRT-parsed as literal backslashes with
            // the quote still closing the string.
            clArgs.Append("/Fo:\"").Append(objDir.TrimEnd('\\')).Append("\\\\\" ");
            foreach (string f in cppFiles)
            {
                clArgs.Append('"').Append(f).Append("\" ");
            }

            // We spawn cmd.exe so that the vcvars batch sets up the environment before cl.exe runs.
            // The outer quoting on cmd /c is: cmd /c ""cmd1" && "cmd2""
            string combined =
                "\"\"" + vcvars + "\" && cl.exe " + clArgs.ToString().Trim() + "\"";

            log.AppendLine("[msvc] VS install: " + vsInstall);
            log.AppendLine("[msvc] cl.exe args: " + clArgs.ToString().Trim());

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + combined,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    // Read asynchronously to avoid deadlock when both streams are large.
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                    p.ErrorDataReceived  += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return (p.ExitCode, log.ToString());
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("[msvc] exception launching cl.exe: " + ex.Message);
                return (1, log.ToString());
            }
        }
    }
}
