# HelixPython

HelixPython is a visual-first Windows desktop IDE for Python. It combines editing, project and
solution management, code generation, analysis, debugging, profiling, notebook editing, and test
running in one application.

## Why HelixPython exists

### 1. Python makes type information less certain

Python is dynamically typed. Type annotations can guide editors and other tools, but Python does
not enforce them at runtime. When code leaves types implicit, an editor must infer them from
source and context. Completion and static diagnostics can therefore be less certain than in
statically typed languages such as C# or C++. [Python's typing documentation](https://docs.python.org/3/library/typing.html)
describes how annotations support third-party tools without changing runtime enforcement.

Python is widely used in machine learning and scientific work, including deep learning and
molecular modeling.

### 2. Python developers use varied environments

Python developers work across desktop IDEs, code editors, and terminal-based tools. HelixPython
provides a Windows desktop workflow with explicit Python project membership and solution-level
rules.

### 3. Project boundaries affect correctness

Python can import modules from the interpreter's search path, including paths added through
`sys.path` or `PYTHONPATH`, as well as installed packages. If several copies of a module are
available, a developer may run or debug a different copy from the one open in the editor.
([Python module search path](https://docs.python.org/3/library/sys_path_init.html))

HelixPython makes project membership part of its run and debug checks. In a solution, every
user-written source file must belong to a project, and the on-disk structure must match the
Solution Explorer. When running a loose Python file, the IDE allows installed packages but blocks
imports from other user-written files. See [workspace rules](docs/workspace-rules.md) for details.

### 4. HelixPython connects visual tools to editable source

HelixPython uses a parser-based code model, solution-aware project management, integrated visual
designers, and analysis tools. Designer-generated Python stays editable, and completion, checks,
run, and debug workflows use the same workspace model. The result is a Windows IDE that makes
project structure visible and keeps the code being analyzed and executed traceable.

## Features

- Python editing with completion, documentation help, linting, semantic analysis, and refactoring.
- Visual project, package, and solution management with policy checks.
- Class and GUI designers that generate editable Python source.
- Python debugging, profiling, test execution, and Jupyter notebook support.
- Python-to-C/C++/CUDA conversion and CMake project support.
- An add-in SDK for independently developed extensions.

See [PRODUCT.md](PRODUCT.md) for the product boundary and [docs/](docs/) for user and subsystem
documentation.

## Requirements

- Windows 10 or later.
- Visual Studio with the .NET Framework 4.7.2 targeting pack and MSBuild.
- Python 3.9 or later for Python project execution. Some workflows need optional tools such as
  `debugpy`, `pytest`, CMake, or a C/C++ compiler; the IDE reports unavailable tools at use time.

## Build

From a Visual Studio Developer PowerShell, restore and build the IDE project:

```powershell
$project = 'src\gui\desktop\HelixStudio.App\HelixStudio.App.csproj'
MSBuild.exe $project -t:Restore -p:Configuration=Debug -m:1 -nodereuse:false
MSBuild.exe $project -t:Build -p:Configuration=Debug -m:1 -nodereuse:false
```

For other Visual Studio installations, locate `MSBuild.exe` with `vswhere`. Build and test details
are in [docs/development/build-and-test.md](docs/development/build-and-test.md). The IDE and its GUI
integration tests target Windows.

## Open source and citation

HelixPython is distributed under the MIT License; see [LICENSE](LICENSE). Please read
[CONTRIBUTING.md](CONTRIBUTING.md) before proposing a change. Citation metadata is in
[CITATION.cff](CITATION.cff).
