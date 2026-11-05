// Mirrors lib_utils/_lint_demo.py.
//
// That file is a sandbox where every block triggers exactly one
// Python-lint rule (PascalCase class names, snake_case function names,
// mutable default arguments, bare `except:`, comparing to None/True with
// `==`, multiple imports/statements on one line, wildcard imports). None
// of these rules have a C++ equivalent -- a real C++ compiler + linter
// enforce a disjoint style ruleset, and several of the Python snippets
// (a bare `except:`, a wildcard `from math import *`) have no C++ syntax
// at all to reproduce. Left as a documentation-only stub, matching the
// Rust sibling's lib_utils/src/lint_demo.rs. See the Python source for
// exactly which lint code (I001-I002, N001-N003, C001-C004, B001, L004)
// each block was meant to trigger.
