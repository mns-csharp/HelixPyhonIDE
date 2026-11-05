# Contributing to HelixPython

Contributions are welcome. For a substantial change, discuss the proposed behavior with the
maintainers before implementation. Keep changes focused and describe the user-visible effect.

## Development

- Use Visual Studio MSBuild for restore and builds; `dotnet build` does not correctly process this
  solution's Windows Forms resources.
- Preserve the four solution policy blockers PY8001–PY8004 and the single-file import isolation
  rule PY8005. A change must not make Compile, Run, or Debug bypass the applicable rules.
- Add or update tests for behavior changes. GUI behavior should be checked through fixture
  solutions using the real IDE surface.
- Keep third-party notices and license files with any dependencies or assets you add.
- Do not commit build output, local settings, credentials, or user project data.

## Pull requests

Explain the problem, the intended behavior, and how you verified it. Include relevant test output and
screenshots for user-visible GUI changes. A maintainer should review the change before it is merged.

## Authorship

By contributing, you confirm that you have the right to submit your contribution under the
repository's MIT License. Contributors retain copyright in their contributions.
