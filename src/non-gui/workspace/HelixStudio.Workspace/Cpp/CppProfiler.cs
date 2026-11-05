using System;
using System.IO;
using HelixPythonNamespace.Workspace.Profiling;

namespace HelixPythonNamespace.Workspace.Cpp
{
    /// <summary>
    /// FR-PROF (C++): the profiler for plain C++ — the last of the shipped languages without one
    /// (Python had cProfile; the native languages have no equivalent built in,
    /// sampler, CUDA the nvcc occupancy pass; C++ had nothing).
    ///
    /// It samples an ALREADY-BUILT executable through the shared <see cref="NativeSampler"/> rather
    /// than owning a build step: for a C++/CUDA workspace the IDE already knows how to configure and
    /// build with the real CMake toolchain and how to locate a target's binary, so the caller builds
    /// and hands over the exe. That keeps this engine free of any CMake dependency and makes it
    /// equally usable for a binary produced any other way.
    ///
    /// Requires a PDB next to the executable — i.e. a Debug/RelWithDebInfo build. Without symbols
    /// every frame resolves to "[external]" and the run reports that honestly instead of inventing
    /// function names.
    /// </summary>
    public static class CppProfiler
    {
        /// <summary>
        /// Sample <paramref name="executablePath"/> for <paramref name="sampleMs"/> milliseconds.
        /// </summary>
        public static WorkspaceProfile Run(string executablePath, int sampleMs = 4000, string arguments = null)
        {
            var r = new WorkspaceProfile { Tool = "sampler" };
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                r.Reason = "executable not found: " + (executablePath ?? "<null>");
                return r;
            }

            try { NativeSampler.Sample(executablePath, sampleMs, r, IsRuntimeFrame); }
            catch (Exception ex)
            {
                r.Reason = "sampling failed: " + ex.Message;
                r.Available = false;
                return r;
            }

            if (r.Available && r.Samples.Count > 0 && !HasSymbols(r))
                r.Reason = "no symbols resolved — build with debug info (Debug or RelWithDebInfo) " +
                           "so a .pdb sits next to the executable";
            return r;
        }

        private static bool HasSymbols(WorkspaceProfile r)
        {
            foreach (var s in r.Samples)
                if (!string.IsNullOrEmpty(s.Function) && s.Function != "[external]") return true;
            return false;
        }

        /// <summary>
        /// C++ scaffolding to drop from the hot set: the CRT/OS frames every native binary carries,
        /// plus the standard library. A user's own code is what belongs in the panel.
        /// </summary>
        private static bool IsRuntimeFrame(string name)
        {
            if (NativeSampler.IsCommonRuntimeFrame(name)) return true;
            string[] pre = { "std::", "boost::", "operator new", "operator delete",
                             "malloc", "free", "memcpy", "memset" };
            foreach (string p in pre) if (name.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
