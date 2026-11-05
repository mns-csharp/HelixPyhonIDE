# Multi-package Python workspace — the NON-COMPLIANT half of the sample pair

Hand-crafted multi-package Python workspace for integration tests and manual QA of
HelixStudio IDE features. Preserve its intentional diagnostics and test expectations.

## Its other job: the counterexample

This fixture is deliberately **blocked**, and must stay that way — it is the "before" against which
`../python-test-sample-oop` is the "after". Do not "fix" it; the pair only teaches anything as a
contrast, and `SampleFixturePolicyTests.TheTwoSamplesDifferOnlyByTheTestPackageKind_InTheirTestTrees`
fails if both go green.

| Rule | What triggers it here |
|---|---|
| `PY8001` (workspace membership) | `debug_target.py` and `exception_target.py` sit at the solution root, inside no member project |
| `PY8003` (test-file placement) | four test files inside `my_unit_test_package`, which is `kind = "library"` |
| `PY8002` (object orientation) | module-level functions across `src/`, **and** the module-level `@pytest.fixture`s and `test_pi_constant_is_close_to_math_pi` in the test tree |

That last row is the one worth opening side by side with `-oop`: those fixture functions are
byte-identical in both fixtures. They are errors here and legal there, and the only difference is
`kind = "library"` versus `kind = "test"`. That is rule 2's test-protocol carve-out, isolated —
it keys on the project kind, never on the function name, so production code cannot opt out of
rule 2 by naming a function `test_something`.

Pinned by `tests/unit/non-gui/workspace/HelixStudio.Workspace.Tests/SampleFixturePolicyTests.cs`
(read-only; it asserts the exact PY8001 file names and the PY8003 count of four).

## Layout

```
pyproject.toml          <-- workspace root; File -> Open this in HelixStudio
src/
  lib_core/             <-- library: shapes, animals, storage, models, events, errors (Protocols + ABCs)
  lib_utils/            <-- library: geometry, logger, async_ops, iterators, decorators (depends on lib_core)
  app_cli/              <-- app with a main() — shapes/animals console demo (has 1 intentional bug)
  app_server/           <-- app with a main() — async routes + middleware demo
  app_gui/              <-- app with a main() — Tkinter desktop demo (shapes + animals in a window)
```

## How to load

1. Launch HelixStudio.
2. **File -> Open** and pick `tests/fixtures/python/python-test-sample/pyproject.toml`.
3. The left tree should show five packages: `lib_core`, `lib_utils`, `app_cli`, `app_server`, `app_gui`.

## What to check in each feature

### Project tree
- 4 packages, each expandable into category folders: Interfaces / Abstract Classes / Classes.
- Interfaces: `Shape` (lib_core.shapes), `Storage` (lib_core.storage).
- Abstract classes: `Animal` (lib_core.animals).
- Concrete: `Circle`, `Rectangle`, `Square`, `Triangle`, `Dog`, `Cat`, `Bird`, `PoliceDog`,
  `InMemoryStorage`, `Logger`, and (in the apps) no type declarations — just module-level
  functions.

### Designer Canvas ("All" tab)
- ~10 type boxes with inheritance arrows:
  - `Shape` with implements-style edges to `Circle`, `Rectangle`, `Triangle`
  - `Rectangle` <- `Square`
  - `Animal` <- `Dog`, `Cat`, `Bird`; `Dog` <- `PoliceDog`
  - `Storage` with implements-style edge to `InMemoryStorage`

### Tree double-click
Double-click any type in the left tree. Expected:
- Editor tab loads the source file at the declaration line (open it manually if focus
  stays on the Designer).
- Designer Canvas stays visible, inner tab flips to All, the clicked node is selected
  and zoomed.

### Compile (F6 / Build -> Compile)
- Virtual Compiler walks all four packages.
- Problems tab should light up with at least one entry from `app_cli/main.py` — the line
  `total_area("not a list")` passes a `str` where a `list` is expected (code PY6003 or
  similar arg-type mismatch).

### Run (Ctrl+Shift+B / Run menu)
- With Python and the `build` module installed on the machine, runs `python -m build`
  against the workspace root. Output streams to the Run Results tab.
- Without those prerequisites, expect a helpful error in the tab.

### Analyze -> Run Analysis
- SOLID rules evaluate the diagram. Tab populates with whatever violations the rules
  find (probably none — the sample is intentionally small and tidy).

## Deliberate test cases

| File | Line pattern | What it tests |
|------|--------------|---------------|
| `app_cli/main.py` | `total_area("not a list")` | Problems tab arg-type mismatch |

Add more over time as new features land.
