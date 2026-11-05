using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Templates
{
    public enum PostGenActionType { Readme, Gitignore, License, GitInit, Venv, Restore }

    /// <summary>One requested post-generation action (SRS §8).</summary>
    public sealed class PostGenRequest
    {
        public PostGenActionType Type;
        public string ProjectName = "";
        public string Description = "";
        public string Author = "";
        public string Language = "";
        public string LicenseKind = "MIT";
        public int Year;                 // caller stamps (DateTime.Now.Year); kept as data so the runner is deterministic
    }

    /// <summary>The outcome of one action.</summary>
    public sealed class PostGenStep
    {
        public string Name = "";
        public bool Ok;
        public bool Skipped;             // e.g. required tool absent — not a hard failure
        public string Detail = "";
        public override string ToString() { return (Skipped ? "SKIP " : Ok ? "OK   " : "FAIL ") + Name + (string.IsNullOrEmpty(Detail) ? "" : " — " + Detail); }
    }

    public sealed class PostGenReport
    {
        public readonly List<PostGenStep> Steps = new List<PostGenStep>();
        public bool AnyFailed { get { foreach (var s in Steps) if (!s.Ok && !s.Skipped) return true; return false; } }
    }

    /// <summary>
    /// Runs post-generation actions after <see cref="ProjectGenerator"/> has written the project (SRS §8):
    /// emit README / .gitignore / LICENSE (native file writes) and, when requested, initialise a Git repo and
    /// create a virtual environment (shelling out to git / python — tool absence is a soft skip, never a
    /// throw). The file-emitting builders are pure/static so they're unit-testable without a shell.
    /// </summary>
    public static class PostGenRunner
    {
        public static PostGenReport Run(string projectRoot, IEnumerable<PostGenRequest> requests)
        {
            return Run(projectRoot, requests, null);
        }

        /// <summary><paramref name="onStep"/> (optional) fires BEFORE each action executes — hosts use
        /// it to drive progress UI (the venv/restore steps can run for many seconds).</summary>
        public static PostGenReport Run(string projectRoot, IEnumerable<PostGenRequest> requests,
                                        Action<PostGenRequest> onStep)
        {
            var rep = new PostGenReport();
            foreach (var r in requests)
            {
                if (onStep != null) onStep(r);
                try { rep.Steps.Add(Execute(projectRoot, r)); }
                catch (Exception ex) { rep.Steps.Add(new PostGenStep { Name = r.Type.ToString(), Ok = false, Detail = ex.Message }); }
            }
            return rep;
        }

        private static PostGenStep Execute(string root, PostGenRequest req)
        {
            switch (req.Type)
            {
                case PostGenActionType.Readme:
                    File.WriteAllText(Path.Combine(root, "README.md"), ReadmeText(req.ProjectName, req.Description));
                    return new PostGenStep { Name = "README.md", Ok = true };
                case PostGenActionType.Gitignore:
                    File.WriteAllText(Path.Combine(root, ".gitignore"), GitignoreText(req.Language));
                    return new PostGenStep { Name = ".gitignore", Ok = true };
                case PostGenActionType.License:
                    File.WriteAllText(Path.Combine(root, "LICENSE"), LicenseText(req.LicenseKind, req.Year, req.Author));
                    return new PostGenStep { Name = "LICENSE", Ok = true };
                case PostGenActionType.GitInit:
                    return Shell("git", "git", "init", root, "Git repository initialised");
                case PostGenActionType.Venv:
                    return Shell("python", "python", "-m venv .venv", root, "virtual environment created (.venv)");
                case PostGenActionType.Restore:
                    return Restore(root, req.Language);
                default:
                    return new PostGenStep { Name = req.Type.ToString(), Ok = false, Detail = "unknown action" };
            }
        }

        // ---- pure content builders (unit-tested) ----

        public static string ReadmeText(string name, string description)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(name).Append("\n\n");
            if (!string.IsNullOrEmpty(description)) sb.Append(description).Append("\n");
            return sb.ToString();
        }

        public static string GitignoreText(string language)
        {
            switch ((language ?? "").ToLowerInvariant())
            {
                case "python": return "__pycache__/\n*.py[cod]\n.venv/\nvenv/\n*.egg-info/\n.pytest_cache/\nbuild/\ndist/\n";
                case "rust": return "/target\n**/*.rs.bk\n";
                case "c#": return "bin/\nobj/\n*.user\n";
                case "cuda c":
                case "c++":
                case "c": return "build/\n*.o\n*.obj\n*.exe\n";
                case "julia": return "/Manifest.toml\n*.jl.cov\n*.jl.mem\n";
                default: return "build/\ndist/\n";
            }
        }

        public static string LicenseText(string kind, int year, string author)
        {
            string holder = string.IsNullOrEmpty(author) ? "The Authors" : author;
            // Default to MIT; other kinds fall through to MIT for now (the SRS lists LICENSE generation, not a full SPDX set).
            return
                "MIT License\n\n" +
                "Copyright (c) " + year + " " + holder + "\n\n" +
                "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
                "of this software and associated documentation files (the \"Software\"), to deal\n" +
                "in the Software without restriction, including without limitation the rights\n" +
                "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
                "copies of the Software, and to permit persons to whom the Software is\n" +
                "furnished to do so, subject to the following conditions:\n\n" +
                "The above copyright notice and this permission notice shall be included in all\n" +
                "copies or substantial portions of the Software.\n\n" +
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND.\n";
        }

        // ---- dependency restore (SRS §8) ----

        /// <summary>The (exe, args, tool, okDetail) restore command for a language, or null if none applies.
        /// Pure — exposed for testing the language→command mapping.</summary>
        public static string[] RestoreCommand(string language)
        {
            switch ((language ?? "").ToLowerInvariant())
            {
                case "python": return new[] { "pip", "install -r requirements.txt", "pip", "pip requirements installed" };
                default: return null;
            }
        }

        private static PostGenStep Restore(string root, string language)
        {
            string[] cmd = RestoreCommand(language);
            if (cmd == null) return new PostGenStep { Name = "restore", Skipped = true, Detail = "no dependency restore for '" + language + "'" };
            // Python pip is skipped unless a requirements.txt is actually present (avoid touching a global env otherwise).
            if (string.Equals(language, "python", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(root, "requirements.txt")))
                return new PostGenStep { Name = "restore", Skipped = true, Detail = "no requirements.txt to install" };
            return Shell("restore (" + cmd[0] + ")", cmd[0], cmd[1], root, cmd[3]);
        }

        // ---- shell helper (soft-skip on tool absence) ----

        private static PostGenStep Shell(string name, string exe, string args, string wd, string okDetail)
        {
            string tool = FindOnPath(exe + ".exe") ?? FindOnPath(exe) ?? FindOnPath(exe + ".cmd") ?? FindOnPath(exe + ".bat");
            if (tool == null) return new PostGenStep { Name = name, Skipped = true, Detail = exe + " not found (skipped)" };
            try
            {
                var psi = new ProcessStartInfo { FileName = tool, Arguments = args, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = wd };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return new PostGenStep { Name = name, Ok = false, Detail = "timed out" }; }
                    return p.ExitCode == 0
                        ? new PostGenStep { Name = name, Ok = true, Detail = okDetail }
                        : new PostGenStep { Name = name, Ok = false, Detail = (err ?? "").Trim() };
                }
            }
            catch (Exception ex) { return new PostGenStep { Name = name, Ok = false, Detail = ex.Message }; }
        }

        private static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string c; try { c = Path.Combine(dir.Trim(), exe); } catch { continue; }
                if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
