# HelixPython product boundary

HelixPython is a visual-first Windows desktop IDE for Python. It combines an editor, Python
language services, project and solution management, analysis, code generation, and execution tools.
The product favors integrated visual workflows while keeping generated and handwritten Python
source directly editable.

## Included

- WinForms desktop shell, in-house editor, completion, and Python language services.
- Python project, package, and solution workflows, including the four solution blockers PY8001–PY8004.
- Single-file import isolation (PY8005) when running a loose Python file.
- OOP class canvas, GUI Designer, TOML/pyproject canvas, and CMake Designer.
- Lint, semantic analysis, code review, metrics, complexity analysis, refactoring, debugging,
  profiling, test execution, and Jupyter notebook support.
- Python-to-C/C++/CUDA conversion and related C++ development services.
- Generic add-in SDK and the built-in GUI Designer add-in.

The add-in SDK remains available for independently developed extensions. The PDB
structure-processing library is retained as a non-UI library.

## Product identity

- Desktop executable: `HelixPython.exe`.
- User data: `%APPDATA%\HelixPython\`.
- Settings, logs, and scratch data are kept outside the source repository.

See [README.md](README.md) for requirements and build instructions. Project history, public
hosting, release and support channels must be configured by maintainers for a public release.
