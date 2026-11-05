using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Scaffolds a new Python project on disk for one of the three supported
    /// templates (ClassLibrary / App / FlaskWebApp) and returns a hydrated
    /// <see cref="SolutionModel"/>.
    ///
    /// Always writes:
    ///   pyproject.toml (with [tool.helixstudio].kind),
    ///   &lt;name&gt;.sln (real multi-project format, references the .pyproj),
    ///   src/&lt;name&gt;/&lt;name&gt;.pyproj (PTVS format; opens in Visual Studio),
    ///   src/&lt;name&gt;/__init__.py, tests/__init__.py, tests/test_&lt;name&gt;.py,
    ///   README.md, .gitignore
    /// plus template-specific files (see <see cref="CreateNewProject"/> body).
    /// </summary>
    public static class PythonProjectScaffolder
    {
        public static SolutionModel CreateNewProject(string directory, string name, ProjectKind kind)
        {
            return CreateNewProject(directory, name, kind, SolutionFormat.Toml, GuiFramework.Tkinter);
        }

        public static SolutionModel CreateNewProject(string directory, string name, ProjectKind kind, SolutionFormat format)
        {
            return CreateNewProject(directory, name, kind, format, GuiFramework.Tkinter);
        }

        public static SolutionModel CreateNewProject(string directory, string name, ProjectKind kind, SolutionFormat format, GuiFramework guiFramework)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory cannot be empty.", "directory");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty.", "name");

            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            string sanitized = SanitizePackageName(name);
            string pyprojectPath = Path.Combine(directory, "pyproject.toml");
            string slnPath = Path.Combine(directory, sanitized + ".sln");
            string pkgDir = Path.Combine(directory, sanitized);

            Directory.CreateDirectory(pkgDir);

            // pyproject.toml — dependencies depend on kind (+ gui framework).
            List<string> deps = new List<string>();
            if (kind == ProjectKind.FlaskWebApp) deps.Add("flask>=3.0");
            else if (kind == ProjectKind.GuiApp)
            {
                string dep = GuiFrameworkDependency(guiFramework);
                if (!string.IsNullOrEmpty(dep)) deps.Add(dep);
            }
            GuiFramework? persistedGui = kind == ProjectKind.GuiApp ? (GuiFramework?)guiFramework : null;
            PyProjectWriter.WriteMinimal(pyprojectPath, sanitized, "3.12", kind, deps, persistedGui);

            WriteText(Path.Combine(directory, "README.md"), BuildReadme(name, kind));
            WriteText(Path.Combine(directory, ".gitignore"), BuildGitignore());

            // Package init — shared across templates.
            List<string> pyFiles = new List<string> { "__init__.py" };
            List<string> subDirs = new List<string>();
            WriteText(Path.Combine(pkgDir, "__init__.py"), "");

            // Template-specific files, tracked in pyFiles so the .pyproj's
            // ItemGroup lists them accurately.
            if (kind == ProjectKind.App)
            {
                WriteText(Path.Combine(directory, "main.py"),
                    "def main() -> int | None:\n" +
                    "    return 0\n\n\n" +
                    "if __name__ == \"__main__\":\n" +
                    "    raise SystemExit(main())\n");
                pyFiles.Add(Path.Combine("..", "main.py"));
            }
            else if (kind == ProjectKind.GuiApp)
            {
                WriteText(Path.Combine(directory, "main.py"), BuildGuiMainPy(name, guiFramework));
                pyFiles.Add(Path.Combine("..", "main.py"));
            }
            else if (kind == ProjectKind.FlaskWebApp)
            {
                string templatesDir = Path.Combine(pkgDir, "templates");
                Directory.CreateDirectory(templatesDir);
                WriteText(Path.Combine(pkgDir, "app.py"),
                    "from flask import Flask, render_template\n\napp = Flask(__name__)\n\n\n@app.route(\"/\")\ndef index() -> str:\n    return render_template(\"index.html\")\n");
                WriteText(Path.Combine(templatesDir, "index.html"),
                    "<!doctype html>\n<html lang=\"en\">\n  <head><meta charset=\"utf-8\"><title>" + name + "</title></head>\n  <body><h1>Hello, Flask</h1></body>\n</html>\n");
                pyFiles.Add("app.py");
                subDirs.Add("templates");
            }
            else // ClassLibrary
            {
                WriteText(Path.Combine(pkgDir, "core.py"),
                    "class Core:\n    \"\"\"Entry point of the " + name + " library.\"\"\"\n    pass\n");
                pyFiles.Add("core.py");
            }

            // No tests/ folder, and no placeholder test. Solution policy rule 3 (PY8003) bars
            // every project kind except a test project from containing test files, so scaffolding
            // `tests/test_<name>.py` into the new project would hand the user a solution that is
            // blocked from compiling, running and debugging the moment it is created. Tests belong
            // in a sibling test project (kind = "test", conventionally `<project>_tests`) — add one
            // from the "Python Unit Test Library (pytest)" template.

            // Build a PackageDescriptor and emit a real PTVS .pyproj + a real
            // multi-project .sln referencing it. The .sln is therefore openable
            // in Visual Studio with Python Tools installed; HelixStudio still
            // resolves the .sln to the adjacent pyproject.toml at load time.
            PackageDescriptor pkg = new PackageDescriptor(sanitized, pkgDir);
            pkg.PyFiles.AddRange(pyFiles);
            pkg.SubDirectories.AddRange(subDirs);
            pkg.PyProjFilePath = Path.Combine(pkgDir, sanitized + ".pyproj");
            pkg.ProjectGuid = Guid.NewGuid();
            PyProjectFileWriter.Write(pkg);
            VsSolutionWriter.Write(slnPath, new List<PackageDescriptor> { pkg });

            string primaryPath = format == SolutionFormat.Sln ? slnPath : pyprojectPath;
            SolutionModel solution = new SolutionModel(primaryPath, sanitized);
            // ProjectModel.FilePath stays on pyproject.toml because that's where
            // the project metadata actually lives; only the SolutionModel anchor
            // varies by the user's chosen format.
            ProjectModel project = new ProjectModel(pyprojectPath, sanitized, solution)
            {
                DefaultNamespace = sanitized,
                PythonVersion = "3.12",
                TargetFramework = "python3.12",
                Kind = kind,
            };
            solution.Projects.Add(project);
            return solution;
        }

        private static string SanitizePackageName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "pkg";
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '_' || c == '-') sb.Append('_');
            }
            if (sb.Length == 0) return "pkg";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static void WriteText(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static string BuildReadme(string name, ProjectKind kind)
        {
            string role;
            switch (kind)
            {
                case ProjectKind.App: role = "runnable application"; break;
                case ProjectKind.FlaskWebApp: role = "Flask web application"; break;
                default: role = "Python library"; break;
            }
            return "# " + name + "\n\nA " + role + " scaffolded by HelixPython.\n";
        }

        private static string BuildGitignore()
        {
            return "__pycache__/\n*.py[cod]\n*$py.class\n.venv/\nvenv/\nenv/\ndist/\nbuild/\n*.egg-info/\n.mypy_cache/\n.pytest_cache/\n.ruff_cache/\n.idea/\n.vscode/\n";
        }

        private static string GuiFrameworkDependency(GuiFramework framework)
        {
            switch (framework)
            {
                case GuiFramework.PySide6: return "pyside6>=6.6";
                case GuiFramework.PyQt6: return "pyqt6>=6.6";
                case GuiFramework.WxPython: return "wxpython>=4.2";
                case GuiFramework.Tkinter: return null; // stdlib — no pip dep
                default: return null;
            }
        }

        private static string BuildGuiMainPy(string displayName, GuiFramework framework)
        {
            string title = displayName ?? "App";
            switch (framework)
            {
                case GuiFramework.PySide6:
                    return
                        "import sys\n" +
                        "from PySide6.QtWidgets import QApplication, QLabel, QMainWindow\n\n\n" +
                        "def main() -> None:\n" +
                        "    app = QApplication(sys.argv)\n" +
                        "    window = QMainWindow()\n" +
                        "    window.setWindowTitle(\"" + title + "\")\n" +
                        "    window.setCentralWidget(QLabel(\"Hello, PySide6!\"))\n" +
                        "    window.show()\n" +
                        "    sys.exit(app.exec())\n\n\n" +
                        "if __name__ == \"__main__\":\n" +
                        "    raise SystemExit(main())\n";
                case GuiFramework.PyQt6:
                    return
                        "import sys\n" +
                        "from PyQt6.QtWidgets import QApplication, QLabel, QMainWindow\n\n\n" +
                        "def main() -> None:\n" +
                        "    app = QApplication(sys.argv)\n" +
                        "    window = QMainWindow()\n" +
                        "    window.setWindowTitle(\"" + title + "\")\n" +
                        "    window.setCentralWidget(QLabel(\"Hello, PyQt6!\"))\n" +
                        "    window.show()\n" +
                        "    sys.exit(app.exec())\n\n\n" +
                        "if __name__ == \"__main__\":\n" +
                        "    raise SystemExit(main())\n";
                case GuiFramework.WxPython:
                    return
                        "import wx\n\n\n" +
                        "def main() -> None:\n" +
                        "    app = wx.App()\n" +
                        "    frame = wx.Frame(None, title=\"" + title + "\", size=(360, 180))\n" +
                        "    panel = wx.Panel(frame)\n" +
                        "    wx.StaticText(panel, label=\"Hello, wxPython!\", pos=(24, 24))\n" +
                        "    frame.Show()\n" +
                        "    app.MainLoop()\n\n\n" +
                        "if __name__ == \"__main__\":\n" +
                        "    raise SystemExit(main())\n";
                case GuiFramework.Tkinter:
                default:
                    return
                        "import tkinter as tk\n" +
                        "from tkinter import ttk\n\n\n" +
                        "def main() -> None:\n" +
                        "    root = tk.Tk()\n" +
                        "    root.title(\"" + title + "\")\n" +
                        "    ttk.Label(root, text=\"Hello, Tkinter!\").pack(padx=24, pady=12)\n" +
                        "    ttk.Button(root, text=\"Close\", command=root.destroy).pack(padx=24, pady=12)\n" +
                        "    root.mainloop()\n\n\n" +
                        "if __name__ == \"__main__\":\n" +
                        "    raise SystemExit(main())\n";
            }
        }
    }
}
