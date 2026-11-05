# Write and deploy HelixPython add-ins

An add-in is a .NET Framework 4.7.2 class library that the IDE loads at startup. It adds menu
items, tabs, file types and Solution Explorer commands to the IDE. It talks to the IDE only
through one small public SDK assembly, `HelixStudio.AddIns.dll`.

The repository includes the add-in SDK and the built-in GUI Designer. Use the public SDK to create
independent extensions.

## The contract

The SDK is `src/gui/addins/HelixStudio.AddIns/` (namespace `HelixPythonNamespace.AddIns`).

### IHelixAddIn: implement the interface

```csharp
public interface IHelixAddIn
{
    string Id { get; }                  // stable key, for example "org.example.hello"
    string DisplayName { get; }         // the name of your submenu under Add-in
    void Initialize(IAddInHost host);   // register everything here
    void Shutdown();                    // the IDE is closing, or the user removed you
}
```

- Your class must be `public`, not `abstract`, and have a parameterless constructor.
- `Id` must never change between versions. The IDE keys your settings on it, and two add-ins
  with the same `Id` cannot both load.
- Keep the constructor cheap and free of side effects. Preferences ▸ Install Add-in creates your
  class once, from a throw-away copy, only to read `Id` and `DisplayName`.
- An exception from `Initialize` does not stop the IDE. It is written to the Output pane, and
  everything you registered before the exception is taken away again.

### IAddInHost: use the host

Each add-in gets its own host. The host records everything you register, so the IDE can undo
all of it when the user removes your add-in. These are the members you will use most:

```csharp
// Menus: items go under Add-in ▸ <your DisplayName>.
// "visible" is checked each time the menu opens; null means always visible.
void AddMenuItem(string caption, Action run, Func<bool> visible);
void AddMenuItem(string caption, string toolTip, Action run, Func<bool> visible);

// Tabs in the main tab strip. Registering does not show the tab; ShowTab does.
void RegisterTab(string key, TabPage page, bool solutionScoped, AddInTabCallbacks callbacks);
void ShowTab(string key);
void CloseTab(string key);

// File types: File ▸ Open, shell open and Recent files call "open" for a path
// that ends with fileSuffix (case is ignored), for example ".cgmc.json".
void RegisterFileOpener(string fileSuffix, Action<string> open);

// Canvas files (*.canvas, the shared binary design format): File ▸ Open, the Solution
// Explorer and shell open call "open" for a file whose header names this kind, for
// example "cg-mc". The IDE checks the whole file first; a damaged file never arrives.
// A kind is 1 to 32 characters of a-z, 0-9 and '-'; any other kind is refused with
// one Output line and never registered.
void RegisterCanvasKind(string kind, Action<string> open);

// Solution Explorer right-click items on files.
void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo, Action<string> run);
void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo,
    Func<string, string> disabledReason, Action<string> run);

// Output pane and status bar.
void WriteOutputLine(string line);
void ShowOutput();
void SetStatus(string text);
void SetStatus(string text, System.Drawing.Color color);

// Editor and solution.
string SolutionDirectory { get; }        // null when no solution is open
bool IsPythonSolutionOpen { get; }
event EventHandler SolutionChanged;      // opened, closed, or a new workspace
void OpenInEditor(string path, int line); // line <= 0: no jump
string ReadSource(string path);          // editor buffer if open, else the file on disk
void WriteSource(string path, string text); // one undoable edit if the file is open
string ResolvePythonInterpreter();

// Dialogs.
IWin32Window Owner { get; }              // the owner for your dialogs
bool IsNonInteractive { get; }           // true in tests and automation: show no modal dialog

// Your own settings, saved with the user's settings.
string GetSetting(string name);          // null when not set
void SetSetting(string name, string value); // null removes it
```

The host also docks tool panes beside the Solution Explorer:
`ClaimToolPanel(string title, Control pane, DockSide side)`, `ReleaseToolPanel(string title,
Control pane)` and `RevealToolPanel(string title)`. `DockSide` is `Left` or `Right`. The two
canvases use them for their Toolbox (left) and Properties (right) panes.

`AddInTabCallbacks` has two `Action` properties:

- `Activated`: your tab became the selected tab.
- `Removed`: your tab was closed. The host waits a moment before it calls this, so a tab that is
  only being moved in the strip does not count as closed.

A `solutionScoped` tab is closed when the solution closes, and is shown again on the next
solution open unless the user closed it.

### Use the designer kit

Namespace `HelixPythonNamespace.AddIns.Kit` holds visual components shared by the shell and
design tools: `DesignCommandStrip`, `ToolboxPalette`, `DesignerStatus`, `CanvasErrorTip`, `LunaColors`,
`CtrlWheelZoomFilter` and others. Use them if you build a design canvas and want it to look like
the others. You do not need them for a simple add-in.

## Create a simple add-in

`HelloAddIn.csproj`. Point `HintPath` at the folder that holds `HelixPython.exe`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <!-- Private=false: compile against the IDE's copy, but do not ship it. -->
    <Reference Include="HelixStudio.AddIns">
      <HintPath>C:\Program Files\HelixPython\HelixStudio.AddIns.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="System.Windows.Forms" />
  </ItemGroup>
</Project>
```

`HelloAddIn.cs`:

```csharp
using System.Windows.Forms;
using HelixPythonNamespace.AddIns;

namespace Contoso.Hello
{
    public sealed class HelloAddIn : IHelixAddIn
    {
        private const string TabKey = "contoso-hello";

