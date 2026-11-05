namespace HelixPythonNamespace.AddIns.GuiDesigner
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.GuiDesigner.Model;

    /// <summary>
    /// Detects whether a parsed Python <see cref="TypeModel"/> is a GUI form (subclasses a known
    /// Tkinter / PyQt6 / PySide6 / wxPython base, directly or through an in-solution intermediate
    /// base) and bridges it to the GUI Designer Canvas's <see cref="GuiForm"/> model.
    ///
    /// The bridge only carries over what a TypeModel exposes today — class name and detected
    /// framework. TypeModel doesn't capture the class body's statement-level AST, so extracting the
    /// actual widgets a real <c>__init__</c> constructs isn't implemented; a detected form opens the
    /// designer scoped to the right class/framework with an empty canvas, not a parsed widget tree.
    /// </summary>
    public static class GuiFormBridge
    {
        private static readonly HashSet<string> TkinterBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tk", "tkinter.Tk",
            "Toplevel", "tkinter.Toplevel",
            "Frame", "tkinter.Frame", "ttk.Frame", "tkinter.ttk.Frame",
        };

        private static readonly HashSet<string> PyQt6Bases = new HashSet<string>(StringComparer.Ordinal)
        {
            "PyQt6.QtWidgets.QWidget", "PyQt6.QtWidgets.QMainWindow", "PyQt6.QtWidgets.QDialog",
        };

        private static readonly HashSet<string> PySide6Bases = new HashSet<string>(StringComparer.Ordinal)
        {
            "PySide6.QtWidgets.QWidget", "PySide6.QtWidgets.QMainWindow", "PySide6.QtWidgets.QDialog",
        };

        // Qt short names (QWidget/QMainWindow/QDialog) are shared by PyQt6 and PySide6 — a base
        // written by short name alone can't tell them apart, so it's treated as "some Qt flavor"
        // and defaults to PyQt6 (the combo box's own first Qt option) rather than guessing wrong.
        private static readonly HashSet<string> QtShortNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "QWidget", "QMainWindow", "QDialog",
        };

        private static readonly HashSet<string> WxBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "wx.Frame", "wx.Panel", "wx.Dialog",
        };

        /// <summary>
        /// Walks <paramref name="type"/>'s base-type chain — including up to a few hops through
        /// in-solution intermediate bases, so a form subclassing a local base class rather than the
        /// framework class directly is still recognized — looking for a known GUI-framework root.
        /// Returns null when nothing matches.
        /// </summary>
        public static GuiFramework? DetectFramework(TypeModel type)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            TypeModel current = type;

            for (int hop = 0; current != null && hop < 8; hop++)
            {
                if (!visited.Add(current.FullyQualifiedName)) break; // guard against inheritance cycles

                TypeModel next = null;
                if (current.BaseTypes != null)
                {
                    foreach (TypeReference baseRef in current.BaseTypes)
                    {
                        string fqn = baseRef.FullyQualifiedName;
                        string simple = baseRef.SimpleName;

                        if (TkinterBases.Contains(fqn) || TkinterBases.Contains(simple)) return GuiFramework.Tkinter;
                        if (PyQt6Bases.Contains(fqn)) return GuiFramework.PyQt6;
                        if (PySide6Bases.Contains(fqn)) return GuiFramework.PySide6;
                        if (WxBases.Contains(fqn) || WxBases.Contains(simple)) return GuiFramework.WxPython;
                        if (QtShortNames.Contains(simple)) return GuiFramework.PyQt6;

                        // Keep following the first in-solution base for the next hop.
                        if (next == null && !baseRef.IsExternal && baseRef.ResolvedType != null)
                        {
                            next = baseRef.ResolvedType;
                        }
                    }
                }
                current = next;
            }

            return null;
        }

        /// <summary>True when <paramref name="type"/> is recognizably a GUI form class.</summary>
        public static bool IsGuiFormType(TypeModel type)
        {
            return type != null && DetectFramework(type).HasValue;
        }

        /// <summary>
        /// Builds a minimal <see cref="GuiForm"/> for <paramref name="type"/> — correct class name
        /// and framework, empty control list (see the class-level remarks on why). Framework
        /// preference: the owning project's explicit <see cref="ProjectModel.GuiFramework"/> (set
        /// once at scaffold time and persisted in <c>pyproject.toml</c> — the authoritative choice)
        /// when the project is a <see cref="ProjectKind.GuiApp"/>, else whatever this class's own
        /// base-type chain implies, else Tkinter.
        /// </summary>
        public static GuiForm BuildFormFor(TypeModel type)
        {
            if (type == null) throw new ArgumentNullException("type");

            GuiFramework framework = FromProjectSetting(type.Project) ?? DetectFramework(type) ?? GuiFramework.Tkinter;
            return new GuiForm
            {
                FormClassName = type.Name,
                Title = type.Name,
                Width = 420,
                Height = 260,
                Framework = framework,
            };
        }

        private static GuiFramework? FromProjectSetting(ProjectModel project)
        {
            if (project == null
                || project.Kind != HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp
                || !project.GuiFramework.HasValue)
            {
                return null;
            }

            switch (project.GuiFramework.Value)
            {
                case HelixPythonNamespace.Core.Model.Enums.GuiFramework.Tkinter: return GuiFramework.Tkinter;
                case HelixPythonNamespace.Core.Model.Enums.GuiFramework.PyQt6: return GuiFramework.PyQt6;
                case HelixPythonNamespace.Core.Model.Enums.GuiFramework.PySide6: return GuiFramework.PySide6;
                case HelixPythonNamespace.Core.Model.Enums.GuiFramework.WxPython: return GuiFramework.WxPython;
                default: return null;
            }
        }
    }
}
