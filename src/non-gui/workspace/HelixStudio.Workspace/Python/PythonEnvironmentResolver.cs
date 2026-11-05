using System;
using System.IO;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Works out which interpreter a package runs on, for display only.
    ///
    /// Deliberately cheap: this runs for every package on every solution load, so it looks at the
    /// file system and the environment and never launches an interpreter to ask it questions. A
    /// version number that costs 200ms per package to obtain is not worth 200ms per package.
    ///
    /// Search order mirrors what actually wins at run time: an explicitly bound venv, then the
    /// package's own <c>.venv</c>, then the solution root's, then <c>VIRTUAL_ENV</c>, then nothing.
    /// </summary>
    public static class PythonEnvironmentResolver
    {
        private static readonly string[] VenvFolderNames = { ".venv", "venv", ".env" };

        public static PythonEnvironmentInfo Resolve(ProjectModel project, SolutionModel solution)
        {
            var info = new PythonEnvironmentInfo();
            if (project == null) return info;

            info.RequiresPython = string.IsNullOrEmpty(project.PythonVersion)
                ? null
                : ">=" + project.PythonVersion;

            string venv = FindVenv(project.ProjectDirectory)
                       ?? FindVenv(SolutionDirectoryOf(solution))
                       ?? EnvironmentVenv();

            if (!string.IsNullOrEmpty(venv))
            {
                info.VirtualEnvPath = venv;
                info.VirtualEnvName = Path.GetFileName(venv.TrimEnd(Path.DirectorySeparatorChar));
                string interpreter = VenvService.InterpreterPathIn(venv);
                if (!string.IsNullOrEmpty(interpreter) && File.Exists(interpreter))
                    info.InterpreterPath = interpreter;
            }

            return info;
        }

        private static string SolutionDirectoryOf(SolutionModel solution)
        {
            if (solution == null || string.IsNullOrEmpty(solution.FilePath)) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(solution.FilePath)); }
            catch { return null; }
        }

        private static string FindVenv(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return null;
            foreach (string name in VenvFolderNames)
            {
                string candidate;
                try { candidate = Path.Combine(directory, name); }
                catch { continue; }
                if (Directory.Exists(candidate) && VenvService.IsVenv(candidate)) return candidate;
            }
            return null;
        }

        // An activated shell exports VIRTUAL_ENV; when the IDE was launched from one, that is the
        // interpreter the user is actually working against.
        private static string EnvironmentVenv()
        {
            string fromEnv;
            try { fromEnv = System.Environment.GetEnvironmentVariable("VIRTUAL_ENV"); }
            catch { return null; }
            if (string.IsNullOrEmpty(fromEnv) || !Directory.Exists(fromEnv)) return null;
            return VenvService.IsVenv(fromEnv) ? fromEnv : null;
        }
    }
}