        public string Id { get { return "contoso-hello"; } }

        public string DisplayName { get { return "Hello"; } }

        public void Initialize(IAddInHost host)
        {
            var page = new TabPage("Hello");
            page.Controls.Add(new Label { Text = "Hello from an add-in.", Dock = DockStyle.Fill });
            host.RegisterTab(TabKey, page, false, new AddInTabCallbacks
            {
                Activated = () => host.SetStatus("Hello tab is active"),
            });

            host.AddMenuItem("Say &Hello", () =>
            {
                host.ShowTab(TabKey);
                host.WriteOutputLine("Hello from " + DisplayName);
            }, null);
        }

        public void Shutdown()
        {
        }
    }
}
```

After deploy (§4), the IDE shows **Add-in ▸ Hello ▸ Say Hello**. It opens the tab and writes one
line to the Output pane.

## Add-in locations

There are two roots. Each add-in has its own folder in one of them.

| Kind | Folder | Written by |
|---|---|---|
| Bundled | `<folder of HelixPython.exe>\addins\<Name>\` | The build, or the client's installer. Can be read-only (Program Files). |
| User | `%LOCALAPPDATA%\HelixPython\addins\<AssemblyName>\` | Preferences ▸ Install Add-in |

When `HELIXSTUDIO_STATE_DIR` is set (tests do this), the user root is
`<HELIXSTUDIO_STATE_DIR>\addins\` instead.

The build places the built-in GUI Designer in its add-in folder under the application output.

Two lists in the user's settings file control what loads:

- `RemovedAddIns`: the `Id`s of add-ins the user removed. A listed add-in does not load, from
  either root.
- `PendingAddInDeletes`: user-installed folders to delete. Windows locks a loaded DLL, so Remove
  cannot delete the folder at once. The IDE deletes these folders at the next start, before it
  loads anything.

## How the IDE loads add-ins

At startup, after the main window is built and before the saved tabs come back, the IDE:

1. deletes the folders in `PendingAddInDeletes`;
2. scans the bundled root, then the user root, folder by folder, in name order;
3. loads every DLL directly in each folder (not in subfolders), except a DLL whose file name is
   also in the IDE folder;
4. creates every public, non-abstract `IHelixAddIn` class with a parameterless constructor, and
   calls `Initialize` on it.

A DLL that fails to load, or an add-in that fails to start, is one line in the Output pane. The
IDE always continues.

**How dependencies resolve.** The IDE folder comes first. Only an assembly the IDE folder does not
have is looked for in the add-in folders. So:

- Do not ship your own copy of `HelixStudio.AddIns.dll` or `HelixStudio.UI.dll`. The IDE ignores
  it and uses its own copy. If you build against a different version, your add-in can fail to
  load. Use `<Private>false</Private>`, as in §2. (The two bundled folders carry copies only
  because their build copies its whole output folder. The IDE ignores those copies too.)
- Ship every other dependency in your add-in folder, next to your DLL.

## Deploy an add-in

**By copy.** Put your DLL and its dependencies in a new folder under the bundled root, for
example `C:\Program Files\HelixPython\addins\Hello\`. Restart the IDE.

**From the IDE.** Preferences ▸ **Install Add-in** ▸ **[Install Add-in]**, then choose your DLL.
The IDE checks the DLL before it copies anything. It refuses a DLL with no add-in class, and one
whose `Id` is already loaded. If the DLL is good, the IDE copies the DLL, and every file in its
folder that the IDE folder does not already have (subfolders too), to the user root. It loads the
add-in at once: the **Add-in** menu shows it without a restart.

**Remove.** Select the add-in in the same list and press **[Remove Add-in]**. The IDE calls
`Shutdown`, closes your tabs and removes your menu items, file types and commands at once. Your
`Id` goes on `RemovedAddIns`. Install the same DLL again to bring it back.

Install and Remove take effect at once. The dialog's Cancel does not undo them.

The **Add-in** menu also has **Manage Add-ins...**, which opens Preferences on the Install Add-in
tab.

## Ship add-ins to clients

Each add-in is a separate deliverable. Give each client only the `addins\<Name>\` folders they
paid for. The IDE starts normally without a missing add-in: there is no error, only no menu for
it. There is no licence check. A client who gets a folder can use it.

Each add-in has its own version (`<Version>` in its project file). The Install Add-in list shows
it as `DisplayName — version — Bundled|User`.

## Limitations

- **An add-in's assembly stays in memory until the IDE restarts.** .NET Framework cannot unload an
  assembly without a separate AppDomain, and add-ins run in the IDE's own domain. After Remove,
  nothing of it can be reached; it only uses memory.
- **A newer version of a loaded add-in needs a restart.** The old assembly is still in memory, so
  the new one is not used until the next start. For a user-installed add-in: Remove it, restart,
  then Install the new DLL. (An Install in the same session as the Remove keeps the old files and
  does not copy the new ones.) For a bundled add-in: close the IDE, replace its folder, start
  again.
- **File ▸ Open does not list your file type.** Its filter has no entry for add-in file types yet,
  so the user must choose *All Files* to see them. Shell open, Recent files and the Welcome page
  work without this.
- **Keep paths under 260 characters.** .NET Framework 4.7.2 does not accept longer paths by
  default. This counts the full path of every file in your add-in folder, under both roots.
- Add-ins run in the IDE process with the user's rights. There is no sandbox.
