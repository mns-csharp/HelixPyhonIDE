# Workspace rules

HelixPython checks a solution before compile, run, or debug actions. These checks keep the IDE's
project model aligned with the files and prevent it from executing code outside the workspace it
can inspect.

## Solution rules

| Rule | Requirement |
|---|---|
| PY8001 | Every user-written source file belongs to a project in the solution. |
| PY8002 | Python code follows the configured object-oriented policy. The supported entry point and test protocols can use module-level functions. |
| PY8003 | Test files belong to a test project. |
| PY8004 | The on-disk directory structure matches the Solution Explorer. |

When a rule blocks an action, use the diagnostic to find the affected file or project. Add files to
the correct project, move tests to a test project, or correct the directory structure. Do not
change files to silence a diagnostic unless the change matches the intended project design.

## Loose Python files

When you run a Python file outside a solution, PY8005 allows imports from installed packages but
blocks imports from other user-written files. If the script needs your own modules, add it to a
solution.

## Why the rules exist

The IDE should execute code users can see, analyze, and debug. A project tree that disagrees with
the disk or omits source files cannot provide that guarantee. These checks make the workspace
structure part of the run and debug contract.
