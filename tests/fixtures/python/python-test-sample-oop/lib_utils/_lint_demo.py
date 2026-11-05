"""Lint-engine sandbox — every block triggers ONE specific lint rule.

The diagnostics_demo files target the compile/link engines; this file
mirrors them for the lint engine so every rule has an end-to-end
verification on real Python. Each block is prefixed with the rule code
it is expected to fire.
"""
from __future__ import annotations

# I002: multiple imports on one line.
import os, sys  # noqa: E401,F401

# I001: wildcard import — opaque to static analysis.
from math import *  # noqa: F403

# C001: import that is never used (also exercises C001 outside match patterns).
from typing import Optional  # noqa: F401

# N003: identifier shadows a Python builtin.
list = [1, 2, 3]  # noqa


# N001: class name should be PascalCase.
class badly_named_class:  # noqa
    pass


# N002: function name should be snake_case.
def BadlyNamedFunction():  # noqa
    return 1


# C002: mutable default argument.
def append_to(target=[]):  # noqa
    target.append(1)
    return target


# C003: comparison to None should use 'is' / 'is not'.
def is_none(x):
    return x == None  # noqa


# C004: comparison to True / False literal.
def is_truthy(x):
    return x == True  # noqa


# B001: bare 'except:' catches BaseException.
def swallow():
    try:
        return 1
    except:  # noqa
        return 0


# L004: multiple statements on one line.
def two_stmts():
    a = 1; b = 2  # noqa
    return a + b


# PY-ANN-001: method with no return-type annotation (covered by analyze, not lint).
class WithUnannotated:
    def helper(self, x: int):  # noqa: missing return annotation
        return x * 2
