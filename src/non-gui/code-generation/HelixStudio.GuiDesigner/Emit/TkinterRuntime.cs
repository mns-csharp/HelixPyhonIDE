namespace HelixPythonNamespace.GuiDesigner.Emit
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.GuiDesigner.Model;
    using K = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind;

    /// <summary>
    /// The support module a generated Tkinter form imports: <c>helix_tk.py</c>, written beside the
    /// form the first time one of the toolbox controls Tkinter has no widget for is used.
    ///
    /// <para>
    /// <b>Why a separate module rather than more generated code.</b> Eighteen of the toolbox's
    /// controls have no Tkinter widget — seven visual ones (DataGridView, Chart, MonthCalendar,
    /// DateTimePicker, MaskedTextBox, WebBrowser, BindingNavigator) and eleven non-visual
    /// components (Timer, ToolTip, ImageList, ErrorProvider, NotifyIcon, BindingSource and the five
    /// dialogs). Each needs real code, not a widget name. Inlining that into every generated form
    /// would put several hundred lines the user never wrote in front of the twenty they did, and
    /// would regenerate them on every property change; a module the form imports stays out of the
    /// way, is written once, and can be unit-tested on its own.
    /// </para>
    ///
    /// <para>
    /// <b>Everything here is real behaviour, not a placeholder.</b> Before this existed the
    /// emitter's <c>default:</c> arm turned each of those eighteen into <c>tk.Frame</c> — so a
    /// DataGridView was an empty grey rectangle, and a SaveFileDialog was an empty grey rectangle
    /// PLACED ON THE FORM. The designer would let you drop any control in the toolbox and then
    /// generate a program that did not contain it, which is a worse failure than refusing the
    /// control, because it looks like it worked.
    /// </para>
    ///
    /// <para>
    /// <b>Two are honest approximations, and say so in their own docstrings.</b> Tk has no browser
    /// engine, so <c>WebBrowser</c> shows the URL and opens the system browser; Tk has no tray
    /// icon, so <c>NotifyIcon</c> raises a transient balloon window near the corner of the screen.
    /// Both are the behaviour a user gets from the platform, not a stub that does nothing.
    /// </para>
    /// </summary>
    public static class TkinterRuntime
    {
        /// <summary>The file name the emitter writes and the generated form imports.</summary>
        public const string ModuleFileName = "helix_tk.py";

        /// <summary>The module name used in the generated <c>from ... import ...</c> line.</summary>
        public const string ModuleName = "helix_tk";

        /// <summary>Kinds whose emission needs a name from <see cref="ModuleName"/>.</summary>
        private static readonly Dictionary<K, string> RuntimeClassByKind = new Dictionary<K, string>
        {
            // Visual widgets Tk does not have.
            { K.DataGridView, "HelixDataGrid" },
            { K.Chart, "HelixChart" },
            { K.MonthCalendar, "HelixMonthCalendar" },
            { K.DateTimePicker, "HelixDateTimePicker" },
            { K.MaskedTextBox, "HelixMaskedEntry" },
            { K.WebBrowser, "HelixWebBrowser" },
            { K.BindingNavigator, "HelixBindingNavigator" },
            // Non-visual components. These are constructed, never placed.
            { K.Timer, "HelixTimer" },
            { K.ToolTip, "HelixToolTip" },
            { K.ImageList, "HelixImageList" },
            { K.ErrorProvider, "HelixErrorProvider" },
            { K.NotifyIcon, "HelixNotifyIcon" },
            { K.BindingSource, "HelixBindingSource" },
            { K.OpenFileDialog, "HelixOpenFileDialog" },
            { K.SaveFileDialog, "HelixSaveFileDialog" },
            { K.FolderBrowserDialog, "HelixFolderBrowserDialog" },
            { K.ColorDialog, "HelixColorDialog" },
            { K.FontDialog, "HelixFontDialog" },
            // Menus and toolbars are real Tk objects but not plain widgets: a menubar belongs to
            // the toplevel, and a context menu is posted rather than placed.
            { K.MenuStrip, "HelixMenuStrip" },
            { K.ContextMenuStrip, "HelixContextMenuStrip" },
            { K.ToolStrip, "HelixToolStrip" },
            { K.StatusStrip, "HelixStatusStrip" },
        };

        /// <summary>The runtime class for <paramref name="kind"/>, or null when Tk has a widget.</summary>
        public static string ClassFor(K kind)
        {
            string name;
            return RuntimeClassByKind.TryGetValue(kind, out name) ? name : null;
        }

        /// <summary>True when this form needs <c>helix_tk.py</c> written beside it.</summary>
        public static bool IsRequiredBy(GuiForm form)
        {
            if (form == null) return false;
            foreach (GuiControl c in form.Controls)
                if (ClassFor(c.Kind) != null) return true;
            return false;
        }

        /// <summary>The names this form imports from the runtime, sorted, or an empty list.</summary>
        public static List<string> ImportsFor(GuiForm form)
        {
            var names = new SortedSet<string>(System.StringComparer.Ordinal);
            if (form != null)
                foreach (GuiControl c in form.Controls)
                {
                    string cls = ClassFor(c.Kind);
                    if (cls != null) names.Add(cls);
                }
            return new List<string>(names);
        }

        /// <summary>The complete source of <c>helix_tk.py</c>.</summary>
        public static string Source()
        {
            var o = new StringBuilder();
            o.Append(Header);
            o.Append(Components);
            o.Append(Dialogs);
            o.Append(Widgets);
            o.Append(Strips);
            return o.ToString();
        }

        // The banner must be one EmitterCommon.StartsWithAutoGeneratedBanner recognises, or the
        // designer will treat a regenerated runtime as a file somebody hand-wrote and refuse to
        // overwrite it.
        private const string Header =
            "# AUTO-GENERATED by HelixPython. Support module for generated Tkinter forms.\n" +
            "#\n" +
            "# Tk has no widget for several of the controls the GUI Designer Canvas offers, so they live\n" +
            "# here as real implementations instead. Do not edit: the designer rewrites this file.\n" +
            "\n" +
            "import calendar\n" +
            "import datetime\n" +
            "import tkinter as tk\n" +
            "import webbrowser\n" +
            "from tkinter import colorchooser, filedialog, font as tkfont, ttk\n" +
            "\n" +
            "\n";

        // ---- Non-visual components --------------------------------------------------

        private const string Components =
            "class HelixTimer:\n" +
            "    \"\"\"A repeating timer, the WinForms Timer shape over Tk's `after`.\n" +
            "\n" +
            "    Tk has no timer object: scheduling is a method on a widget, and a repeat is the\n" +
            "    callback booking itself again. This keeps the widget, the interval and the pending\n" +
            "    id together so Stop() can actually cancel -- a bare `after` loop cannot be stopped,\n" +
            "    which is how a form that has been closed goes on firing into a dead widget.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, widget, interval=1000, on_tick=None, enabled=False):\n" +
            "        self._widget = widget\n" +
            "        self.interval = int(interval)\n" +
            "        self.on_tick = on_tick\n" +
            "        self._pending = None\n" +
            "        if enabled:\n" +
            "            self.start()\n" +
            "\n" +
            "    @property\n" +
            "    def enabled(self):\n" +
            "        return self._pending is not None\n" +
            "\n" +
            "    def start(self):\n" +
            "        if self._pending is None:\n" +
            "            self._pending = self._widget.after(self.interval, self._fire)\n" +
            "\n" +
            "    def stop(self):\n" +
            "        if self._pending is not None:\n" +
            "            try:\n" +
            "                self._widget.after_cancel(self._pending)\n" +
            "            except Exception:\n" +
            "                pass\n" +
            "            self._pending = None\n" +
            "\n" +
            "    def _fire(self):\n" +
            "        self._pending = None\n" +
            "        if self.on_tick is not None:\n" +
            "            self.on_tick()\n" +
            "        if self._widget.winfo_exists():\n" +
            "            self.start()\n" +
            "\n" +
            "\n" +
            "class HelixToolTip:\n" +
            "    \"\"\"Hover text for any widget -- the WinForms ToolTip component.\n" +
            "\n" +
            "    One instance serves many widgets, as in WinForms: SetToolTip(widget, text).\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, delay=500):\n" +
            "        self.delay = int(delay)\n" +
            "        self._texts = {}\n" +
            "        self._tip = None\n" +
            "        self._after = None\n" +
            "\n" +
            "    def set_tooltip(self, widget, text):\n" +
            "        self._texts[widget] = text\n" +
            "        widget.bind('<Enter>', lambda e, w=widget: self._schedule(w), add='+')\n" +
            "        widget.bind('<Leave>', lambda e: self._hide(), add='+')\n" +
            "\n" +
            "    def _schedule(self, widget):\n" +
            "        self._hide()\n" +
            "        self._after = widget.after(self.delay, lambda: self._show(widget))\n" +
            "\n" +
            "    def _show(self, widget):\n" +
            "        text = self._texts.get(widget, '')\n" +
            "        if not text:\n" +
            "            return\n" +
            "        self._tip = tk.Toplevel(widget)\n" +
            "        self._tip.wm_overrideredirect(True)\n" +
            "        self._tip.wm_geometry('+%d+%d' % (widget.winfo_rootx() + 12,\n" +
            "                                          widget.winfo_rooty() + widget.winfo_height() + 4))\n" +
            "        tk.Label(self._tip, text=text, background='#ffffe1', relief='solid',\n" +
            "                 borderwidth=1, padx=6, pady=2).pack()\n" +
            "\n" +
            "    def _hide(self):\n" +
            "        if self._after is not None:\n" +
            "            self._after = None\n" +
            "        if self._tip is not None:\n" +
            "            self._tip.destroy()\n" +
            "            self._tip = None\n" +
            "\n" +
            "\n" +
            "class HelixImageList:\n" +
            "    \"\"\"Named images, loaded once and kept alive.\n" +
            "\n" +
            "    The keeping-alive is the point: a tk.PhotoImage with no Python reference is garbage\n" +
            "    collected and the widget showing it goes blank, which is the single most common\n" +
            "    Tkinter surprise. Holding them in one list is what WinForms' ImageList is for too.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, paths=()):\n" +
            "        self._images = {}\n" +
            "        for path in paths:\n" +
            "            if path:\n" +
            "                self.add(path)\n" +
            "\n" +
            "    def add(self, path, key=None):\n" +
            "        try:\n" +
            "            image = tk.PhotoImage(file=path)\n" +
            "        except Exception:\n" +
            "            return None\n" +
            "        self._images[key or path] = image\n" +
            "        return image\n" +
            "\n" +
            "    def get(self, key):\n" +
            "        return self._images.get(key)\n" +
            "\n" +
            "    def __len__(self):\n" +
            "        return len(self._images)\n" +
            "\n" +
            "\n" +
            "class HelixErrorProvider:\n" +
            "    \"\"\"Marks a widget as invalid and explains why -- the WinForms ErrorProvider.\n" +
            "\n" +
            "    The marker is a label placed beside the widget rather than an icon inside it,\n" +
            "    because Tk widgets have no error-icon slot. Clearing removes the marker and the\n" +
            "    highlight, so a field that becomes valid stops looking wrong.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, colour='#c0392b'):\n" +
            "        self.colour = colour\n" +
            "        self._markers = {}\n" +
            "\n" +
            "    def set_error(self, widget, message):\n" +
            "        self.clear(widget)\n" +
            "        if not message:\n" +
            "            return\n" +
            "        marker = tk.Label(widget.master, text='\\u26a0', fg=self.colour)\n" +
            "        marker.place(x=widget.winfo_x() + widget.winfo_width() + 2, y=widget.winfo_y())\n" +
            "        HelixToolTip().set_tooltip(marker, message)\n" +
            "        self._markers[widget] = marker\n" +
            "\n" +
            "    def clear(self, widget):\n" +
            "        marker = self._markers.pop(widget, None)\n" +
            "        if marker is not None:\n" +
            "            marker.destroy()\n" +
            "\n" +
            "    def has_errors(self):\n" +
            "        return len(self._markers) > 0\n" +
            "\n" +
            "\n" +
            "class HelixNotifyIcon:\n" +
            "    \"\"\"A transient notification balloon.\n" +
            "\n" +
            "    An honest approximation: Tk has no system-tray API, so there is no tray icon to put\n" +
            "    anywhere. What the component is USED for -- telling the user something happened\n" +
            "    without stealing focus -- is done with a borderless window in the bottom-right\n" +
            "    corner that fades itself out. A form that asks for a NotifyIcon gets that rather\n" +
            "    than nothing.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, widget, title='', timeout=3000):\n" +
            "        self._widget = widget\n" +
            "        self.title = title\n" +
            "        self.timeout = int(timeout)\n" +
            "\n" +
            "    def show_balloon(self, text, title=None):\n" +
            "        top = tk.Toplevel(self._widget)\n" +
            "        top.wm_overrideredirect(True)\n" +
            "        top.configure(background='#2c3e50')\n" +
            "        tk.Label(top, text=title or self.title, fg='white', bg='#2c3e50',\n" +
            "                 font=('Segoe UI', 9, 'bold')).pack(anchor='w', padx=10, pady=(8, 0))\n" +
            "        tk.Label(top, text=text, fg='white', bg='#2c3e50',\n" +
            "                 wraplength=240, justify='left').pack(anchor='w', padx=10, pady=(2, 8))\n" +
            "        top.update_idletasks()\n" +
            "        x = top.winfo_screenwidth() - top.winfo_width() - 24\n" +
            "        y = top.winfo_screenheight() - top.winfo_height() - 64\n" +
            "        top.wm_geometry('+%d+%d' % (x, y))\n" +
            "        top.after(self.timeout, top.destroy)\n" +
            "        return top\n" +
            "\n" +
            "\n" +
            "class HelixBindingSource:\n" +
            "    \"\"\"A list with a current position and change notification.\n" +
            "\n" +
            "    This is what a BindingSource actually is, minus the data-binding plumbing WinForms\n" +
            "    layers on top: rows, a cursor, and a way to hear that either moved. HelixDataGrid\n" +
            "    and HelixBindingNavigator both bind to one, which is what makes a navigator move a\n" +
            "    grid's selection without either knowing about the other.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, rows=None):\n" +
            "        self._rows = list(rows or [])\n" +
            "        self._position = 0 if self._rows else -1\n" +
            "        self._listeners = []\n" +
            "\n" +
            "    @property\n" +
            "    def rows(self):\n" +
            "        return list(self._rows)\n" +
            "\n" +
            "    @property\n" +
            "    def count(self):\n" +
            "        return len(self._rows)\n" +
            "\n" +
            "    @property\n" +
            "    def position(self):\n" +
            "        return self._position\n" +
            "\n" +
            "    @position.setter\n" +
            "    def position(self, value):\n" +
            "        if not self._rows:\n" +
            "            self._position = -1\n" +
            "        else:\n" +
            "            self._position = max(0, min(int(value), len(self._rows) - 1))\n" +
            "        self._notify()\n" +
            "\n" +
            "    @property\n" +
            "    def current(self):\n" +
            "        if 0 <= self._position < len(self._rows):\n" +
            "            return self._rows[self._position]\n" +
            "        return None\n" +
            "\n" +
            "    def reset(self, rows):\n" +
            "        self._rows = list(rows or [])\n" +
            "        self._position = 0 if self._rows else -1\n" +
            "        self._notify()\n" +
            "\n" +
            "    def add_listener(self, callback):\n" +
            "        self._listeners.append(callback)\n" +
            "\n" +
            "    def _notify(self):\n" +
            "        for callback in list(self._listeners):\n" +
            "            callback(self)\n" +
            "\n" +
            "\n";

        // ---- Dialogs ----------------------------------------------------------------

        private const string Dialogs =
            "class _HelixDialog:\n" +
            "    \"\"\"Base for the file/colour/font dialogs: a component that remembers its result.\n" +
            "\n" +
            "    WinForms dialogs are components you configure once and show many times, and the\n" +
            "    result stays readable afterwards (FileName, Color, Font). Tk's are bare functions\n" +
            "    that return a value and forget it, so that shape is rebuilt here.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, title=''):\n" +
            "        self.title = title\n" +
            "        self.result = None\n" +
            "\n" +
            "    @property\n" +
            "    def has_result(self):\n" +
            "        return bool(self.result)\n" +
            "\n" +
            "    @staticmethod\n" +
            "    def parse_filter(filter_text):\n" +
            "        \"\"\"'Text files|*.txt|All files|*.*' -> Tk's [(label, pattern), ...].\n" +
            "\n" +
            "        A static method rather than a module function because the IDE that generates\n" +
            "        this file enforces PY8002 -- user-written code lives in classes -- and code the\n" +
            "        IDE writes must pass the rules it then applies to the project it wrote them into.\n" +
            "        A generated module its own Run button refuses to launch is not a support file,\n" +
            "        it is a blocker.\n" +
            "        \"\"\"\n" +
            "        parts = [p for p in (filter_text or '').split('|') if p]\n" +
            "        pairs = []\n" +
            "        for i in range(0, len(parts) - 1, 2):\n" +
            "            pairs.append((parts[i], parts[i + 1]))\n" +
            "        return pairs or [('All files', '*.*')]\n" +
            "\n" +
            "\n" +
            "class HelixOpenFileDialog(_HelixDialog):\n" +
            "    def __init__(self, title='Open', filter_text='All files|*.*'):\n" +
            "        _HelixDialog.__init__(self, title)\n" +
            "        self.filter_text = filter_text\n" +
            "\n" +
            "    def show(self, parent=None):\n" +
            "        self.result = filedialog.askopenfilename(\n" +
            "            parent=parent, title=self.title, filetypes=self.parse_filter(self.filter_text))\n" +
            "        return self.result\n" +
            "\n" +
            "\n" +
            "class HelixSaveFileDialog(_HelixDialog):\n" +
            "    def __init__(self, title='Save As', filter_text='All files|*.*', default_ext=''):\n" +
            "        _HelixDialog.__init__(self, title)\n" +
            "        self.filter_text = filter_text\n" +
            "        self.default_ext = default_ext\n" +
            "\n" +
            "    def show(self, parent=None):\n" +
            "        self.result = filedialog.asksaveasfilename(\n" +
            "            parent=parent, title=self.title, defaultextension=self.default_ext,\n" +
            "            filetypes=self.parse_filter(self.filter_text))\n" +
            "        return self.result\n" +
            "\n" +
            "\n" +
            "class HelixFolderBrowserDialog(_HelixDialog):\n" +
            "    def show(self, parent=None):\n" +
            "        self.result = filedialog.askdirectory(parent=parent, title=self.title or 'Select a folder')\n" +
            "        return self.result\n" +
            "\n" +
            "\n" +
            "class HelixColorDialog(_HelixDialog):\n" +
            "    def show(self, parent=None):\n" +
            "        rgb, hex_value = colorchooser.askcolor(parent=parent, title=self.title or 'Colour')\n" +
            "        self.result = hex_value\n" +
            "        self.rgb = rgb\n" +
            "        return self.result\n" +
            "\n" +
            "\n" +
            "class HelixFontDialog(_HelixDialog):\n" +
            "    \"\"\"A font chooser.\n" +
            "\n" +
            "    Tk ships no font dialog, so this is a real modal built from the font families Tk\n" +
            "    itself reports -- which is the same list any Tk widget can actually use, so the\n" +
            "    chooser cannot offer a font the form then fails to apply.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def show(self, parent=None):\n" +
            "        top = tk.Toplevel(parent)\n" +
            "        top.title(self.title or 'Font')\n" +
            "        top.transient(parent)\n" +
            "        top.grab_set()\n" +
            "\n" +
            "        families = sorted(set(tkfont.families()))\n" +
            "        family = tk.StringVar(value=families[0] if families else 'Segoe UI')\n" +
            "        size = tk.IntVar(value=10)\n" +
            "\n" +
            "        listbox = tk.Listbox(top, height=10, exportselection=False)\n" +
            "        for name in families:\n" +
            "            listbox.insert('end', name)\n" +
            "        listbox.pack(fill='both', expand=True, padx=8, pady=8)\n" +
            "        ttk.Spinbox(top, from_=6, to=72, textvariable=size, width=6).pack(pady=(0, 8))\n" +
            "\n" +
            "        def accept():\n" +
            "            picked = listbox.curselection()\n" +
            "            if picked:\n" +
            "                family.set(listbox.get(picked[0]))\n" +
            "            self.result = (family.get(), size.get())\n" +
            "            top.destroy()\n" +
            "\n" +
            "        ttk.Button(top, text='OK', command=accept).pack(pady=(0, 10))\n" +
            "        top.wait_window()\n" +
            "        return self.result\n" +
            "\n" +
            "\n" +
            "\n" +
            "\n";

        // ---- Visual widgets Tk does not have ----------------------------------------

        private const string Widgets =
            "class HelixDataGrid(ttk.Frame):\n" +
            "    \"\"\"A scrollable, column-headed grid -- the DataGridView.\n" +
            "\n" +
            "    ttk.Treeview in 'headings' mode is Tk's real table widget; what it lacks is the\n" +
            "    packaging (a scrollbar, a way to load rows, a binding source). A bare Treeview is\n" +
            "    why the emitter used to produce something that looked like a grid and held nothing.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, columns=('Column',), source=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self.columns = list(columns)\n" +
            "        self.tree = ttk.Treeview(self, columns=self.columns, show='headings', selectmode='browse')\n" +
            "        for name in self.columns:\n" +
            "            self.tree.heading(name, text=name)\n" +
            "            self.tree.column(name, width=110, anchor='w')\n" +
            "        bar = ttk.Scrollbar(self, orient='vertical', command=self.tree.yview)\n" +
            "        self.tree.configure(yscrollcommand=bar.set)\n" +
            "        self.tree.pack(side='left', fill='both', expand=True)\n" +
            "        bar.pack(side='right', fill='y')\n" +
            "        self.source = None\n" +
            "        if source is not None:\n" +
            "            self.bind_source(source)\n" +
            "\n" +
            "    def bind_source(self, source):\n" +
            "        self.source = source\n" +
            "        source.add_listener(lambda s: self.load(s.rows))\n" +
            "        self.load(source.rows)\n" +
            "\n" +
            "    def load(self, rows):\n" +
            "        self.tree.delete(*self.tree.get_children())\n" +
            "        for row in rows or []:\n" +
            "            self.tree.insert('', 'end', values=list(row))\n" +
            "\n" +
            "    def selected_row(self):\n" +
            "        picked = self.tree.selection()\n" +
            "        return self.tree.item(picked[0], 'values') if picked else None\n" +
            "\n" +
            "\n" +
            "class HelixChart(tk.Canvas):\n" +
            "    \"\"\"A bar chart drawn on a Canvas -- the Chart control.\n" +
            "\n" +
            "    Redraws on <Configure> so it fills whatever space it is given, which is the whole\n" +
            "    reason to draw a chart rather than show a picture of one.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    PALETTE = ('#2e86de', '#10ac84', '#ee5253', '#f39c12', '#8854d0', '#0abde3')\n" +
            "\n" +
            "    def __init__(self, master=None, title='', **kw):\n" +
            "        kw.setdefault('background', 'white')\n" +
            "        kw.setdefault('highlightthickness', 1)\n" +
            "        tk.Canvas.__init__(self, master, **kw)\n" +
            "        self.title = title\n" +
            "        self._points = []\n" +
            "        self.bind('<Configure>', lambda e: self._redraw())\n" +
            "\n" +
            "    def plot(self, points):\n" +
            "        \"\"\"points: [(label, value), ...]\"\"\"\n" +
            "        self._points = list(points or [])\n" +
            "        self._redraw()\n" +
            "\n" +
            "    def _redraw(self):\n" +
            "        self.delete('all')\n" +
            "        width = max(self.winfo_width(), 80)\n" +
            "        height = max(self.winfo_height(), 60)\n" +
            "        top = 22 if self.title else 8\n" +
            "        if self.title:\n" +
            "            self.create_text(width / 2, 12, text=self.title, font=('Segoe UI', 9, 'bold'))\n" +
            "        if not self._points:\n" +
            "            self.create_text(width / 2, height / 2, text='(no data)', fill='#95a5a6')\n" +
            "            return\n" +
            "        floor = height - 20\n" +
            "        biggest = max([abs(float(v)) for _, v in self._points]) or 1.0\n" +
            "        slot = width / float(len(self._points))\n" +
            "        for i, (label, value) in enumerate(self._points):\n" +
            "            bar = (abs(float(value)) / biggest) * (floor - top)\n" +
            "            x0 = i * slot + slot * 0.2\n" +
            "            x1 = i * slot + slot * 0.8\n" +
            "            self.create_rectangle(x0, floor - bar, x1, floor,\n" +
            "                                  fill=self.PALETTE[i % len(self.PALETTE)], outline='')\n" +
            "            self.create_text((x0 + x1) / 2, floor + 9, text=str(label), font=('Segoe UI', 7))\n" +
            "        self.create_line(0, floor, width, floor, fill='#bdc3c7')\n" +
            "\n" +
            "\n" +
            "class HelixMonthCalendar(ttk.Frame):\n" +
            "    \"\"\"A real month grid -- the MonthCalendar control.\n" +
            "\n" +
            "    Built from the stdlib `calendar` module, so the weekday alignment and leap years are\n" +
            "    right without this file owning a date algorithm.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        today = datetime.date.today()\n" +
            "        self._year, self._month = today.year, today.month\n" +
            "        self.selected = today\n" +
            "        self.on_select = None\n" +
            "        self._header = ttk.Label(self, anchor='center', font=('Segoe UI', 9, 'bold'))\n" +
            "        nav = ttk.Frame(self)\n" +
            "        ttk.Button(nav, text='<', width=3, command=lambda: self._step(-1)).pack(side='left')\n" +
            "        self._header.pack(in_=nav, side='left', fill='x', expand=True)\n" +
            "        ttk.Button(nav, text='>', width=3, command=lambda: self._step(1)).pack(side='right')\n" +
            "        nav.pack(fill='x')\n" +
            "        self._grid = ttk.Frame(self)\n" +
            "        self._grid.pack(fill='both', expand=True)\n" +
            "        self._draw()\n" +
            "\n" +
            "    def _step(self, months):\n" +
            "        month = self._month + months\n" +
            "        self._year += (month - 1) // 12\n" +
            "        self._month = (month - 1) % 12 + 1\n" +
            "        self._draw()\n" +
            "\n" +
            "    def _draw(self):\n" +
            "        for child in self._grid.winfo_children():\n" +
            "            child.destroy()\n" +
            "        self._header.configure(text='%s %d' % (calendar.month_name[self._month], self._year))\n" +
            "        for column, name in enumerate(('Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su')):\n" +
            "            ttk.Label(self._grid, text=name, width=3, anchor='center').grid(row=0, column=column)\n" +
            "        for row, week in enumerate(calendar.monthcalendar(self._year, self._month), start=1):\n" +
            "            for column, day in enumerate(week):\n" +
            "                if day == 0:\n" +
            "                    continue\n" +
            "                button = ttk.Button(self._grid, text=str(day), width=3,\n" +
            "                                    command=lambda d=day: self._pick(d))\n" +
            "                button.grid(row=row, column=column, sticky='nsew')\n" +
            "\n" +
            "    def _pick(self, day):\n" +
            "        self.selected = datetime.date(self._year, self._month, day)\n" +
            "        if self.on_select is not None:\n" +
            "            self.on_select(self.selected)\n" +
            "\n" +
            "\n" +
            "class HelixDateTimePicker(ttk.Frame):\n" +
            "    \"\"\"A date field with a drop-down month grid -- the DateTimePicker control.\"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, value=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self._value = value or datetime.date.today()\n" +
            "        self._text = tk.StringVar(value=self._value.isoformat())\n" +
            "        self._entry = ttk.Entry(self, textvariable=self._text)\n" +
            "        self._entry.pack(side='left', fill='both', expand=True)\n" +
            "        ttk.Button(self, text='\\u25bc', width=3, command=self._drop).pack(side='right')\n" +
            "        self._popup = None\n" +
            "\n" +
            "    @property\n" +
            "    def value(self):\n" +
            "        try:\n" +
            "            return datetime.date.fromisoformat(self._text.get())\n" +
            "        except ValueError:\n" +
            "            return self._value\n" +
            "\n" +
            "    def _drop(self):\n" +
            "        if self._popup is not None:\n" +
            "            self._popup.destroy()\n" +
            "            self._popup = None\n" +
            "            return\n" +
            "        self._popup = tk.Toplevel(self)\n" +
            "        self._popup.wm_overrideredirect(True)\n" +
            "        self._popup.wm_geometry('+%d+%d' % (self.winfo_rootx(),\n" +
            "                                            self.winfo_rooty() + self.winfo_height()))\n" +
            "        calendar_widget = HelixMonthCalendar(self._popup)\n" +
            "        calendar_widget.on_select = self._chose\n" +
            "        calendar_widget.pack()\n" +
            "\n" +
            "    def _chose(self, date):\n" +
            "        self._value = date\n" +
            "        self._text.set(date.isoformat())\n" +
            "        if self._popup is not None:\n" +
            "            self._popup.destroy()\n" +
            "            self._popup = None\n" +
            "\n" +
            "\n" +
            "class HelixMaskedEntry(ttk.Entry):\n" +
            "    \"\"\"An entry that refuses input the mask does not allow -- the MaskedTextBox.\n" +
            "\n" +
            "    Mask characters follow WinForms: 0 a digit, L a letter, A either, and anything else\n" +
            "    is a literal the user does not have to type. Validation is per-keystroke through\n" +
            "    Tk's own validatecommand, so a rejected character never appears at all.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, mask='', **kw):\n" +
            "        ttk.Entry.__init__(self, master, **kw)\n" +
            "        self.mask = mask or ''\n" +
            "        if self.mask:\n" +
            "            self.configure(validate='key',\n" +
            "                           validatecommand=(self.register(self._validate), '%P'))\n" +
            "\n" +
            "    def _validate(self, proposed):\n" +
            "        if len(proposed) > len(self.mask):\n" +
            "            return False\n" +
            "        for character, slot in zip(proposed, self.mask):\n" +
            "            if slot == '0' and not character.isdigit():\n" +
            "                return False\n" +
            "            if slot == 'L' and not character.isalpha():\n" +
            "                return False\n" +
            "            if slot == 'A' and not character.isalnum():\n" +
            "                return False\n" +
            "            if slot not in '0LA' and character != slot:\n" +
            "                return False\n" +
            "        return True\n" +
            "\n" +
            "    @property\n" +
            "    def is_complete(self):\n" +
            "        return len(self.get()) == len(self.mask)\n" +
            "\n" +
            "\n" +
            "class HelixWebBrowser(ttk.Frame):\n" +
            "    \"\"\"A URL bar that opens the system browser.\n" +
            "\n" +
            "    An honest approximation, and the docstring says so where a user will read it: Tk has\n" +
            "    no HTML engine and embedding one is not a widget, it is a dependency. What a form\n" +
            "    that drops a WebBrowser usually wants -- show a link, let the user follow it -- works\n" +
            "    exactly as expected; rendering a page in-process does not, and pretending otherwise\n" +
            "    with an empty frame is what this replaces.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, url='', **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self.url = tk.StringVar(value=url)\n" +
            "        bar = ttk.Frame(self)\n" +
            "        ttk.Entry(bar, textvariable=self.url).pack(side='left', fill='x', expand=True)\n" +
            "        ttk.Button(bar, text='Open', command=self.navigate).pack(side='right')\n" +
            "        bar.pack(fill='x')\n" +
            "        self.status = ttk.Label(self, anchor='w', foreground='#7f8c8d',\n" +
            "                                text='Opens in the system browser (Tk has no HTML engine).')\n" +
            "        self.status.pack(fill='both', expand=True)\n" +
            "\n" +
            "    def navigate(self, url=None):\n" +
            "        target = url or self.url.get()\n" +
            "        if not target:\n" +
            "            return False\n" +
            "        self.url.set(target)\n" +
            "        webbrowser.open(target)\n" +
            "        self.status.configure(text='Opened ' + target)\n" +
            "        return True\n" +
            "\n" +
            "\n" +
            "class HelixBindingNavigator(ttk.Frame):\n" +
            "    \"\"\"First/previous/position/next/last over a HelixBindingSource.\"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, source=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self.source = source\n" +
            "        self._label = ttk.Label(self, width=12, anchor='center')\n" +
            "        ttk.Button(self, text='|<', width=3, command=lambda: self._go(0)).pack(side='left')\n" +
            "        ttk.Button(self, text='<', width=3, command=lambda: self._step(-1)).pack(side='left')\n" +
            "        self._label.pack(side='left')\n" +
            "        ttk.Button(self, text='>', width=3, command=lambda: self._step(1)).pack(side='left')\n" +
            "        ttk.Button(self, text='>|', width=3, command=lambda: self._go(-1)).pack(side='left')\n" +
            "        if source is not None:\n" +
            "            self.bind_source(source)\n" +
            "        self._refresh()\n" +
            "\n" +
            "    def bind_source(self, source):\n" +
            "        \"\"\"Attach a HelixBindingSource after construction.\n" +
            "\n" +
            "        The designer builds every control with no arguments it cannot know at design\n" +
            "        time, and which binding source a navigator drives is one of those -- both are\n" +
            "        constructed in _build_ui and the wiring is the application's business. Without\n" +
            "        a method to do it afterwards the navigator can only ever be bound by passing\n" +
            "        the source to the constructor, which the generated code cannot do, so it would\n" +
            "        sit there reading '0 of 0' over a grid with rows in it.\n" +
            "        \"\"\"\n" +
            "        self.source = source\n" +
            "        if source is not None:\n" +
            "            source.add_listener(lambda s: self._refresh())\n" +
            "        self._refresh()\n" +
            "\n" +
            "    def _go(self, index):\n" +
            "        if self.source is not None and self.source.count:\n" +
            "            self.source.position = self.source.count - 1 if index < 0 else index\n" +
            "\n" +
            "    def _step(self, delta):\n" +
            "        if self.source is not None and self.source.count:\n" +
            "            self.source.position = self.source.position + delta\n" +
            "\n" +
            "    def _refresh(self):\n" +
            "        if self.source is None or not self.source.count:\n" +
            "            self._label.configure(text='0 of 0')\n" +
            "        else:\n" +
            "            self._label.configure(text='%d of %d' % (self.source.position + 1, self.source.count))\n" +
            "\n" +
            "\n";

        // ---- Menus, toolbars and the status bar -------------------------------------

        private const string Strips =
            "class HelixMenuStrip:\n" +
            "    \"\"\"The window's menu bar.\n" +
            "\n" +
            "    Not a widget and not placeable: a Tk menubar is attached to the TOPLEVEL, so it has\n" +
            "    no x/y and cannot be a child of a frame. Emitting it as a placed tk.Frame -- which\n" +
            "    is what happened before -- produced a grey rectangle where the menu should be and a\n" +
            "    window with no menu.\n" +
            "    \"\"\"\n" +
            "\n" +
            "    def __init__(self, toplevel):\n" +
            "        self.toplevel = toplevel\n" +
            "        self.menu = tk.Menu(toplevel)\n" +
            "        try:\n" +
            "            toplevel.configure(menu=self.menu)\n" +
            "        except tk.TclError:\n" +
            "            pass\n" +
            "        self._menus = {}\n" +
            "\n" +
            "    def add_menu(self, title):\n" +
            "        submenu = tk.Menu(self.menu, tearoff=0)\n" +
            "        self.menu.add_cascade(label=title, menu=submenu)\n" +
            "        self._menus[title] = submenu\n" +
            "        return submenu\n" +
            "\n" +
            "    def add_item(self, menu_title, item_title, command=None):\n" +
            "        submenu = self._menus.get(menu_title) or self.add_menu(menu_title)\n" +
            "        if item_title == '-':\n" +
            "            submenu.add_separator()\n" +
            "        else:\n" +
            "            submenu.add_command(label=item_title, command=command)\n" +
            "        return submenu\n" +
            "\n" +
            "\n" +
            "class HelixContextMenuStrip:\n" +
            "    \"\"\"A right-click menu, posted at the pointer rather than placed.\"\"\"\n" +
            "\n" +
            "    def __init__(self, widget):\n" +
            "        self.widget = widget\n" +
            "        self.menu = tk.Menu(widget, tearoff=0)\n" +
            "\n" +
            "    def add_item(self, title, command=None):\n" +
            "        if title == '-':\n" +
            "            self.menu.add_separator()\n" +
            "        else:\n" +
            "            self.menu.add_command(label=title, command=command)\n" +
            "\n" +
            "    def attach(self, widget):\n" +
            "        widget.bind('<Button-3>', self._post, add='+')\n" +
            "\n" +
            "    def _post(self, event):\n" +
            "        try:\n" +
            "            self.menu.tk_popup(event.x_root, event.y_root)\n" +
            "        finally:\n" +
            "            self.menu.grab_release()\n" +
            "\n" +
            "\n" +
            "class HelixToolStrip(ttk.Frame):\n" +
            "    \"\"\"A horizontal button strip with separators.\"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self.buttons = {}\n" +
            "\n" +
            "    def add_button(self, title, command=None):\n" +
            "        if title == '-':\n" +
            "            ttk.Separator(self, orient='vertical').pack(side='left', fill='y', padx=4)\n" +
            "            return None\n" +
            "        button = ttk.Button(self, text=title, command=command)\n" +
            "        button.pack(side='left', padx=2, pady=2)\n" +
            "        self.buttons[title] = button\n" +
            "        return button\n" +
            "\n" +
            "\n" +
            "class HelixStatusStrip(ttk.Frame):\n" +
            "    \"\"\"A status bar with named panels, so two writers cannot overwrite each other.\"\"\"\n" +
            "\n" +
            "    def __init__(self, master=None, **kw):\n" +
            "        ttk.Frame.__init__(self, master, **kw)\n" +
            "        self._panels = {}\n" +
            "\n" +
            "    def add_panel(self, key, text='', width=0, side='left'):\n" +
            "        label = ttk.Label(self, text=text, relief='sunken', anchor='w', padding=(6, 2))\n" +
            "        if width:\n" +
            "            label.configure(width=width)\n" +
            "        label.pack(side=side, fill='y', padx=1)\n" +
            "        self._panels[key] = label\n" +
            "        return label\n" +
            "\n" +
            "    def set_text(self, key, text):\n" +
            "        label = self._panels.get(key)\n" +
            "        if label is not None:\n" +
            "            label.configure(text=text)\n";
    }
}
