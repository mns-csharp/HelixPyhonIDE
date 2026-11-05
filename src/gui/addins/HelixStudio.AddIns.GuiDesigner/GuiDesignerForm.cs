namespace HelixPythonNamespace.AddIns.GuiDesigner
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Reflection;
    using System.Windows.Forms;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.GuiDesigner.Emit;
    using HelixPythonNamespace.GuiDesigner.Parse;
    using HelixPythonNamespace.GuiDesigner.Persistence;
    using HelixPythonNamespace.UI;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// FR-DES-02: WinForms surface for the (previously unwired) Python GUI form-builder engine
    /// (<see cref="HelixPythonNamespace.GuiDesigner"/>). A VS2022-style categorized/drag-enabled toolbox, a
    /// mouse-editable design canvas (move + 8-handle resize, like the real WinForms designer), and a
    /// <see cref="PropertyGrid"/> construct a <see cref="GuiForm"/> model visually. Every edit
    /// (move/resize/add/delete/property change) regenerates the real <see cref="EmitterFactory"/>
    /// emitter output (Tkinter / PyQt6 / PySide6 / wxPython) and — when opened for a real project file
    /// via "GUI Design View" — writes it straight back to that <c>.py</c> file plus a <c>.designer.json</c>
    /// sidecar, the same "the designer surface IS the source of truth, the file is always in sync"
    /// model VS2022 uses, instead of requiring a manual copy-paste. The emitter is round-trip-safe (it
    /// preserves <c># region UserCode-*</c> blocks) but otherwise fully regenerates the file, so a
    /// <c>.py</c> that wasn't already designer-generated is never auto-overwritten without the user
    /// explicitly taking it over first — see <see cref="IsSafeToAdopt"/>. Opened from Add-in ▸ GUI
    /// Designer Canvas and from the Solution Explorer's "GUI Design View" (<see cref="GuiDesignerAddIn"/>).
    /// </summary>
    public sealed partial class GuiDesignerForm : LunaForm, IToolboxCollapseState
    {
        // ----- Layout constants (design-surface geometry) -----
        private const int FormMargin = 24;      // gap between the canvas edge and the simulated form
        private const int TitleBarHeight = 22;  // simulated form's own caption bar
        private const int HandleSize = 7;       // VS2022-style 7x7 selection handle squares
        private const int MinControlSize = 12;
        private const int MinFormSize = 120;
        private const int TrayHeight = 34;
        private const int TabStripHeight = 22;  // TabControl (Notebook) tab-header band
        private const string CategorizedIcon = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAadEVYdFNvZnR3YXJlAFBhaW50Lk5FVCB2My41LjEwMPRyoQAAALBJREFUOE9joAowLt8tB8T+QBwFxSC2HFSaMDAq2xVw4vbbHS8//Dj16uOPMyA2SAwqTRho5GxIP33n7SZunbT/IHzo+uuVIDGQXEhIyH9cGKwZBFQz12Ydv/VmI8wAEBskBpUmDBRTVyYuP3x/27GbbzYDnb8NxAaJgeRmzpz5Hxkji4HYYCARs0BDPHp+FhBXQXEWSAwqTRlAth1mKzKbOgAU59C4H00HQzAdMDAAAKjc+SkMX9YxAAAAAElFTkSuQmCC";
        private const string AlphabeticIcon = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGXRFWHRTb2Z0d2FyZQBBZG9iZSBJbWFnZVJlYWR5ccllPAAAAd1JREFUeNqkU71rwlAQv8QoDrYOfmwuVTqkg2vBwdLuDq5uDtJVhGKdxMFgwT2uDv4BBR1LcXK3hBJKEQdBcDDo4Le9u5LwFIVCHxz38X6/e3eXi1QsFkGSJBCPy+UiiaF5j/K23W6/UA4w+/0eNE0DGU6fGBIeyuVygzT5Z3CgYKYr1LcoF3Zws9lAtVrVd7sdVCoVvVQqPWL4QeDNkNdD/Q2FQiEzGo0+kLQXZb1eO3J8R3jiURsKAi6DweANvfrXQ3jicQur1UqiUmlI3W4XJpMJKIoCqVTKITSbTdY+nw/S6TTbxOMEy+XSSZBIJPiy0WiAOPVMJgOmacJ4POY4fTXi0Z1MmShI0mq1QNd1mE6n7NdqNdZUVbvdhng8DjbWrkAWK+j3+5DNZp0E+XyedafTgWQyCV6vl33C2xUctBAKhfhVWZa55Gg0yi2Q3ev12K7X67xoToLFYuEkyOVyB9O250DbKsYITzxuAcudDQaDT7fbzdO3xe6VRIwTjvDEowRSJBK5RtAd2n7xdcMwXizLAr/fD6qqPh2tgoVtvA+HQ1NBsokTNcXb+XyuhsNhF342DfUzzqSNO2CIGI/H8/svnNo07NGgzQwEAuS+YunGua2UaJ//c34EGAAoZ1hbg/EeUAAAAABJRU5ErkJggg==";
        private const string EventsIcon = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGXRFWHRTb2Z0d2FyZQBBZG9iZSBJbWFnZVJlYWR5ccllPAAAAhlJREFUeNqkUj1oFEEUfjOzuR9373K63nkSYwIpRAsbT4wiGhQR8a600dLWwt9CiIVgY2cVsNLCMo0IFhKDIArR5FxEYmI4IwZhibns3m3iZn/Ht7c52Hi3aXww7Oy8+b753vce4ZzD/4QQ/Zl+QdtbkRB4uh3wSNm/1EHg++EXwWd291cGBg9ePco9E084ECEL2up3fXriejWd4re6KtgkoEhwoTBQGXIa1RDck4Omruozk7eDgwCstDE0SuD5rVUSdx4+JBB/l20b4PIe0NecBoI/BWBGudJW2qHA81A+QDnff37IbM4BZSloGH5zauLu52wGbjLGlWip3Qj2J9PycCa7t89qfgWWkGH+4723ud5W+kGkYeU4gkph38ggcIcIqSJw34bS6dGLgS4szfryYey9ZdbvRzEkOgcvnwhvzA3OrA1grsvpyXOXj1l/liGRLrg/F15N2ebKI8ZgHE2Gs1f8TgWYGGFoqyhCKScXxxjVYIeU5D++TSrrxsozSmC89R6J8YC3SDngpVJvLpNPJQ2ozdVmtbr6nFJ4zEnYqdhJNNa8sLcUjst5Ki8tLiz+WlJfo6qHlJKuM79lDly3tfooEw6s/taM+Vn1HRo7iiknbqS3ENg23nRgmHOSUKrLM9jvO4Gwf0EZaZtBwjiha04dDb2BezUgXQ8dAkkiUNwDIIkxBG5IcArB17DuGtl0uxuwHX8FGAA0+tliSE7bRgAAAABJRU5ErkJggg==";
        private const string PropertiesIcon = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAadEVYdFNvZnR3YXJlAFBhaW50Lk5FVCB2My41LjEwMPRyoQAAAYJJREFUOE/F07FLAmEYBvBvjf6AkLZoiaChoCFKAoeIIIRAECKKyCmEoBpqKIhwailCLEGKghAjcgs1ULqI+ioyCTEKEjlJMZU78rq0J1/JcD0beuHhueH98X3fcOzP0+9KGcrxlsM1hoyB6Z1J79y5mreFv7B6U8LKpYolQcFi8B2zARnTPgnjfgnGUxn6UAHtFx/QXX9Cd6bkybJeR4JbvG+YOs5g8iiNicNXjLlFjB4kYN5/gWn3GSOuRxidUQxvPWDIfo/BzTsMbNyiaT7I6QkcdQ5Z1rct8lKphGKxqClkyLIee5yrqgpFUdDlbqm0z+cD5xyRSASxWAyCIMDv9/827ZAhy7rXn3ihUIAsy5pChizrXItySZKQzWYRmrFWunpaNbWnU9MOGbKswxbmuVwO6XRaU8iQZW3LVzyTyUAURVgcqHQgEIDH46l07Xf1FrRDhixrXRB4KpVCPB7XFDJkWbP1hNOVksmkppAhyxrNe44G0w6vJ2R/fql/G8a+ATGom6JnuJmnAAAAAElFTkSuQmCC";

        private readonly GuiForm _form;
        private readonly DesignerCanvasControl _canvas;
        private readonly ToolboxPalette _toolbox;

        /// <summary>The folded toolbox categories, forwarded to the shared palette.</summary>
        public System.Collections.Generic.IList<string> CollapsedToolboxCategories
        {
            get { return _toolbox.CollapsedToolboxCategories; }
            set { _toolbox.CollapsedToolboxCategories = value; }
        }

        /// <summary>Raised when the user folds or unfolds a toolbox category.</summary>
        public event System.EventHandler ToolboxCollapseChanged
        {
            add { _toolbox.ToolboxCollapseChanged += value; }
            remove { _toolbox.ToolboxCollapseChanged -= value; }
        }
        /// <summary>Owns the 16 px artwork. The palette draws the images; the list keeps them
        /// alive and is the one place they are decoded.</summary>
        private ImageList _toolboxImages;
        private readonly TextBox _toolboxSearch;
        private readonly ComboBox _frameworkCombo;
        private readonly TextBox _codeBox;
        private readonly Label _codeLabel;
        private readonly PropertyGrid _propertyGrid;
        private Panel _toolboxPanel;
        private Panel _propertiesPanel;
        private ToolStripButton _toolboxToggle;
        private ToolStripButton _propertiesToggle;

        /// <summary>The commands, as icons. See <see cref="DesignCommandStrip"/>.</summary>
        private DesignCommandStrip _commands;

        /// <summary>The bar the strip and the target combo sit on — docked Top, where this
        /// designer's own menu bar used to be.</summary>
        private Panel _designerBar;

        private ToolStripButton _undoButton;
        private ToolStripButton _redoButton;

        /// <summary>Where the command strip starts: immediately after the target combo.</summary>
        private const int CommandBarLeft = 246;
        private readonly ListView _eventsList;
        private readonly ComboBox _objectSelector;
        private readonly Dictionary<GuiControl, Rectangle> _trayRects = new Dictionary<GuiControl, Rectangle>();
        private readonly Dictionary<string, int> _activeTabByNotebookId = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, Rectangle> _tabHeaderRects = new Dictionary<string, Rectangle>(StringComparer.Ordinal);
        private readonly GuiFormRepository _repository = new GuiFormRepository();
        private readonly GuiFormHistory _history = new GuiFormHistory();
        private bool _propertyTransactionPending;
        private bool _refreshingObjectSelector;
        private GuiControl _selected;

        // ----- Live file sync (VS2022-style: the designer surface IS the source of truth) -----
        private readonly string _pySourcePath;   // null = standalone/preview-only (e.g. the Tools-menu sample form)
        private readonly string _designerJsonPath;
        private bool _adopted;                   // true once it's safe to overwrite _pySourcePath on every change
        private bool _adoptDeclined;             // the user was asked to take the file over and said no
        private Panel _syncBar;
        private Label _syncStatusLabel;
        private ToolStripButton _startSyncingButton;

        // ----- Drag/resize interaction state -----
        private enum DragKind { None, MoveControl, ResizeControl, ResizeForm }
        private enum HandlePos { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

        private static readonly HandlePos[] AllHandles =
        {
            HandlePos.TopLeft, HandlePos.Top, HandlePos.TopRight, HandlePos.Right,
            HandlePos.BottomRight, HandlePos.Bottom, HandlePos.BottomLeft, HandlePos.Left,
        };
        // The form has no draggable X/Y origin (it's anchored at FormMargin), so only the handles
        // that grow it rightward/downward make sense.
        private static readonly HandlePos[] FormHandles = { HandlePos.Right, HandlePos.Bottom, HandlePos.BottomRight };

        private DragKind _dragKind = DragKind.None;
        private HandlePos _dragHandle = HandlePos.None;
        private Point _dragStartMouse;
        private Rectangle _dragStartBounds;
        private Size _dragStartFormSize;

        private const string ToolboxDragFormat = "HelixPythonNamespace.GuiToolboxDrag.v1";

        public GuiDesignerForm() : this(BuildSampleForm(), null)
        {
        }

        /// <summary>Opens the designer pre-loaded with <paramref name="initialForm"/>, preview-only
        /// (no linked file) — the Tools-menu sample-form entry point.</summary>
        public GuiDesignerForm(GuiForm initialForm) : this(initialForm, null)
        {
        }

        /// <summary>Opens the designer pre-loaded with <paramref name="initialForm"/> and, when
        /// <paramref name="pySourcePath"/> is given, keeps that <c>.py</c> file (and a
        /// <c>.designer.json</c> sidecar) continuously in sync with every edit — used when a real
        /// GUI-form class is opened via Solution Explorer's "GUI Design View" (see
        /// <see cref="GuiFormBridge"/>). If a sidecar from a previous designer session already exists
        /// for that file, it is loaded in place of <paramref name="initialForm"/> so re-opening the
        /// designer resumes the real prior layout instead of an empty canvas.</summary>
        public GuiDesignerForm(GuiForm initialForm, string pySourcePath)
        {
            _pySourcePath = string.IsNullOrEmpty(pySourcePath) ? null : pySourcePath;
            _designerJsonPath = _pySourcePath != null ? GuiFormRepository.DesignerJsonFor(_pySourcePath) : null;

            // The .designer.json sidecar is the persisted source of truth (the same role a WinForms
            // .resx plays) — checked FIRST, ahead of everything else, so an ongoing edit/save loop
            // never depends on re-reading generated code back in. Only when NO sidecar exists yet
            // (this file has never been through the designer) does construction fall back to a
            // best-effort, one-time IMPORT straight from the real .py — see TkinterLayoutImporter —
            // so opening the designer for the first time on an already-hand-written form shows its
            // real layout instead of an empty canvas wherever that's actually recoverable. A file the
            // importer can't make sense of (or doesn't exist yet) falls through to the bridge's
            // empty-canvas stub, same as before.
            GuiForm loaded = _designerJsonPath != null ? _repository.Load(_designerJsonPath) : null;
            bool sidecarExisted = loaded != null;
            if (loaded == null && _pySourcePath != null && File.Exists(_pySourcePath))
            {
                string diskContent = null;
                try { diskContent = File.ReadAllText(_pySourcePath); } catch { }
                if (!string.IsNullOrWhiteSpace(diskContent))
                {
                    GuiFramework fw = initialForm != null ? initialForm.Framework : GuiFramework.Tkinter;
                    if (fw == GuiFramework.Tkinter)
                        loaded = TkinterLayoutImporter.TryImport(diskContent, initialForm != null ? initialForm.FormClassName : null);
                }
            }

            _form = loaded ?? initialForm ?? BuildSampleForm();
            // Safe to start auto-writing immediately when: there's nothing to overwrite (no linked
            // file), a prior designer session already owns this file (the sidecar existed — NOT just
            // "the importer managed to guess something," which is a best-effort seed, not a safety
            // signal), or the file on disk is new/empty/already designer-generated. Otherwise it's
            // real, hand-written code and must not be silently clobbered even though its layout could
            // be imported — see IsSafeToAdopt and the adoption bar built below.
            _adopted = _pySourcePath == null || sidecarExisted || IsSafeToAdopt(GeneratedSourcePath(_pySourcePath, _form.Framework));

            // These designer windows are plain Forms, so without this they fall back to the
            // WinForms default (Microsoft Sans Serif) instead of the shell UI font.
            Font = LunaFonts.Ui;
            Text = "GUI Designer Canvas — " + _form.FormClassName;
            Icon = DesignerIcon() ?? Icon;      // its own mark, not the shell's
            Width = 1200;
            Height = 760;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            KeyDown += OnFormKeyDown;

            // Top bar: the target framework, then the commands as ICONS on the shared design strip
            // — the same strip and geometry used by the other design surfaces.
            //
            // What this replaced: a full File / Edit / View / Format / Help MENU BAR of this
            // designer's own, inside a tab of an IDE that already has a menu bar, with a "Target:"
            // combo and a 92px "Regenerate" push button on a bar that was never shown at all
            // (`new Panel { Visible = false }`) — so the ONLY way to reach any of it was the menu.
            // A menu hides every command behind a click on a word, which is the wrong trade on a
            // surface whose whole point is that the work is visible, and a second menu bar two rows
            // under the first invites the user to look in the wrong one.
            //
            // One command was dropped rather than moved: Format ▸ Target Framework, which set the
            // very combo now sitting beside it. Two controls for one setting is one too many.
            var top = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = LunaColors.ToolbarBackground };
            var lbl = new Label { Text = "Target:", AutoSize = true, Location = new Point(8, 9) };
            _frameworkCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(58, 6),
                Width = 180,
            };
            foreach (GuiFramework framework in EmitterFactory.AllFrameworks())
                _frameworkCombo.Items.Add(EmitterFactory.EmitterFor(framework).FrameworkName);
            _frameworkCombo.SelectedIndex = ComboIndexForFramework(_form.Framework);
            _frameworkCombo.SelectedIndexChanged += delegate { BuildToolboxItems(_toolbox); GenerateCode(true); RefreshPropertyGrid(); };
            top.Controls.Add(lbl);
            top.Controls.Add(_frameworkCombo);

            _commands = new DesignCommandStrip(CommandBarLeft, 3);
            // New first, closest to the combo.
            _commands.Add("New Form", DesignGlyph.NewDesign(), delegate { NewFormCommand(); });
            _commands.AddSeparator();
            _undoButton = _commands.Add("Undo (Ctrl+Z)",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Undo, DesignGlyph.Undo(true)),
                delegate { if (_history.Undo(_form)) AfterHistoryNavigation(); });
            _redoButton = _commands.Add("Redo (Ctrl+Y)",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Redo, DesignGlyph.Undo(false)),
                delegate { if (_history.Redo(_form)) AfterHistoryNavigation(); });
            _commands.Add("Delete the selected control (Del)",
                DesignCommandStrip.SharedIcon(ToolbarIcons.DeadCode, DesignGlyph.Undo(true)),
                delegate { OnFormKeyDown(this, new KeyEventArgs(Keys.Delete)); });
            _commands.AddSeparator();
            _commands.Add("Regenerate the source from this form",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Compile, DesignGlyph.Save()),
                delegate { GenerateCode(true); });
            // Offered only for a real file this session is not already writing to — the same rule
            // the menu item had, asked of the button's Visible rather than of DropDownOpening.
            _startSyncingButton = _commands.Add(
                "Start syncing to this file… — write every edit straight to the source",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Link, DesignGlyph.Save()),
                delegate { if (AskToAdopt()) { UpdateSyncStatusUi(); GenerateCode(); } });
            _commands.Add("Show the generated file in Explorer",
                DesignCommandStrip.SharedIcon(ToolbarIcons.OpenFolder, DesignGlyph.Open()),
                delegate { ShowGeneratedFileInExplorer(); });
            _commands.AddSeparator();
            _commands.Add("Save Design As… — the form and the source generated from it",
                DesignGlyph.Save(),
                delegate { SaveDesignAsCommand(); });
            _commands.Add("Open Design…",
                DesignGlyph.Open(),
                delegate { OpenDesignCommand(); });
            _commands.AddSeparator();
            _commands.AddToggle("Snap to grid",
                DesignCommandStrip.SharedIcon(ToolbarIcons.ZoomFit, DesignGlyph.Group(true)),
                _form.Viewport.SnapToGrid, delegate(object sender, EventArgs e)
                {
                    _form.Viewport.SnapToGrid = ((ToolStripButton)sender).Checked;
                    _canvas.Invalidate();
                });
            _commands.AddSeparator();
            // The way back for a tool window closed with its ✕ — a pressed button rather than a
            // ticked menu entry, which is the only thing the tick was doing. They come and go with
            // the panels themselves: a tool window the shell has taken over is reopened from the
            // shell's View ▸ Panels, and two switches for one window that can disagree with each
            // other is worse than one switch in the right place.
            _toolboxToggle = _commands.AddToggle("Toolbox",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Designer, DesignGlyph.Group(true)), true,
                delegate { SetToolWindowVisible(_toolboxPanel, _toolboxToggle.Checked); });
            _propertiesToggle = _commands.AddToggle("Properties",
                DesignCommandStrip.SharedIcon(ToolbarIcons.BuildSettings, DesignGlyph.Group(false)), true,
                delegate { SetToolWindowVisible(_propertiesPanel, _propertiesToggle.Checked); });
            _commands.AddSeparator();
            _commands.Add("Designer help",
                DesignCommandStrip.SharedIcon(ToolbarIcons.Welcome, DesignGlyph.Open()),
                delegate { ShowDesignerHelp(); });
            top.Controls.Add(_commands);
            _commands.BringToFront();
            _designerBar = top;

            // Sync-status bar: one line saying where (and whether) this session is auto-saving.
            //
            // It used to carry a "Take Over & Start Syncing" button as well. The confirmation that
            // button raised is the thing that matters and it has not gone anywhere — it is now
            // asked at the moment it is actually needed, the first edit that would be written to
            // the file, and offered again from File - "Start Syncing to this file" for anyone who
            // said no. A button sitting on the canvas asks the user to take a decision about a file
            // before they have done anything to it, which is both the wrong moment to ask and one
            // stray click away from the overwrite it exists to prevent.
            _syncBar = new Panel { Dock = DockStyle.Top, Height = 24 };
            _syncStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
            _syncBar.Controls.Add(_syncStatusLabel);
            _syncBar.Visible = _adopted && _pySourcePath != null;   // UpdateSyncStatusUi owns this from here on

            // Left toolbox: VS2022-style categorized, icon-bearing list. Drag onto the canvas (like
            // dragging from the real Toolbox), or double-click to drop it at the next free spot.
            // The shared palette rather than a ListView with groups. The OS draws a ListView's
            // group headers and will not be told otherwise, which is why this toolbox's categories
            // ToolboxStyle keeps the categories consistent with the IDE.
            _toolboxImages = BuildToolboxImages();
            _toolbox = new ToolboxPalette { Dock = DockStyle.Fill };
            BuildToolboxItems(_toolbox);
            _toolbox.ItemActivated += delegate (object s2, ToolboxItemEventArgs e2)
            {
                if (e2.Tag is GuiControlKind) AddControl((GuiControlKind)e2.Tag, null);
            };
            _toolbox.ItemDragStarted += delegate (object s2, ToolboxItemEventArgs e2)
            {
                if (!(e2.Tag is GuiControlKind)) return;
                var kind = (GuiControlKind)e2.Tag;
                _toolbox.DoDragDrop(
                    new DataObject(ToolboxDragFormat, GuiControlKinds.ToToken(kind)),
                    DragDropEffects.Copy);
            };
            _toolboxSearch = new TextBox { Dock = DockStyle.Top, Height = 24, Text = "Search Toolbox" };
            _toolboxSearch.GotFocus += delegate { if (_toolboxSearch.Text == "Search Toolbox") _toolboxSearch.Text = string.Empty; };
            _toolboxSearch.LostFocus += delegate { if (string.IsNullOrWhiteSpace(_toolboxSearch.Text)) _toolboxSearch.Text = "Search Toolbox"; };
            _toolboxSearch.TextChanged += delegate { if (_toolboxSearch.Focused) BuildToolboxItems(_toolbox); };
            var toolboxPanel = new Panel { Dock = DockStyle.Left, Width = 205, BackColor = Color.FromArgb(246, 246, 246), Padding = new Padding(1) };
            _toolboxPanel = toolboxPanel;
            toolboxPanel.Controls.Add(_toolbox);
            toolboxPanel.Controls.Add(_toolboxSearch);
            toolboxPanel.Controls.Add(ToolWindowCaption("Toolbox", toolboxPanel));

            // Center canvas: custom-drawn design surface with move/resize + drop-target support.
            _canvas = new DesignerCanvasControl { Dock = DockStyle.Fill, AllowDrop = true };
            _canvas.Paint += OnCanvasPaint;
            _canvas.MouseDown += OnCanvasMouseDown;
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseUp += OnCanvasMouseUp;
            _canvas.DragEnter += OnCanvasDragEnter;
            _canvas.DragDrop += OnCanvasDragDrop;

            // Right column: Properties grid (VS2022's other signature designer affordance) stacked
            // above the live-generated Python preview.
            _propertyGrid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, PropertySort = PropertySort.Categorized };
            _eventsList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            _eventsList.Columns.Add("Event", 120); _eventsList.Columns.Add("Handler", 145);
            _eventsList.DoubleClick += OnEventDoubleClick;
            _objectSelector = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Height = 25 };
            _objectSelector.SelectedIndexChanged += OnObjectSelectorChanged;
            var inspectorHost = new Panel { Dock = DockStyle.Fill };
            inspectorHost.Controls.Add(_propertyGrid);
            inspectorHost.Controls.Add(_eventsList);
            _eventsList.Visible = false;
            var propertyToolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Height = 25 };
            var categorizedButton = new ToolStripButton { Image = PropertyIcon(CategorizedIcon), ToolTipText = "Categorized", Checked = true, CheckOnClick = true };
            var alphabeticButton = new ToolStripButton { Image = PropertyIcon(AlphabeticIcon), ToolTipText = "Alphabetical", CheckOnClick = true };
            var propertiesButton = new ToolStripButton { Image = PropertyIcon(PropertiesIcon), ToolTipText = "Properties", Checked = true, CheckOnClick = true };
            var eventsButton = new ToolStripButton { Image = PropertyIcon(EventsIcon), ToolTipText = "Events", CheckOnClick = true };
            categorizedButton.Click += delegate { _propertyGrid.PropertySort = PropertySort.Categorized; categorizedButton.Checked = true; alphabeticButton.Checked = false; };
            alphabeticButton.Click += delegate { _propertyGrid.PropertySort = PropertySort.Alphabetical; alphabeticButton.Checked = true; categorizedButton.Checked = false; };
            propertiesButton.Click += delegate { _propertyGrid.Visible = true; _eventsList.Visible = false; propertiesButton.Checked = true; eventsButton.Checked = false; };
            eventsButton.Click += delegate { _eventsList.Visible = true; _propertyGrid.Visible = false; eventsButton.Checked = true; propertiesButton.Checked = false; };
            propertyToolbar.Items.AddRange(new ToolStripItem[] { categorizedButton, alphabeticButton, new ToolStripSeparator(), propertiesButton, eventsButton });

            _codeBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
            };
            var codePanel = new Panel { Visible = false };
            _codeLabel = new Label { Text = "Generated source", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            codePanel.Controls.Add(_codeLabel);

            var rightPanel = new Panel { Dock = DockStyle.Right, Width = 310, BackColor = Color.FromArgb(246, 246, 246), Padding = new Padding(1) };
            rightPanel.Controls.Add(inspectorHost);
            rightPanel.Controls.Add(propertyToolbar);
            rightPanel.Controls.Add(_objectSelector);
            _propertiesPanel = rightPanel;
            rightPanel.Controls.Add(ToolWindowCaption("Properties", rightPanel));

            var documentCaption = new Label
            {
                Text = Path.GetFileName(_pySourcePath ?? _form.FormClassName) + " [Design]",
                Dock = DockStyle.Top, Height = 26, Padding = new Padding(9, 5, 0, 0),
                BackColor = Color.White, ForeColor = Color.FromArgb(30, 30, 30),
                Font = new Font(Font, FontStyle.Bold),
            };
            var centerPanel = new Panel { Dock = DockStyle.Fill };
            centerPanel.Controls.Add(_canvas);
            centerPanel.Controls.Add(documentCaption);

            // Wired only now: the panels the toggles switch do not exist until this far down.
            _toolWindowButton[_toolboxPanel] = _toolboxToggle;
            _toolWindowButton[_propertiesPanel] = _propertiesToggle;

            Controls.Add(centerPanel);
            Controls.Add(rightPanel);
            Controls.Add(toolboxPanel);
            Controls.Add(_syncBar);
            Controls.Add(_designerBar);

            RefreshPropertyGrid();
            UpdateSyncStatusUi();
            UpdateHistoryButtons();
            GenerateCode();
        }

        /// <summary>
        /// The quill this designer is marked with, embedded in this assembly.
        ///
        /// <para>Found by SEARCHING the manifest for the file name rather than naming a resource
        /// string: the assembly is called HelixStudio.App while its code says HelixPythonNamespace,
        /// and a literal that spells one of those has already gone quietly null once here (see
        /// <see cref="LunaForm.GetAppIcon"/>) — a missing icon never throws, it just leaves the
        /// default glyph behind and says nothing. Loaded once; the .ico carries frames from 16 to
        /// 256 px so a caption or a task-switcher picks the one it wants rather than squeezing a
        /// 256 down.</para>
        /// </summary>
        private static Icon DesignerIcon()
        {
            if (_designerIcon != null) return _designerIcon;
            try
            {
                Assembly asm = typeof(GuiDesignerForm).Assembly;
                foreach (string candidate in asm.GetManifestResourceNames())
                {
                    if (!candidate.EndsWith("gui_designer.ico", StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream stream = asm.GetManifestResourceStream(candidate))
                        if (stream != null) _designerIcon = new Icon(stream);
                    break;
                }
            }
            catch { /* a window with the default icon beats a window that would not open */ }
            return _designerIcon;
        }

        private static Icon _designerIcon;

        /// <summary>
        /// The form a designer opens on for <paramref name="sourcePath"/>: named after the file, in
        /// the binding its extension implies (C++ Win32 for a C++ source, Tkinter otherwise). Null
        /// path: the sample form. The add-in opens the result as a document tab
        /// (<c>GuiDesignerAddIn.OpenGuiDesignerDocument</c>); there is no floating designer window
        /// any more (spec 004, Task B ruling).
        /// </summary>
        internal static GuiForm FormForSource(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return BuildSampleForm();
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            GuiFramework framework = GuiFramework.Tkinter;
            if (ext == ".cpp" || ext == ".cc" || ext == ".cxx" || ext == ".h" || ext == ".hpp") framework = GuiFramework.CppWin32;
            return new GuiForm { FormClassName = EmitterCommon.SafeMember(Path.GetFileNameWithoutExtension(sourcePath)), Title = Path.GetFileNameWithoutExtension(sourcePath), Framework = framework };
        }

        internal static GuiForm BuildSampleForm()
        {
            var f = new GuiForm { FormClassName = "LoginForm", Title = "Login", Width = 420, Height = 260, Framework = GuiFramework.Tkinter };
            f.Controls.Add(new GuiControl { Id = "l1", Kind = GuiControlKind.Label, Name = "userLabel", X = 30, Y = 30, Width = 90, Height = 24, Props = { ["Text"] = "Username" } });
            f.Controls.Add(new GuiControl { Id = "t1", Kind = GuiControlKind.TextBox, Name = "userText", X = 140, Y = 28, Width = 220, Height = 26 });
            f.Controls.Add(new GuiControl { Id = "l2", Kind = GuiControlKind.Label, Name = "passLabel", X = 30, Y = 74, Width = 90, Height = 24, Props = { ["Text"] = "Password" } });
            f.Controls.Add(new GuiControl { Id = "t2", Kind = GuiControlKind.TextBox, Name = "passText", X = 140, Y = 72, Width = 220, Height = 26 });
            f.Controls.Add(new GuiControl { Id = "c1", Kind = GuiControlKind.CheckBox, Name = "rememberCheck", X = 140, Y = 112, Width = 160, Height = 24, Props = { ["Text"] = "Remember me" } });
            f.Controls.Add(new GuiControl { Id = "b1", Kind = GuiControlKind.Button, Name = "loginButton", X = 140, Y = 156, Width = 100, Height = 30, Props = { ["Text"] = "Log In" }, Events = { new GuiEvent { EventName = "Click", HandlerName = "on_login_click" } } });
            return f;
        }

        // ================= Toolbox: categories, icons, drag source =================

        /// <summary>
        /// A tool-window title bar for the designer's own Toolbox and Properties.
        ///
        /// The same bar the docked panels wear — same height, same colours, same Marlett ✕ in the
        /// same place — because these sit on screen beside them, and chrome that is nearly the same
        /// reads worse than chrome that is plainly different. It is a shared control rather than a
        /// copy so the two cannot drift apart, and so the Luna auto-styler leaves it alone.
        ///
        /// The ✕ hides <paramref name="owner"/> rather than disposing it: the panel keeps its
        /// search text, its scroll position and its selection, and View ▸ brings it back exactly as
        /// it was. A tool window that closes and cannot reopen is a trap, so the menu entry is
        /// built alongside this rather than left to be remembered later.
        /// </summary>
        private ToolWindowHeader ToolWindowCaption(string text, Panel owner)
        {
            var header = new ToolWindowHeader(text, new Font(Font, FontStyle.Bold));
            header.CloseClicked += delegate { SetToolWindowVisible(owner, false); };
            return header;
        }

        private readonly System.Collections.Generic.Dictionary<Panel, ToolStripButton> _toolWindowButton =
            new System.Collections.Generic.Dictionary<Panel, ToolStripButton>();

        /// <summary>
        /// Hands the Toolbox out so the host can dock it beside the Solution Explorer.
        ///
        /// The panel is REMOVED, not hidden: a copy in two places would drift, and the host is
        /// taking ownership of where it lives. Its View entry goes with it, because the host's
        /// View ▸ Panels is what reopens it once it is over there — leaving both would give the
        /// same tool window two switches that disagree.
        /// </summary>
        internal Panel DetachToolbox() { return DetachToolWindow(ref _toolboxPanel, _toolboxToggle); }

        /// <summary>Hands the Properties pane out. See <see cref="DetachToolbox"/>.</summary>
        internal Panel DetachProperties() { return DetachToolWindow(ref _propertiesPanel, _propertiesToggle); }

        private Panel DetachToolWindow(ref Panel panel, ToolStripButton toggle)
        {
            Panel detached = panel;
            if (detached == null) return null;
            panel = null;

            // The title bar stays with the designer: over in the dock, the DockablePanel's own bar
            // is the one that names and closes it, and two stacked bars is one too many.
            foreach (Control child in detached.Controls)
            {
                var header = child as ToolWindowHeader;
                if (header == null) continue;
                detached.Controls.Remove(header);
                header.Dispose();
                break;
            }

            detached.Parent?.Controls.Remove(detached);
            _toolWindowButton.Remove(detached);
            if (toggle != null && _commands != null) _commands.Items.Remove(toggle);
            return detached;
        }

        /// <summary>Takes the Toolbox back when the host is done with it.</summary>
        internal void ReattachToolbox(Panel toolbox) { ReattachToolWindow(toolbox, "Toolbox", ref _toolboxPanel, ref _toolboxToggle, DockStyle.Left, 205); }

        /// <summary>Takes the Properties pane back. See <see cref="ReattachToolbox"/>.</summary>
        internal void ReattachProperties(Panel properties) { ReattachToolWindow(properties, "Properties", ref _propertiesPanel, ref _propertiesToggle, DockStyle.Right, 310); }

        private void ReattachToolWindow(Panel panel, string title, ref Panel field,
                                        ref ToolStripButton toggle, DockStyle side, int width)
        {
            if (panel == null || IsDisposed) return;
            field = panel;
            panel.Dock = side;
            panel.Width = width;
            panel.Visible = true;
            panel.Controls.Add(ToolWindowCaption(title, panel));

            toggle = new ToolStripButton
            {
                Image = title == "Toolbox"
                    ? DesignCommandStrip.SharedIcon(ToolbarIcons.Designer, DesignGlyph.Group(true))
                    : DesignCommandStrip.SharedIcon(ToolbarIcons.BuildSettings, DesignGlyph.Group(false)),
                ToolTipText = title,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoSize = false,
                Size = new Size(26, 26),
                CheckOnClick = true,
                Checked = true,
            };
            ToolStripButton entry = toggle;
            Panel owner = panel;
            entry.Click += delegate { SetToolWindowVisible(owner, entry.Checked); };
            if (_commands != null) _commands.Items.Add(entry);
            _toolWindowButton[panel] = entry;

            Controls.Add(panel);
            panel.BringToFront();
        }

        /// <summary>Shows or hides a designer tool window and keeps its toggle in step.</summary>
        private void SetToolWindowVisible(Panel panel, bool visible)
        {
            if (panel == null) return;
            panel.Visible = visible;
            ToolStripButton item;
            if (_toolWindowButton.TryGetValue(panel, out item)) item.Checked = visible;
        }

        private sealed class ToolboxEntry
        {
            public readonly string Category;
            public readonly GuiControlKind Kind;
            public ToolboxEntry(string category, GuiControlKind kind) { Category = category; Kind = kind; }
        }

        // Mirrors VS2022's default Toolbox groupings (a curated subset of the full ~55-member
        // GuiControlKind taxonomy — printing/rare-component kinds are omitted to keep this focused,
        // the way VS2022's "General" tab doesn't show every possible component either).
        private static readonly ToolboxEntry[] ToolboxItems =
        {
            new ToolboxEntry("Common Controls", GuiControlKind.Button),
            new ToolboxEntry("Common Controls", GuiControlKind.Label),
            new ToolboxEntry("Common Controls", GuiControlKind.LinkLabel),
            new ToolboxEntry("Common Controls", GuiControlKind.TextBox),
            new ToolboxEntry("Common Controls", GuiControlKind.RichTextBox),
            new ToolboxEntry("Common Controls", GuiControlKind.MaskedTextBox),
            new ToolboxEntry("Common Controls", GuiControlKind.CheckBox),
            new ToolboxEntry("Common Controls", GuiControlKind.CheckedListBox),
            new ToolboxEntry("Common Controls", GuiControlKind.RadioButton),
            new ToolboxEntry("Common Controls", GuiControlKind.ComboBox),
            new ToolboxEntry("Common Controls", GuiControlKind.ListBox),
            new ToolboxEntry("Common Controls", GuiControlKind.ListView),
            new ToolboxEntry("Common Controls", GuiControlKind.TreeView),
            new ToolboxEntry("Common Controls", GuiControlKind.PictureBox),
            new ToolboxEntry("Common Controls", GuiControlKind.ProgressBar),
            new ToolboxEntry("Common Controls", GuiControlKind.TrackBar),
            new ToolboxEntry("Common Controls", GuiControlKind.NumericUpDown),
            new ToolboxEntry("Common Controls", GuiControlKind.DomainUpDown),
            new ToolboxEntry("Common Controls", GuiControlKind.DateTimePicker),
            new ToolboxEntry("Common Controls", GuiControlKind.MonthCalendar),
            new ToolboxEntry("Common Controls", GuiControlKind.WebBrowser),
            new ToolboxEntry("Containers", GuiControlKind.Panel),
            new ToolboxEntry("Containers", GuiControlKind.GroupBox),
            new ToolboxEntry("Containers", GuiControlKind.FlowLayoutPanel),
            new ToolboxEntry("Containers", GuiControlKind.TableLayoutPanel),
            new ToolboxEntry("Containers", GuiControlKind.TabControl),
            new ToolboxEntry("Containers", GuiControlKind.SplitContainer),
            new ToolboxEntry("Menus & Toolbars", GuiControlKind.MenuStrip),
            new ToolboxEntry("Menus & Toolbars", GuiControlKind.ToolStrip),
            new ToolboxEntry("Menus & Toolbars", GuiControlKind.StatusStrip),
            new ToolboxEntry("Menus & Toolbars", GuiControlKind.ContextMenuStrip),
            new ToolboxEntry("Data", GuiControlKind.DataGridView),
            new ToolboxEntry("Data", GuiControlKind.Chart),
            new ToolboxEntry("Data", GuiControlKind.BindingNavigator),
            new ToolboxEntry("Data", GuiControlKind.BindingSource),
            new ToolboxEntry("Components", GuiControlKind.Timer),
            new ToolboxEntry("Components", GuiControlKind.ImageList),
            new ToolboxEntry("Components", GuiControlKind.ErrorProvider),
            new ToolboxEntry("Components", GuiControlKind.ToolTip),
            new ToolboxEntry("Components", GuiControlKind.NotifyIcon),
            new ToolboxEntry("Dialogs", GuiControlKind.OpenFileDialog),
            new ToolboxEntry("Dialogs", GuiControlKind.SaveFileDialog),
            new ToolboxEntry("Dialogs", GuiControlKind.ColorDialog),
            new ToolboxEntry("Dialogs", GuiControlKind.FontDialog),
            new ToolboxEntry("Dialogs", GuiControlKind.FolderBrowserDialog),
        };

        private void BuildToolboxItems(ToolboxPalette toolbox)
        {
            string filter = _toolboxSearch != null && _toolboxSearch.Focused
                ? _toolboxSearch.Text.Trim() : string.Empty;

            var entries = new List<ToolboxPalette.Entry>
            {
                new ToolboxPalette.Entry("General", "Pointer", ToolboxImage("Pointer"), null,
                    "Select and move controls already on the form"),
            };

            foreach (ToolboxEntry entry in ToolboxItems)
            {
                if (!GuiFrameworkCapabilities.Supports(FrameworkFromCombo(), entry.Kind)) continue;
                string name = entry.Kind.ToString();
                if (filter.Length > 0
                    && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                entries.Add(new ToolboxPalette.Entry(
                    entry.Category, name, ToolboxImage(name), entry.Kind, name));
            }
            toolbox.SetEntries(entries);
        }

        /// <summary>One tool's artwork from the image list, or null when it has none.</summary>
        private Image ToolboxImage(string key)
        {
            if (_toolboxImages == null || !_toolboxImages.Images.ContainsKey(key)) return null;
            return _toolboxImages.Images[key];
        }

        /// <summary>
        /// The toolbox image list, built once per designer.
        ///
        /// <para>
        /// Three sources, tried in this order, and the order is the point. The drawn artwork in
        /// <c>assets/product/toolbox</c> wins wherever it exists; the two hand-written classic
        /// icons and the generated two-letter badge remain for whatever it does not cover. A
        /// control with no PNG therefore degrades to exactly what it looked like before, visibly
        /// different from its neighbours — which is the honest outcome for an incomplete set, and
        /// much better than substituting "the nearest icon" and quietly mislabelling a control.
        /// </para>
        /// <para>
        /// As shipped, the artwork covers all 46 kinds plus the pointer, so nothing falls through
        /// today. The chain is kept anyway: a kind added to <see cref="ToolboxItems"/> tomorrow
        /// will have no PNG on the day it is added, and the badge is what keeps it a visible row
        /// rather than a blank square. Adding its artwork later needs no code change — the lookup
        /// is by <see cref="GuiControlKind"/> name, so it is a file copy.
        /// </para>
        /// </summary>
        private static ImageList BuildToolboxImages()
        {
            var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            images.Images.Add("Pointer", ToolboxArtwork("Pointer") ?? DesignerIcon("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAadEVYdFNvZnR3YXJlAFBhaW50Lk5FVCB2My41LjEwMPRyoQAAAbpJREFUOE9j+P//PwMMh4aG+gKxVlBQEIO/vz+Dt7c3g7u7O4OxsTGDgIAAAwwg64FrBgkCNWYUFhZWA2ktZAOcnZ0Z7OzsGLS1tRn4+PhQ9KBwgJoygQb9z8/PrwGytWEugBlgaWnJYGJigtsAoIZskAEgkJ2dXQvka4O8QLQBQMW5MANAdEZGRj1QTIdoA4AK85ENALFTUlIanZycdEFhQNALQEWF6AaA+AkJCc1AOT2CBgAVFGMzACQWHR3dYmFhoYc3EIGSpbgMAImHh4e3AdOEPs50oK+vX4FswPfv3786OjrWg8Rh2MDAIAqnAerq6lUgA96+ffu+q6trJYg9d+7cnWpqavqgRKSrq8sANAh3OlBQUKgDaXZ1de0HsuMvXLhw5xsQAJ2dLycnx66kpITfAElJyUZ7e/uJUlJSukAsDgy4GSBXTJ06da+EhIShuLg4Aw8PD24XCAsLJwOxPi8vLzjzCAkJBZ89e/be169fv8nKyhaC8gEI4AwDoBwjELMAMScQ87OysmoEBgbOAbkC6I3JIDEgZsVnABNQAQcQCwGxLBCrCwoKxuvp6U0FGhYMFeNCNgAAHWkhoZFCUHsAAAAASUVORK5CYII="));
            var classic = new Dictionary<GuiControlKind, string>
            {
                { GuiControlKind.Button, "R0lGODdhGAAYAPIAAICAgP///wAAANPQySZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANHSLrc/jDKSau9K+jNexBPMIxkWQKgI5rsiIZt+6oxOzdrTd5Mrg+8zG+Xwg1dxd4RmMwAntCotKkQWK/YrBXD7Xq/4LAYkwAAOw==" },
                { GuiControlKind.CheckBox, "R0lGODdhGAAYAPIAAICAgNPQyf///wAAACZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANNSLrc/jDKSau92ILN+xYTMIxkGYCSKKwse4ZDy8ZvGsujUEfqLZA6FC+WAwZhv5JLCFElfUfbTLZrQqnMByDA7XqrD5l4lSmbz+g0JAEAOw==" },
                { GuiControlKind.ComboBox, "R0lGODlhGAAYAPIAANPQyYCAgP///wAAACZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANeSLrc/jDKSSscOOut2xBgKAoYYGLeqGZAlqoiN3hy7TIfHOb7q5OCFiAww5E4u+OA6NN9TMPigucMWK1Sxcem7Bh/wFgTtsUwjTXQcxAdr4Km8xRMumJptpplz+9TEgA7" },
                { GuiControlKind.ListBox, "R0lGODdhGAAYAPIAANPQyYCAgP///wAAACZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANUSLrc/jDKSau9aujNO2+DII6kEJIDWK4hMABByoTeaZparK6oAPy6GQ8VKBZlC1pN2dkNbyjnkBlM4jyi1quaeWZ9QGTXOzAeQcu0GMNuu9/wuCQBADs=" },
                { GuiControlKind.RadioButton, "R0lGODlhGAAYAPIAAICAgNPQyf///wAAACZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANBSLrc/jDKSau9OBPAgeZDOHAXEAqocFpmmgZC1aInClOzONjxlIs83I42FNyErqAsAHsdKzZmU+PSWK/YrHb7SAAAOw==" },
                { GuiControlKind.TextBox, "R0lGODdhGAAYAPIAAICAgNPQyf///wAAACZFySZFySZFySZFySH5BAEAAAQALAAAAAAYABgAAANrSLrc/jDKSasDOOu9xQJDKI6kGHgKKKxs667nN7z0OsfpTOvtjRKq3csnY4lsgZBRgAPqBqekQBkk5obTZbYZnB55T+avesqCbeLitDzTVdNX2/eI5vJqPbgTX+MG/oCBgoE/fIYWiImKFAkAOw==" },
            };
            foreach (ToolboxEntry entry in ToolboxItems)
            {
                string key = entry.Kind.ToString();
                if (images.Images.ContainsKey(key)) continue;
                string encoded;
                Image art = ToolboxArtwork(key)
                    ?? (classic.TryGetValue(entry.Kind, out encoded)
                        ? DesignerIcon(encoded)
                        : BuildToolboxIcon(entry.Kind, CategoryColor(entry.Category)));
                images.Images.Add(key, art);
            }
            return images;
        }

        /// <summary>
        /// The drawn 16x16 for one toolbox key from <c>assets/product/toolbox</c>, or null when
        /// there is none.
        ///
        /// Null rather than a placeholder, deliberately: the caller's fallback chain is what makes
        /// a missing file degrade to the old badge instead of to a blank square, and a blank square
        /// is the one outcome that looks like a broken IDE rather than an unfinished icon set.
        /// Every failure mode — no file, no assets folder, a PNG that will not decode — lands here.
        /// </summary>
        private static Image ToolboxArtwork(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try { return ToolbarIconDiskLoader.Load("product\\toolbox", key + ".png"); }
            catch { return null; }
        }

        private static Bitmap DesignerIcon(string base64)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(base64)))
            using (var source = new Bitmap(stream))
                return new Bitmap(source, new Size(16, 16));
        }

        private static Image PropertyIcon(string base64) { return DesignerIcon(base64); }

        private static Color CategoryColor(string category)
        {
            switch (category)
            {
                case "Common Controls": return Color.FromArgb(91, 155, 213);
                case "Containers": return Color.FromArgb(112, 173, 71);
                case "Menus & Toolbars": return Color.FromArgb(237, 125, 49);
                case "Data": return Color.FromArgb(165, 105, 189);
                case "Components": return Color.FromArgb(128, 128, 128);
                case "Dialogs": return Color.FromArgb(191, 143, 0);
                default: return Color.FromArgb(120, 120, 120);
            }
        }

        // A small tinted badge with the control kind's initials — a real per-item icon without
        // depending on external icon assets for the full ~45-entry toolbox.
        private static Bitmap BuildToolboxIcon(GuiControlKind kind, Color tint)
        {
            var bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(tint)) g.FillRectangle(b, 1, 1, 14, 14);
                using (var p = new Pen(Color.FromArgb(60, 60, 60))) g.DrawRectangle(p, 1, 1, 13, 13);
                string label = Initials(kind.ToString());
                using (var font = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(label, font);
                    g.DrawString(label, font, Brushes.White, 8 - sz.Width / 2, 8 - sz.Height / 2);
                }
            }
            return bmp;
        }

        private static string Initials(string name)
        {
            var caps = new List<char>();
            foreach (char c in name) if (char.IsUpper(c)) caps.Add(c);
            if (caps.Count >= 2) return "" + caps[0] + caps[1];
            if (caps.Count == 1 && name.Length > 1) return "" + caps[0] + char.ToUpperInvariant(name[1]);
            return name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant();
        }


        private void OnCanvasDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(ToolboxDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnCanvasDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(ToolboxDragFormat)) return;
            string token = e.Data.GetData(ToolboxDragFormat) as string;
            GuiControlKind kind = GuiControlKinds.FromToken(token);
            Point client = _canvas.PointToClient(new Point(e.X, e.Y));
            Rectangle fr = FormRect;
            var formLocal = new Point(client.X - fr.X, client.Y - fr.Y - TitleBarHeight);
            AddControl(kind, formLocal);
        }

        // Adds a new control of `kind`, either at an explicit drop position (form-local coordinates)
        // or — for a double-click add, or a non-visual component (which has no on-form position at
        // all) — at the next free, non-overlapping spot / the component tray.
        private void AddControl(GuiControlKind kind, Point? formLocal)
        {
            _history.Record(_form);
            int n = _form.Controls.Count + 1;
            Size size = DefaultSizeFor(kind);
            var ctrl = new GuiControl
            {
                Id = "ctl" + n,
                Kind = kind,
                Name = kind.ToString().ToLowerInvariant() + n,
                Width = size.Width,
                Height = size.Height,
            };
            if (kind == GuiControlKind.Button || kind == GuiControlKind.Label || kind == GuiControlKind.CheckBox
                || kind == GuiControlKind.RadioButton || kind == GuiControlKind.LinkLabel || kind == GuiControlKind.GroupBox)
                ctrl.Props["Text"] = kind.ToString();

            if (GuiControlKinds.IsNonVisualComponent(kind))
            {
                ctrl.X = 0;
                ctrl.Y = 0;
            }
            else if (formLocal.HasValue)
            {
                int fx = Math.Max(0, Math.Min(formLocal.Value.X, Math.Max(0, _form.Width - ctrl.Width)));
                int fy = Math.Max(0, Math.Min(formLocal.Value.Y, Math.Max(0, _form.Height - ctrl.Height)));
                ctrl.X = SnapCoord(fx);
                ctrl.Y = SnapCoord(fy);
            }
            else
            {
                Point spot = FindNonOverlappingSpot(ctrl.Width, ctrl.Height);
                ctrl.X = spot.X;
                ctrl.Y = spot.Y;
            }

            _form.Controls.Add(ctrl);
            _selected = ctrl;
            RefreshPropertyGrid();
            _canvas.Invalidate();
            GenerateCode(true);
        }

        private static Size DefaultSizeFor(GuiControlKind kind)
        {
            switch (kind)
            {
                case GuiControlKind.Panel:
                case GuiControlKind.GroupBox:
                case GuiControlKind.TabControl:
                case GuiControlKind.SplitContainer:
                case GuiControlKind.FlowLayoutPanel:
                case GuiControlKind.TableLayoutPanel:
                    return new Size(200, 140);
                case GuiControlKind.PictureBox:
                    return new Size(96, 96);
                case GuiControlKind.ListBox:
                case GuiControlKind.CheckedListBox:
                case GuiControlKind.ListView:
                case GuiControlKind.TreeView:
                case GuiControlKind.DataGridView:
                    return new Size(160, 100);
                case GuiControlKind.MenuStrip:
                case GuiControlKind.ToolStrip:
                case GuiControlKind.StatusStrip:
                    return new Size(300, 24);
                default:
                    return new Size(120, 28);
            }
        }

        // Scans the form in a simple row-major grid for the first free rectangle so consecutively
        // double-clicked toolbox items land side by side instead of stacking on top of each other.
        private Point FindNonOverlappingSpot(int width, int height)
        {
            const int margin = 12;
            int stepX = width + margin;
            int stepY = height + margin;
            for (int y = margin; y + height <= _form.Height - margin; y += stepY)
            {
                for (int x = margin; x + width <= _form.Width - margin; x += stepX)
                {
                    var candidate = new Rectangle(x, y, width, height);
                    bool overlaps = false;
                    foreach (GuiControl c in _form.Controls)
                    {
                        if (GuiControlKinds.IsNonVisualComponent(c.Kind)) continue;
                        if (candidate.IntersectsWith(new Rectangle(c.X, c.Y, c.Width, c.Height))) { overlaps = true; break; }
                    }
                    if (!overlaps) return new Point(x, y);
                }
            }
            // Form has no free grid cell left — cascade, same fallback spirit as before.
            int n = _form.Controls.Count + 1;
            return new Point(margin + (n % 5) * 14, margin + (n % 8) * 14);
        }

        // ================= Canvas geometry / hit-testing =================

        private Rectangle FormRect
        {
            get
            {
                int x = Math.Max(FormMargin, (_canvas.ClientSize.Width - _form.Width) / 2);
                int y = Math.Max(FormMargin + 12, (_canvas.ClientSize.Height - _form.Height - TrayHeight) / 2);
                return new Rectangle(x, y, _form.Width, _form.Height);
            }
        }

        // A TabControl (Notebook) child fills its parent's tab content area — it has no meaningful
        // X/Y/Width/Height of its own (a real Notebook.add() child is sized by the container, not
        // placed independently), so its screen rect is derived from the notebook's, not its own
        // fields. Everything else keeps the original absolute form-relative placement.
        private Rectangle ControlScreenRect(GuiControl c)
        {
            GuiControl parent = FindById(c.ParentId);
            if (parent != null && parent.Kind == GuiControlKind.TabControl)
            {
                Rectangle parentRect = ControlScreenRect(parent);
                return new Rectangle(parentRect.X + 2, parentRect.Y + TabStripHeight,
                    Math.Max(0, parentRect.Width - 4), Math.Max(0, parentRect.Height - TabStripHeight - 2));
            }
            Rectangle fr = FormRect;
            return new Rectangle(fr.X + c.X, fr.Y + TitleBarHeight + c.Y, c.Width, c.Height);
        }

        private GuiControl FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (GuiControl c in _form.Controls)
                if (c.Id == id) return c;
            return null;
        }

        private List<GuiControl> ChildrenOf(GuiControl parent)
        {
            var list = new List<GuiControl>();
            if (parent == null) return list;
            foreach (GuiControl c in _form.Controls)
                if (c.ParentId == parent.Id) list.Add(c);
            return list;
        }

        private bool IsNotebookChild(GuiControl c)
        {
            GuiControl parent = c != null ? FindById(c.ParentId) : null;
            return parent != null && parent.Kind == GuiControlKind.TabControl;
        }

        // Which of a Notebook's children is currently showing on the canvas — clamped in case
        // controls were added/removed/reordered since the index was last set.
        private int ActiveTabIndex(GuiControl notebook, int childCount)
        {
            if (childCount == 0) return 0;
            int idx;
            if (!_activeTabByNotebookId.TryGetValue(notebook.Id, out idx)) idx = 0;
            return Math.Max(0, Math.Min(idx, childCount - 1));
        }

        private bool IsHiddenNotebookChild(GuiControl c)
        {
            GuiControl parent = FindById(c.ParentId);
            if (parent == null || parent.Kind != GuiControlKind.TabControl) return false;
            List<GuiControl> siblings = ChildrenOf(parent);
            return siblings.IndexOf(c) != ActiveTabIndex(parent, siblings.Count);
        }

        private static Rectangle HandleRect(Rectangle r, HandlePos pos)
        {
            int half = HandleSize / 2;
            switch (pos)
            {
                case HandlePos.TopLeft: return new Rectangle(r.X - half, r.Y - half, HandleSize, HandleSize);
                case HandlePos.Top: return new Rectangle(r.X + r.Width / 2 - half, r.Y - half, HandleSize, HandleSize);
                case HandlePos.TopRight: return new Rectangle(r.Right - half, r.Y - half, HandleSize, HandleSize);
                case HandlePos.Right: return new Rectangle(r.Right - half, r.Y + r.Height / 2 - half, HandleSize, HandleSize);
                case HandlePos.BottomRight: return new Rectangle(r.Right - half, r.Bottom - half, HandleSize, HandleSize);
                case HandlePos.Bottom: return new Rectangle(r.X + r.Width / 2 - half, r.Bottom - half, HandleSize, HandleSize);
                case HandlePos.BottomLeft: return new Rectangle(r.X - half, r.Bottom - half, HandleSize, HandleSize);
                case HandlePos.Left: return new Rectangle(r.X - half, r.Y + r.Height / 2 - half, HandleSize, HandleSize);
                default: return Rectangle.Empty;
            }
        }

        private static HandlePos HitTestHandles(Rectangle r, Point pt, HandlePos[] candidates)
        {
            foreach (HandlePos pos in candidates)
                if (HandleRect(r, pos).Contains(pt)) return pos;
            return HandlePos.None;
        }

        private static Cursor CursorForHandle(HandlePos pos)
        {
            switch (pos)
            {
                case HandlePos.TopLeft:
                case HandlePos.BottomRight: return Cursors.SizeNWSE;
                case HandlePos.TopRight:
                case HandlePos.BottomLeft: return Cursors.SizeNESW;
                case HandlePos.Left:
                case HandlePos.Right: return Cursors.SizeWE;
                case HandlePos.Top:
                case HandlePos.Bottom: return Cursors.SizeNS;
                default: return Cursors.Default;
            }
        }

        private int SnapCoord(int v)
        {
            int grid = _form.Viewport != null ? Math.Max(1, _form.Viewport.GridSize) : 8;
            return (int)Math.Round(v / (double)grid) * grid;
        }

        // General 8-direction resize: given the drag-start bounds and the mouse delta, computes the
        // new bounds for whichever handle is being dragged. Left/Top handles keep the OPPOSITE edge
        // anchored (recomputing width/height from the snapped near edge) so grid-snapping never lets
        // the box drift off its anchor.
        private void ApplyResize(HandlePos handle, Rectangle start, Point startMouse, Point currentMouse, int minSize,
            out int x, out int y, out int w, out int h)
        {
            int dx = currentMouse.X - startMouse.X;
            int dy = currentMouse.Y - startMouse.Y;
            x = start.X; y = start.Y; w = start.Width; h = start.Height;

            bool left = handle == HandlePos.TopLeft || handle == HandlePos.Left || handle == HandlePos.BottomLeft;
            bool right = handle == HandlePos.TopRight || handle == HandlePos.Right || handle == HandlePos.BottomRight;
            bool top = handle == HandlePos.TopLeft || handle == HandlePos.Top || handle == HandlePos.TopRight;
            bool bottom = handle == HandlePos.BottomLeft || handle == HandlePos.Bottom || handle == HandlePos.BottomRight;

            if (left)
            {
                int clampedDx = Math.Min(dx, start.Width - minSize);
                x = SnapCoord(start.X + clampedDx);
                w = Math.Max(minSize, start.X + start.Width - x);
            }
            if (right) w = SnapCoord(Math.Max(minSize, start.Width + dx));
            if (top)
            {
                int clampedDy = Math.Min(dy, start.Height - minSize);
                y = SnapCoord(start.Y + clampedDy);
                h = Math.Max(minSize, start.Y + start.Height - y);
            }
            if (bottom) h = SnapCoord(Math.Max(minSize, start.Height + dy));
        }

        // ================= Mouse interaction =================

        private void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            // Tab-header clicks switch the active tab rather than selecting/dragging anything.
            foreach (KeyValuePair<string, Rectangle> kvp in _tabHeaderRects)
            {
                if (!kvp.Value.Contains(e.Location)) continue;
                int sep = kvp.Key.IndexOf('|');
                string notebookId = kvp.Key.Substring(0, sep);
                int idx = int.Parse(kvp.Key.Substring(sep + 1));
                _activeTabByNotebookId[notebookId] = idx;
                _selected = FindById(notebookId);
                RefreshPropertyGrid();
                _canvas.Invalidate();
                return;
            }

            if (_selected != null && !GuiControlKinds.IsNonVisualComponent(_selected.Kind) && !IsNotebookChild(_selected))
            {
                Rectangle cr = ControlScreenRect(_selected);
                HandlePos h = HitTestHandles(cr, e.Location, AllHandles);
                if (h != HandlePos.None)
                {
                    _history.Record(_form);
                    _dragKind = DragKind.ResizeControl;
                    _dragHandle = h;
                    _dragStartMouse = e.Location;
                    _dragStartBounds = new Rectangle(_selected.X, _selected.Y, _selected.Width, _selected.Height);
                    return;
                }
                if (cr.Contains(e.Location))
                {
                    _history.Record(_form);
                    _dragKind = DragKind.MoveControl;
                    _dragHandle = HandlePos.None;
                    _dragStartMouse = e.Location;
                    _dragStartBounds = new Rectangle(_selected.X, _selected.Y, _selected.Width, _selected.Height);
                    return;
                }
            }
            else if (_selected == null)
            {
                HandlePos fh = HitTestHandles(FormRect, e.Location, FormHandles);
                if (fh != HandlePos.None)
                {
                    _history.Record(_form);
                    _dragKind = DragKind.ResizeForm;
                    _dragHandle = fh;
                    _dragStartMouse = e.Location;
                    _dragStartFormSize = new Size(_form.Width, _form.Height);
                    return;
                }
            }

            // Hit-test controls topmost-first; non-visual components live in the tray strip instead.
            // A hidden (inactive) notebook tab's child has no visible rect at all — skip it so a
            // click can't "select" something that isn't actually on screen.
            GuiControl hit = null;
            for (int i = _form.Controls.Count - 1; i >= 0; i--)
            {
                GuiControl c = _form.Controls[i];
                if (GuiControlKinds.IsNonVisualComponent(c.Kind) || IsHiddenNotebookChild(c)) continue;
                if (ControlScreenRect(c).Contains(e.Location)) { hit = c; break; }
            }
            if (hit == null)
            {
                foreach (KeyValuePair<GuiControl, Rectangle> kvp in _trayRects)
                    if (kvp.Value.Contains(e.Location)) { hit = kvp.Key; break; }
            }

            _selected = hit;
            _dragKind = DragKind.None;
            // A notebook's child fills the tab area automatically — it can be SELECTED (to view/edit
            // its Properties) but not dragged/resized, the same way a WinForms designer won't let you
            // freely move a control whose layout is container-managed.
            if (hit != null && !GuiControlKinds.IsNonVisualComponent(hit.Kind) && !IsNotebookChild(hit))
            {
                _history.Record(_form);
                _dragKind = DragKind.MoveControl;
                _dragHandle = HandlePos.None;
                _dragStartMouse = e.Location;
                _dragStartBounds = new Rectangle(hit.X, hit.Y, hit.Width, hit.Height);
            }
            RefreshPropertyGrid();
            _canvas.Invalidate();
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragKind == DragKind.MoveControl && _selected != null)
            {
                int dx = e.X - _dragStartMouse.X;
                int dy = e.Y - _dragStartMouse.Y;
                _selected.X = Math.Max(0, SnapCoord(_dragStartBounds.X + dx));
                _selected.Y = Math.Max(0, SnapCoord(_dragStartBounds.Y + dy));
                _canvas.Invalidate();
                _propertyGrid.Refresh();
                return;
            }
            if (_dragKind == DragKind.ResizeControl && _selected != null)
            {
                int nx, ny, nw, nh;
                ApplyResize(_dragHandle, _dragStartBounds, _dragStartMouse, e.Location, MinControlSize, out nx, out ny, out nw, out nh);
                _selected.X = Math.Max(0, nx);
                _selected.Y = Math.Max(0, ny);
                _selected.Width = nw;
                _selected.Height = nh;
                _canvas.Invalidate();
                _propertyGrid.Refresh();
                return;
            }
            if (_dragKind == DragKind.ResizeForm)
            {
                var start = new Rectangle(0, 0, _dragStartFormSize.Width, _dragStartFormSize.Height);
                int ignoredX, ignoredY, fw, fh;
                ApplyResize(_dragHandle, start, _dragStartMouse, e.Location, MinFormSize, out ignoredX, out ignoredY, out fw, out fh);
                _form.Width = fw;
                _form.Height = fh;
                _canvas.Invalidate();
                // The grid shows ClientWidth/ClientHeight, so it goes stale the moment the form is
                // dragged. Resizing a CONTROL already refreshed it; resizing the form did not, and
                // the grid then reported the size the form used to be — which is worse than showing
                // nothing, because it looks authoritative.
                _propertyGrid.Refresh();
                return;
            }

            // Not dragging — VS2022-style hover cursor feedback over handles / the selected control.
            Cursor desired = Cursors.Default;
            if (_selected != null && !GuiControlKinds.IsNonVisualComponent(_selected.Kind) && !IsNotebookChild(_selected))
            {
                Rectangle cr = ControlScreenRect(_selected);
                HandlePos h = HitTestHandles(cr, e.Location, AllHandles);
                if (h != HandlePos.None) desired = CursorForHandle(h);
                else if (cr.Contains(e.Location)) desired = Cursors.SizeAll;
            }
            else if (_selected == null)
            {
                HandlePos fh = HitTestHandles(FormRect, e.Location, FormHandles);
                if (fh != HandlePos.None) desired = CursorForHandle(fh);
            }
            if (_canvas.Cursor != desired) _canvas.Cursor = desired;
        }

        private void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragKind != DragKind.None)
            {
                _dragKind = DragKind.None;
                _dragHandle = HandlePos.None;
                GenerateCode(true);
            }
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && !e.Shift && e.KeyCode == Keys.Z)
            {
                if (_history.Undo(_form)) AfterHistoryNavigation();
                e.SuppressKeyPress = true; return;
            }
            if (e.Control && (e.KeyCode == Keys.Y || (e.Shift && e.KeyCode == Keys.Z)))
            {
                if (_history.Redo(_form)) AfterHistoryNavigation();
                e.SuppressKeyPress = true; return;
            }
            if (e.KeyCode == Keys.Delete && _selected != null)
            {
                _history.Record(_form);
                _form.Controls.Remove(_selected);
                _trayRects.Remove(_selected);
                _selected = null;
                _dragKind = DragKind.None;
                RefreshPropertyGrid();
                _canvas.Invalidate();
                GenerateCode(true);
                e.Handled = true;
            }
        }

        // ================= Painting =================

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // VS2022's own design-surface backdrop is a light gray-blue, not pure white.
            g.Clear(Color.White);

            Rectangle formRect = FormRect;

            // A drop shadow built from a few decreasing bands rather than one flat rectangle. A
            // single 35%-black offset copy reads as a grey rectangle stuck to the form; the point
            // of a shadow is the falloff, and the falloff is what lifts the window off the canvas.
            for (int band = ShadowDepth; band >= 1; band--)
            {
                using (var shadow = new SolidBrush(Color.FromArgb(10 + (ShadowDepth - band) * 6, 0, 0, 0)))
                    g.FillRectangle(shadow, new Rectangle(
                        formRect.X + band, formRect.Y + band, formRect.Width, formRect.Height));
            }
            using (var back = new SolidBrush(FormFace))
                g.FillRectangle(back, formRect);

            // Design-time snap grid — dots at each grid intersection inside the form's client area.
            int grid = _form.Viewport != null ? Math.Max(4, _form.Viewport.GridSize) : 8;
            using (var dotBrush = new SolidBrush(Color.FromArgb(214, 214, 220)))
            {
                for (int gx = formRect.X; gx < formRect.Right; gx += grid)
                    for (int gy = formRect.Y + TitleBarHeight; gy < formRect.Bottom; gy += grid)
                        g.FillRectangle(dotBrush, gx, gy, 1, 1);
            }

            DrawFormChrome(g, formRect);

            // Controls, offset into the form's client area. Non-visual components (Timer, dialogs,
            // …) have no on-form position — they render in the component tray below instead. A
            // notebook tab's INACTIVE child has no visible rect at all this frame.
            _tabHeaderRects.Clear();
            // Clipped to the client area. A widget cannot paint outside its window at run time, and
            // when the form is made smaller than its contents the designer must show what the user
            // will actually get: the overflow hidden. Drawing it anyway put a tab strip and a panel
            // out in open canvas next to the form they belong to, which reads as the designer
            // having fallen apart rather than as a form that is too small.
            System.Drawing.Drawing2D.GraphicsState clipState = g.Save();
            g.SetClip(ClientAreaOf(formRect), System.Drawing.Drawing2D.CombineMode.Intersect);
            foreach (GuiControl c in _form.Controls)
            {
                if (GuiControlKinds.IsNonVisualComponent(c.Kind) || IsHiddenNotebookChild(c)) continue;
                var r = ControlScreenRect(c);
                bool isSelected = c == _selected;

                if (c.Kind == GuiControlKind.CustomComponent)
                {
                    // Opaque placeholder — the designer has no internals to draw for a hand-written
                    // composed class, only its name/kind.
                    using (var b = new SolidBrush(Color.FromArgb(240, 235, 250))) g.FillRectangle(b, r);
                    using (var dashPen = new Pen(isSelected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(150, 110, 190), isSelected ? 1.5f : 1f))
                    {
                        dashPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                        g.DrawRectangle(dashPen, r);
                    }
                    string className = c.Props.ContainsKey("ClassName") ? c.Props["ClassName"] : c.Kind.ToString();
                    string label = "«" + className + "»";
                    SizeF sz = g.MeasureString(label, Font);
                    using (var tb = new SolidBrush(Color.FromArgb(80, 60, 110)))
                        g.DrawString(label, Font, tb, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - Font.Height) / 2);
                }
                else
                {
                    Color fill = c.Kind == GuiControlKind.Button ? Color.FromArgb(225, 232, 245)
                        : c.Kind == GuiControlKind.TextBox || c.Kind == GuiControlKind.RichTextBox ? Color.White
                        : Color.FromArgb(238, 240, 244);
                    using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                    using (var pen2 = new Pen(isSelected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(150, 150, 165), isSelected ? 1.5f : 1f))
                        g.DrawRectangle(pen2, r);
                    // An explicitly empty Text property is meaningful for input controls: the
                    // runtime field starts blank, so the designer must also draw it blank. Fall
                    // back to the component name only when Text was never specified at all.
                    string caption = c.Props.ContainsKey("Text") ? c.Props["Text"] : c.Name;
                    using (var tb = new SolidBrush(Color.FromArgb(40, 40, 50)))
                        g.DrawString(caption, Font, tb, r.X + 4, r.Y + (r.Height - Font.Height) / 2);
                }

                if (c.Kind == GuiControlKind.TabControl) DrawTabStrip(g, c, r);
            }
            // Handles and the tray belong OUTSIDE the form, so the clip ends here.
            g.Restore(clipState);

            // The form is too small for what is on it. Said plainly, because the alternative is a
            // user staring at a control that has silently vanished.
            if (AnyControlOverflows(formRect))
            {
                using (var warn = new SolidBrush(Color.FromArgb(176, 60, 40)))
                using (var f = new Font(Font, FontStyle.Bold))
                    g.DrawString("Content is clipped — the form is smaller than its controls",
                                 f, warn, formRect.X, formRect.Bottom + 4);
            }

            // Selection handles: the selected control's 8 handles (skipped for a notebook child, whose
            // geometry is container-managed, not free-form), or — when nothing is selected — the
            // form's own resize grips, exactly like VS2022's designer.
            if (_selected != null && !GuiControlKinds.IsNonVisualComponent(_selected.Kind) && !IsNotebookChild(_selected))
                DrawHandles(g, ControlScreenRect(_selected), AllHandles);
            else if (_selected == null)
                DrawHandles(g, formRect, FormHandles);

            DrawComponentTray(g, formRect);
        }

        /// <summary>The area inside the frame and below the caption — where widgets live.</summary>
        private static Rectangle ClientAreaOf(Rectangle formRect)
        {
            return new Rectangle(formRect.X + 1, formRect.Y + TitleBarHeight,
                                 Math.Max(0, formRect.Width - 1),
                                 Math.Max(0, formRect.Height - TitleBarHeight - 1));
        }

        private bool AnyControlOverflows(Rectangle formRect)
        {
            Rectangle client = ClientAreaOf(formRect);
            foreach (GuiControl c in _form.Controls)
            {
                if (GuiControlKinds.IsNonVisualComponent(c.Kind) || IsHiddenNotebookChild(c)) continue;
                if (!client.Contains(ControlScreenRect(c))) return true;
            }
            return false;
        }

        // The window being designed is a WINDOW, so it is drawn like one: a caption you can read
        // at a glance, a frame with an edge, and a shadow. A flat navy strip over a white box is a
        // diagram of a window; this is a picture of one, and the difference is what tells the user
        // that what they are arranging is the real thing.
        private const int ShadowDepth = 6;
        private static readonly Color FormFace = Color.FromArgb(241, 235, 221);
        private static readonly Color CaptionFace = Color.FromArgb(7, 39, 114);
        private static readonly Color CaptionTop = Color.FromArgb(38, 74, 155);
        private static readonly Color CaptionBottom = Color.FromArgb(0, 20, 106);
        private static readonly Color FrameEdge = Color.FromArgb(52, 71, 134);

        private void DrawFormChrome(Graphics g, Rectangle formRect)
        {
            using (var pen = new Pen(FrameEdge, 1)) g.DrawRectangle(pen, formRect);

            var caption = new Rectangle(formRect.X + 1, formRect.Y + 1, formRect.Width - 1, TitleBarHeight - 1);
            using (var face = new SolidBrush(CaptionFace)) g.FillRectangle(face, caption);
            // A light line along the top and a dark one along the bottom. Two 1px lines are the
            // entire bevel — a caption without them is a rectangle of colour, not a raised bar.
            using (var top = new Pen(CaptionTop)) g.DrawLine(top, caption.X, caption.Y, caption.Right - 1, caption.Y);
            using (var bottom = new Pen(CaptionBottom))
                g.DrawLine(bottom, caption.X, caption.Bottom - 1, caption.Right - 1, caption.Bottom - 1);

            int textLeft = caption.X + 6;
            string badge = FrameworkBadge();
            Image logo = FrameworkLogo();
            if (logo != null)
            {
                // The toolkit's OWN mark, where the toolkit has one. A window built on Tk shows
                // the Tk feather at run time, so the simulated window shows it here: the point of
                // the caption is to look like what the user is going to get.
                var logoRect = new Rectangle(caption.X + 5, caption.Y + (caption.Height - 16) / 2, 16, 16);
                InterpolationMode previous = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(logo, logoRect);
                g.InterpolationMode = previous;
                textLeft = logoRect.Right + 6;
            }
            else if (badge.Length > 0)
            {
                // No logo for this toolkit: two letters naming it, which is still better than
                // making the user go and check what the form is being built for.
                var iconRect = new Rectangle(caption.X + 5, caption.Y + (caption.Height - 14) / 2, 14, 14);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, iconRect);
                using (var b = new SolidBrush(Color.FromArgb(170, 170, 175))) g.DrawRectangle(new Pen(b), iconRect);
                using (var f = new Font(Font.FontFamily, 6.5f, FontStyle.Bold))
                using (var b = new SolidBrush(Color.FromArgb(196, 30, 30)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(badge, f, b, iconRect, sf);
                textLeft = iconRect.Right + 6;
            }

            using (var titleFont = new Font(Font, FontStyle.Bold))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                var titleRect = new Rectangle(textLeft, caption.Y,
                    Math.Max(0, caption.Right - 56 - textLeft), caption.Height);
                g.DrawString(_form.Title, titleFont, Brushes.White, titleRect, sf);
            }

            int bx = formRect.Right - 52;
            int cy = caption.Y + caption.Height / 2;
            using (var glyphPen = new Pen(Color.White, 1.2f))
            {
                g.DrawLine(glyphPen, bx, cy + 4, bx + 8, cy + 4);
                g.DrawRectangle(glyphPen, bx + 16, cy - 4, 8, 8);
                g.DrawLine(glyphPen, bx + 34, cy - 4, bx + 42, cy + 4);
                g.DrawLine(glyphPen, bx + 42, cy - 4, bx + 34, cy + 4);
            }
        }

        /// <summary>
        /// The target toolkit's logo, at caption size, or null when we do not ship one.
        ///
        /// <para>Only Tk so far. Its mark is the quill, which is what a Tk window wears on its own
        /// title bar — drawn from the .ico's 16px frame rather than reduced from the 256, because
        /// 16px is what this is drawn at.</para>
        /// </summary>
        private Image FrameworkLogo()
        {
            if (_form.Framework != GuiFramework.Tkinter) return null;
            if (_tkLogo != null) return _tkLogo;
            try
            {
                Icon icon = DesignerIcon();
                if (icon == null) return null;
                using (var sized = new Icon(icon, 16, 16))
                    _tkLogo = sized.ToBitmap();
            }
            catch { /* a lettered badge is a fine fallback; a crash while painting is not */ }
            return _tkLogo;
        }

        private static Bitmap _tkLogo;

        /// <summary>Two letters naming the target toolkit, for the caption badge.</summary>
        private string FrameworkBadge()
        {
            string name = EmitterFactory.EmitterFor(_form.Framework).FrameworkName ?? string.Empty;
            if (name.StartsWith("Tk", StringComparison.OrdinalIgnoreCase)) return "Tk";
            if (name.StartsWith("PyQt", StringComparison.OrdinalIgnoreCase)) return "Qt";
            if (name.StartsWith("PySide", StringComparison.OrdinalIgnoreCase)) return "Qt";
            if (name.StartsWith("wx", StringComparison.OrdinalIgnoreCase)) return "wx";
            if (name.StartsWith("Kivy", StringComparison.OrdinalIgnoreCase)) return "Kv";
            if (name.StartsWith("GTK", StringComparison.OrdinalIgnoreCase)) return "Gtk";
            return name.Length >= 2 ? name.Substring(0, 2) : name;
        }

        // One small tab per child, labeled with Props["TabTitle"] (falling back to the child's own
        // Name). Clicking a tab (handled in OnCanvasMouseDown via the cached rects below) switches
        // which child is the visible one — Tkinter's Notebook only ever shows one page at a time.
        private void DrawTabStrip(Graphics g, GuiControl notebook, Rectangle notebookRect)
        {
            List<GuiControl> kids = ChildrenOf(notebook);
            if (kids.Count == 0) return;
            int active = ActiveTabIndex(notebook, kids.Count);
            int x = notebookRect.X + 2;
            for (int i = 0; i < kids.Count; i++)
            {
                GuiControl kid = kids[i];
                string label = kid.Props.ContainsKey("TabTitle") && kid.Props["TabTitle"].Length > 0 ? kid.Props["TabTitle"] : kid.Name;
                SizeF sz = g.MeasureString(label, Font);
                var tabRect = new Rectangle(x, notebookRect.Y + 2, (int)sz.Width + 16, TabStripHeight - 3);
                bool isActive = i == active;
                using (var b = new SolidBrush(isActive ? Color.White : Color.FromArgb(224, 224, 230)))
                    g.FillRectangle(b, tabRect);
                // Raised: light along the top and left, dark along the bottom and right. The same
                // two lines that lift the caption lift a tab, and a tab strip drawn with a single
                // flat outline is the one thing that most makes a mock-up look like a mock-up.
                using (var light = new Pen(Color.White))
                {
                    g.DrawLine(light, tabRect.X, tabRect.Bottom - 1, tabRect.X, tabRect.Y);
                    g.DrawLine(light, tabRect.X, tabRect.Y, tabRect.Right - 1, tabRect.Y);
                }
                using (var dark = new Pen(Color.FromArgb(128, 128, 138)))
                {
                    g.DrawLine(dark, tabRect.Right - 1, tabRect.Y, tabRect.Right - 1, tabRect.Bottom - 1);
                    if (!isActive) g.DrawLine(dark, tabRect.X, tabRect.Bottom - 1, tabRect.Right - 1, tabRect.Bottom - 1);
                }
                using (var tb = new SolidBrush(Color.FromArgb(40, 40, 50)))
                    g.DrawString(label, Font, tb, tabRect.X + 8, tabRect.Y + (tabRect.Height - Font.Height) / 2);
                _tabHeaderRects[notebook.Id + "|" + i] = tabRect;
                x += tabRect.Width + 2;
            }
        }

        private static void DrawHandles(Graphics g, Rectangle r, HandlePos[] handles)
        {
            // Solid, not hollow. A hollow handle on a light background is a faint outline the eye
            // slides over; a filled one is a grab point you can see without looking for it.
            using (var fillBrush = new SolidBrush(Color.FromArgb(0, 84, 200)))
            using (var borderPen = new Pen(Color.White, 1))
            {
                foreach (HandlePos pos in handles)
                {
                    Rectangle hr = HandleRect(r, pos);
                    g.FillRectangle(fillBrush, hr);
                    g.DrawRectangle(borderPen, hr);
                }
            }
        }

        // Non-visual components (Timer, ImageList, dialogs, …) render as a strip of small chips
        // below the form — the same "component tray" convention VS2022 itself uses. Chip rectangles
        // are cached in _trayRects so mouse-down can select them.
        private void DrawComponentTray(Graphics g, Rectangle formRect)
        {
            _trayRects.Clear();
            var nonVisual = new List<GuiControl>();
            foreach (GuiControl c in _form.Controls)
                if (GuiControlKinds.IsNonVisualComponent(c.Kind)) nonVisual.Add(c);
            if (nonVisual.Count == 0) return;

            var trayRect = new Rectangle(formRect.X, formRect.Bottom + 16, Math.Max(formRect.Width, 200), TrayHeight);
            using (var b = new SolidBrush(Color.FromArgb(238, 238, 242))) g.FillRectangle(b, trayRect);
            using (var p = new Pen(Color.FromArgb(190, 190, 200))) g.DrawRectangle(p, trayRect);

            int x = trayRect.X + 6;
            foreach (GuiControl c in nonVisual)
            {
                string label = c.Name + " (" + c.Kind + ")";
                SizeF sz = g.MeasureString(label, Font);
                var chip = new Rectangle(x, trayRect.Y + 5, (int)sz.Width + 16, TrayHeight - 10);
                bool isSelected = c == _selected;
                using (var cb = new SolidBrush(isSelected ? Color.FromArgb(220, 232, 250) : Color.White)) g.FillRectangle(cb, chip);
                using (var cp = new Pen(isSelected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(190, 190, 200))) g.DrawRectangle(cp, chip);
                using (var tb = new SolidBrush(Color.FromArgb(40, 40, 50)))
                    g.DrawString(label, Font, tb, chip.X + 8, chip.Y + (chip.Height - Font.Height) / 2);
                _trayRects[c] = chip;
                x += chip.Width + 6;
            }
        }

        // ================= Properties grid =================

        private void RefreshPropertyGrid()
        {
            _propertyGrid.SelectedObject = _selected != null
                ? (object)new GuiControlProxy(_selected, BeginPropertyTransaction, OnControlPropertyChangedFromGrid)
                : new GuiFormProxy(_form, BeginPropertyTransaction, OnControlPropertyChangedFromGrid);
            RefreshEventsList();
            RefreshObjectSelector();
        }

        private void RefreshObjectSelector()
        {
            _refreshingObjectSelector = true;
            _objectSelector.Items.Clear();
            _objectSelector.Items.Add(_form.FormClassName + "  " + EmitterFactory.EmitterFor(_form.Framework).FrameworkName + ".Form");
            int selectedIndex = 0;
            for (int i = 0; i < _form.Controls.Count; i++)
            {
                GuiControl control = _form.Controls[i];
                _objectSelector.Items.Add(control.Name + "  " + control.Kind);
                if (control == _selected) selectedIndex = i + 1;
            }
            if (_objectSelector.Items.Count > 0) _objectSelector.SelectedIndex = selectedIndex;
            _refreshingObjectSelector = false;
        }

        private void OnObjectSelectorChanged(object sender, EventArgs e)
        {
            if (_refreshingObjectSelector) return;
            int index = _objectSelector.SelectedIndex;
            _selected = index > 0 && index - 1 < _form.Controls.Count ? _form.Controls[index - 1] : null;
            RefreshPropertyGrid(); _canvas.Invalidate();
        }

        private void RefreshEventsList()
        {
            _eventsList.Items.Clear();
            if (_selected == null) return;
            string[] names = { "Click", "DoubleClick", "TextChanged", "CheckedChanged", "SelectionChanged", "KeyDown" };
            foreach (string name in names)
            {
                string handler = string.Empty;
                foreach (GuiEvent guiEvent in _selected.Events) if (guiEvent.EventName == name) { handler = guiEvent.HandlerName; break; }
                _eventsList.Items.Add(new ListViewItem(new[] { name, handler }) { Tag = name });
            }
        }

        private void OnEventDoubleClick(object sender, EventArgs e)
        {
            if (_selected == null || _eventsList.SelectedItems.Count == 0) return;
            string eventName = _eventsList.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(eventName)) return;
            _history.Record(_form);
            GuiEvent match = null;
            foreach (GuiEvent guiEvent in _selected.Events) if (guiEvent.EventName == eventName) { match = guiEvent; break; }
            if (match == null) { match = new GuiEvent { EventName = eventName }; _selected.Events.Add(match); }
            string controlName = string.IsNullOrEmpty(_selected.Name) ? "Control" : _selected.Name;
            match.HandlerName = "On" + char.ToUpperInvariant(controlName[0]) + controlName.Substring(1) + eventName;
            RefreshEventsList(); GenerateCode(true);
        }

        private void BeginPropertyTransaction()
        {
            if (_propertyTransactionPending) return;
            _history.Record(_form);
            _propertyTransactionPending = true;
        }

        private void OnControlPropertyChangedFromGrid()
        {
            _propertyTransactionPending = false;
            _canvas.Invalidate();
            GenerateCode(true);
        }

        private void AfterHistoryNavigation()
        {
            _selected = null;
            _dragKind = DragKind.None;
            _frameworkCombo.SelectedIndex = ComboIndexForFramework(_form.Framework);
            RefreshPropertyGrid(); _canvas.Invalidate(); GenerateCode(true);
            UpdateHistoryButtons();
        }

        /// <summary>Greys Undo and Redo when there is nothing to undo or redo. A menu could get away
        /// without this — you only find out on opening it — but a button that looks live and does
        /// nothing when clicked is a button the user stops trusting.</summary>
        private void UpdateHistoryButtons()
        {
            if (_undoButton != null) _undoButton.Enabled = _history.CanUndo;
            if (_redoButton != null) _redoButton.Enabled = _history.CanRedo;
        }

        /// <summary>Adapts a <see cref="GuiControl"/>'s handful of editable fields to plain reflected
        /// properties so <see cref="PropertyGrid"/> can bind to it directly (no ICustomTypeDescriptor
        /// needed since every exposed property is a simple string/int).</summary>
        private sealed class GuiControlProxy
        {
            private readonly GuiControl _c;
            private readonly Action _beforeChanged;
            private readonly Action _onChanged;

            public GuiControlProxy(GuiControl c, Action beforeChanged, Action onChanged) { _c = c; _beforeChanged = beforeChanged; _onChanged = onChanged; }

            [Category("Design")]
            public string Name
            {
                get { return _c.Name; }
                set { _beforeChanged(); _c.Name = value ?? string.Empty; _onChanged(); }
            }

            [Category("Design"), ReadOnly(true)]
            public string Kind { get { return _c.Kind.ToString(); } }

            [Category("Layout")]
            public int X
            {
                get { return _c.X; }
                set { _beforeChanged(); _c.X = Math.Max(0, value); _onChanged(); }
            }

            [Category("Layout")]
            public int Y
            {
                get { return _c.Y; }
                set { _beforeChanged(); _c.Y = Math.Max(0, value); _onChanged(); }
            }

            [Category("Layout")]
            public int Width
            {
                get { return _c.Width; }
                set { _beforeChanged(); _c.Width = Math.Max(MinControlSize, value); _onChanged(); }
            }

            [Category("Layout")]
            public int Height
            {
                get { return _c.Height; }
                set { _beforeChanged(); _c.Height = Math.Max(MinControlSize, value); _onChanged(); }
            }

            [Category("Appearance")]
            public string Text
            {
                get { string v; return _c.Props.TryGetValue("Text", out v) ? v : string.Empty; }
                set { _beforeChanged(); _c.Props["Text"] = value ?? string.Empty; _onChanged(); }
            }

            [Category("Appearance")] public string BackColor { get { return Prop("BackColor", "Control"); } set { SetProp("BackColor", value); } }
            [Category("Appearance")] public string ForeColor { get { return Prop("ForeColor", "ControlText"); } set { SetProp("ForeColor", value); } }
            [Category("Appearance")] public string Font { get { return Prop("Font", "Microsoft Sans Serif; 8.25; Regular"); } set { SetProp("Font", value); } }
            [Category("Appearance")] public string Cursor { get { return Prop("Cursor", "Default"); } set { SetProp("Cursor", value); } }
            [Category("Behavior")] public bool Enabled { get { return BoolProp("Enabled", true); } set { SetProp("Enabled", value.ToString()); } }
            [Category("Behavior")] public bool Visible { get { return BoolProp("Visible", true); } set { SetProp("Visible", value.ToString()); } }
            [Category("Behavior")] public int TabIndex { get { return IntProp("TabIndex", 0); } set { SetProp("TabIndex", value.ToString()); } }
            [Category("Behavior")] public bool TabStop { get { return BoolProp("TabStop", true); } set { SetProp("TabStop", value.ToString()); } }
            [Category("Design")] public bool Locked { get { return BoolProp("Locked", false); } set { SetProp("Locked", value.ToString()); } }
            [Category("Design")] public string Modifiers { get { return Prop("Modifiers", "Private"); } set { SetProp("Modifiers", value); } }
            [Category("Layout")] public string Anchor { get { return Prop("Anchor", "Top, Left"); } set { SetProp("Anchor", value); } }
            [Category("Layout")] public string Dock { get { return Prop("Dock", "None"); } set { SetProp("Dock", value); } }
            [Category("Layout")] public string Margin { get { return Prop("Margin", "3, 3, 3, 3"); } set { SetProp("Margin", value); } }
            [Category("Layout")] public string Padding { get { return Prop("Padding", "0, 0, 0, 0"); } set { SetProp("Padding", value); } }

            private string Prop(string key, string fallback) { string value; return _c.Props.TryGetValue(key, out value) ? value : fallback; }
            private bool BoolProp(string key, bool fallback) { bool value; return bool.TryParse(Prop(key, null), out value) ? value : fallback; }
            private int IntProp(string key, int fallback) { int value; return int.TryParse(Prop(key, null), out value) ? value : fallback; }
            private void SetProp(string key, string value) { _beforeChanged(); _c.Props[key] = value ?? string.Empty; _onChanged(); }
        }

        private sealed class GuiFormProxy
        {
            private readonly GuiForm _form; private readonly Action _before; private readonly Action _after;
            public GuiFormProxy(GuiForm form, Action before, Action after) { _form = form; _before = before; _after = after; }
            [Category("Design")] public string Name { get { return _form.FormClassName; } set { _before(); _form.FormClassName = value ?? "MainForm"; _after(); } }
            [Category("Appearance")] public string Text { get { return _form.Title; } set { _before(); _form.Title = value ?? "Form"; _after(); } }
            [Category("Layout")] public int ClientWidth { get { return _form.Width; } set { _before(); _form.Width = Math.Max(MinFormSize, value); _after(); } }
            [Category("Layout")] public int ClientHeight { get { return _form.Height; } set { _before(); _form.Height = Math.Max(MinFormSize, value); _after(); } }
            [Category("Design"), ReadOnly(true)] public string Framework { get { return EmitterFactory.EmitterFor(_form.Framework).FrameworkName; } }
        }

        // ================= Code generation + live file sync =================

        // A file is safe to start auto-overwriting without asking: it doesn't exist yet, it's
        // empty, or it already carries the "# AUTO-GENERATED by HelixPython." banner every emitter
        // writes (i.e. a previous designer session already owns it). Anything else is presumed to be
        // real, hand-written code that must not be silently replaced.
        private static bool IsSafeToAdopt(string pyPath)
        {
            if (!File.Exists(pyPath)) return true;
            string content;
            try { content = File.ReadAllText(pyPath); }
            catch { return false; }
            if (string.IsNullOrWhiteSpace(content)) return true;
            // Banner match is product-name tolerant: a file this designer generated under an
            // earlier product name is still its own output and must stay adoptable.
            foreach (GuiFramework framework in EmitterFactory.AllFrameworks())
                if (EmitterCommon.StartsWithAutoGeneratedBanner(content, EmitterFactory.EmitterFor(framework).AutoGeneratedMarker)) return true;
            return false;
        }

        // "Take over" is destructive by construction: the canvas can only ever hold what's IN the
        // model, and the model is either a best-effort TkinterLayoutImporter import (which can miss
        // things it doesn't recognize — custom widget internals, unusual layout, anything outside
        // __init__/_build_ui) or, if that found nothing, empty. Either way, saving replaces the
        // file's ENTIRE contents with just what the model holds, discarding anything not represented
        // there except code already inside `# region UserCode-*` blocks — this is exactly what went
        // wrong before this guard existed: an empty canvas + "take over" silently deleted a real,
        // hand-written class's whole body, including its `def main()`, breaking PSM-09. A modal,
        // explicit confirmation (worded differently depending on whether anything was importable)
        // plus a one-time backup make that outcome recoverable and impossible to trigger without
        // someone reading the question and answering it.
        private bool AskToAdopt()
        {
            string body = _form.Controls.Count > 0
                ? "This canvas was populated by a best-effort IMPORT of the file's existing widgets — " +
                  "review it first. It can miss things the importer doesn't recognize (custom widget " +
                  "internals, unusual layout code, anything outside __init__). Saving will REPLACE the " +
                  "file's entire contents with what's on this canvas; anything not represented here " +
                  "(and not already inside a `# region UserCode-*` block) will be deleted."
                : "This canvas is EMPTY — nothing importable was found in the file's existing code. " +
                  "Saving will REPLACE the file's entire contents with this empty form; all of its " +
                  "existing code will be deleted, except anything already inside a `# region UserCode-*` block.";
            DialogResult confirm = MessageBox.Show(this,
                body + "\n\nA backup of the current file will be saved as \"" + Path.GetFileName(_pySourcePath) +
                ".helixstudio-backup\" before this happens.\n\nContinue?",
                "Take Over " + Path.GetFileName(_pySourcePath) + "?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) { _adoptDeclined = true; return false; }

            BackupBeforeFirstAdopt();
            _adopted = true;
            _adoptDeclined = false;
            return true;
        }

        // One-time rescue copy, taken right before the first ever overwrite. Never clobbers an
        // existing backup — that would defeat the point if "take over" were ever triggered twice.
        private void BackupBeforeFirstAdopt()
        {
            if (_pySourcePath == null || !File.Exists(_pySourcePath)) return;
            string backupPath = _pySourcePath + ".helixstudio-backup";
            if (File.Exists(backupPath)) return;
            try { File.Copy(_pySourcePath, backupPath); } catch { /* best-effort rescue copy */ }
        }

        private void UpdateSyncStatusUi()
        {
            UpdateSyncCommandVisibility();
            if (_pySourcePath == null)
            {
                _syncBar.Visible = false;   // a preview has nothing to sync and nothing to say
                _syncBar.BackColor = SystemColors.Control;
                _syncStatusLabel.Text = "Preview only — not opened from a project file.";
            }
            else if (_adopted)
            {
                _syncBar.Visible = true;
                _syncBar.BackColor = Color.FromArgb(223, 240, 216);
                _syncStatusLabel.Text = "● Synced — auto-saving to " + _pySourcePath;
            }
            else
            {
                // Nothing at all until there is something to say.
                //
                // This used to be a yellow band reading "Not yet linked - ...", sitting over the
                // canvas from the moment the designer opened. It warned about a write that had not
                // been asked for yet, in language about a link the user had never made, and it was
                // there whether or not they ever intended to touch the file. The question it was
                // warning about is now asked at the first edit, where it means something, so the
                // band has nothing left to report and takes no space.
                _syncBar.Visible = false;
            }
        }

        // Regenerates the Python source for the current model. When linked to a real project file and
        // adopted, this ALSO writes the result straight to that file (plus the .designer.json sidecar)
        // — called after every meaningful edit (add/move/resize/delete/property change/framework
        // switch), the same "the designer surface IS the file" behavior VS2022 has, instead of
        // requiring a manual "Generate Code" + copy-paste step.
        private void GenerateCode() { GenerateCode(false); }

        /// <param name="fromEdit">
        /// True when the user just changed something and expects it in the file.
        ///
        /// <para>This is the moment the take-over question belongs at. A file the designer did not
        /// write is not overwritten until someone says so, and asking here — rather than from a
        /// button on the canvas — asks about a change that exists, at the moment it would be
        /// written, with the same modal confirmation and the same one-time backup as before. A "no"
        /// is remembered for the session so the question is asked once and the edits stay on the
        /// canvas; File - "Start Syncing to This File..." is the way back.</para>
        /// </param>
        private void GenerateCode(bool fromEdit)
        {
            if (fromEdit && !_adopted && !_adoptDeclined && _pySourcePath != null
                && GeneratedSourcePath(_pySourcePath, FrameworkFromCombo()) != null)
            {
                if (AskToAdopt()) UpdateSyncStatusUi();
            }

            _form.Framework = FrameworkFromCombo();
            try
            {
                IGuiCodeEmitter emitter = EmitterFactory.EmitterFor(_form.Framework);
                _codeLabel.Text = "Generated " + emitter.FrameworkName;
                string outputPath = GeneratedSourcePath(_pySourcePath, _form.Framework);
                string existingSource = string.Empty;
                if (outputPath != null && File.Exists(outputPath))
                {
                    try { existingSource = File.ReadAllText(outputPath); }
                    catch { existingSource = string.Empty; }
                }
                string generated = emitter.Emit(_form, existingSource) ?? string.Empty;
                _codeBox.Text = generated.Replace("\n", "\r\n");

                if (outputPath != null && _adopted)
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outputPath, generated);
                    WriteTkinterRuntimeIfNeeded(dir);
                    _repository.Save(_designerJsonPath, _form);
                }
            }
            catch (Exception ex) { _codeBox.Text = "// generation error: " + ex.Message; }
        }

        /// <summary>
        /// Write <c>helix_tk.py</c> beside the form when the form uses a control Tk has no widget
        /// for — see <see cref="TkinterRuntime"/>.
        ///
        /// <para>
        /// Written on every regeneration rather than only when absent, so a stale copy from an
        /// older build cannot outlive the emitter that imports from it: the generated form names
        /// classes by hand, and a runtime missing one of them fails at import with a traceback that
        /// points at the user's form rather than at the file that is actually out of date.
        /// Skipped when it is not needed, so a form built from Tk's own widgets drops nothing extra
        /// into the user's project.
        /// </para>
        /// <para>
        /// Never overwrites a file that is NOT ours: the same banner check that protects a
        /// hand-written form protects a hand-written <c>helix_tk.py</c> a user happens to have.
        /// </para>
        /// </summary>
        private void WriteTkinterRuntimeIfNeeded(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            if (_form.Framework != GuiFramework.Tkinter) return;
            if (!TkinterRuntime.IsRequiredBy(_form)) return;

            string path = Path.Combine(directory, TkinterRuntime.ModuleFileName);
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path);
                    if (existing.Length > 0 && !EmitterCommon.StartsWithAutoGeneratedBanner(
                            existing, EmitterFactory.EmitterFor(GuiFramework.Tkinter).AutoGeneratedMarker)) return;
                }
                File.WriteAllText(path, TkinterRuntime.Source());
            }
            catch { /* generation must not fail over a support file */ }
        }

        /// <summary>Maps a primary document to the designer-owned output. Every framework this
        /// product emits (the Python bindings plus C++ Win32) writes the primary file itself and
        /// protects hand edits with `# region UserCode` blocks rather than a split file, so the
        /// two paths coincide. The parameter stays so a framework that needs a split file later
        /// has a place to say so.</summary>
        public static string GeneratedSourcePath(string primaryPath, GuiFramework framework)
        {
            if (string.IsNullOrEmpty(primaryPath)) return null;
            return primaryPath;
        }

        private GuiFramework FrameworkFromCombo()
        {
            GuiFramework[] frameworks = EmitterFactory.AllFrameworks();
            int index = _frameworkCombo.SelectedIndex;
            return index >= 0 && index < frameworks.Length ? frameworks[index] : GuiFramework.Tkinter;
        }

        private static int ComboIndexForFramework(GuiFramework framework)
        {
            GuiFramework[] frameworks = EmitterFactory.AllFrameworks();
            for (int i = 0; i < frameworks.Length; i++) if (frameworks[i] == framework) return i;
            return 0;
        }

        // Plain Panel with painting/resize flicker suppressed — the canvas now repaints on every
        // MouseMove during a drag, so double-buffering (absent before) matters a lot more than it
        // did for the old static/click-only rendering.
        private sealed class DesignerCanvasControl : Panel
        {
            public DesignerCanvasControl()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
        }

        /// <summary>Test/automation hook: the current generated source.</summary>
        internal string GeneratedCode { get { return _codeBox.Text; } }
        internal int ControlCount { get { return _form.Controls.Count; } }
    }
}
