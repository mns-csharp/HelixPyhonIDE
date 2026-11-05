# pyvectorsample

Fixture project for HelixStudio's editor "Vectorize" / "Un-vectorize" context-menu feature
(loop <-> NumPy/idiomatic refactoring, `HelixStudio.Refactoring.VectorizationRefactor`).

Not a demo app — every function exists to be selected in the editor and run through
Vectorize or Un-vectorize, or to be read by an automated test that does the same thing
programmatically. Files:

- `src/pyvectorsample/forward_patterns.py` — one realistic function per forward pattern
  (F1-F16: loop -> NumPy/comprehension). Selecting the `# BEGIN Fn` / `# END Fn` block and
  choosing **Vectorize** should produce the NumPy/idiomatic replacement in each docstring.
- `src/pyvectorsample/reverse_patterns.py` — the mirror image (R1, R2, R5, R10, R14:
  NumPy/idiomatic -> explicit loop). Choose **Un-vectorize**.
- `src/pyvectorsample/near_misses.py` — code that resembles a pattern but differs in a way
  that changes behavior (self-referential/prefix-sum dependency, shifted index, wrong
  accumulator operator, early exit, hidden side effect, disguised gather, eager
  side-effecting branches, non-multiplicative reset). The engine must refuse to offer an
  uncaveated rewrite on any of these — each docstring explains exactly why.
- `src/pyvectorsample/real_world_examples.py` — the pattern embedded inside a normal,
  docstring-and-validation-bearing function, so only a sub-range of the function (not the
  whole function) is the intended selection — matching how a user actually selects code.
- `src/pyvectorsample/edge_cases.py` — boundary conditions: in-place update, single-element
  input, attribute-based array access, `enumerate()` instead of `range(len())`, nested loops.

`tests/test_pyvectorsample.py` is a plain pytest suite that pins down the *original*
(pre-refactor) behavior of every function above — a correctness oracle for the "before"
value, independent of HelixStudio. It does not test the refactoring itself.

The refactoring engine's own assertions against this fixture live on the C# side, in
`HelixStudio.Refactoring.Tests/VectorizationFixtureTests.cs` — it reads these `.py` files
off disk by their `# BEGIN <ID>` / `# END <ID>` markers and asserts `VectorizationRefactor.Match`
behaves as each docstring claims.

## Running the Python tests standalone

```
pip install -e .
pytest tests/
```
