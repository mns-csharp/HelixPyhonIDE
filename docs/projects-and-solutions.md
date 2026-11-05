# Projects and solutions

HelixPython organizes Python work in projects and solutions. A solution groups related projects;
each project has metadata and source files. Visual tools help create and inspect this structure,
and the files remain editable directly.

## Start a project

1. On the welcome screen, select **New...**.
2. Choose a notebook, project, or solution.
3. Select a project template and location, then create the files.

Project metadata is stored in *pyproject.toml*. Open that file in the IDE to edit it directly, or
use the available project settings and TOML designer.

## Open existing work

Select **Open...** to open a notebook or solution. You can also open a *pyproject.toml* file or
drop it onto the IDE window.

## Import a directory

Select **Import a directory** to scan a folder of Python sources and create project files for it.
Review the generated project metadata and file membership before building or running the project.

## Keep source visible

Designer-generated Python stays in the project. Open it in the editor to review or change the
implementation. This lets visual workflows help with structure without hiding what the program
will run.
