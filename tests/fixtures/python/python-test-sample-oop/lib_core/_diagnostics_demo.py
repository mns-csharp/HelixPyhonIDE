"""Diagnostic sandbox — intentional bugs for IDE stress-testing.

This module is NOT imported by app code. It exists so the Problems
panel has rich material to display:

  - PY6003 type mismatches
  - PY7001 unknown modules
  - PY7003 unknown attributes
  - Unused imports
  - Same-name reuse across scopes (occurrence-highlight stress)

Each bug is preceded by a comment naming the diagnostic code it should
trigger, so reviewing the diagnostics panel doubles as a self-check
that the IDE caught what we planned.

The leading underscore tells human readers "do not import"; the IDE
still analyses the file.
"""
from __future__ import annotations

# PY7001: unknown module — `helix_imaginary` doesn't exist.
import helix_imaginary  # noqa: F401

# PY7003: real module, missing attribute.
from os.path import join, this_attribute_does_not_exist  # noqa: F401

from .errors import KeyMissingError, StorageError, ValidationError  # noqa: F401
from .models import Boxy, Email, User, UserId
from .shapes import Circle, Point3D, Polygon, Rectangle, Shape
from .storage import InMemoryTypedStorage


# -- PY6003: type mismatch examples -----------------------------------------


def add_shapes(shapes: list[Shape]) -> float:
    """Sum every shape's area. Caller is supposed to pass a list of Shape."""
    return sum(s.area() for s in shapes)


def call_with_wrong_types() -> None:
    """Several call sites that should each fire PY6003."""

    # PY6003: passing a list of ints where list[Shape] is expected.
    add_shapes([1, 2, 3])  # type: ignore[arg-type]

    # PY6003: a single int where list[Shape] is expected.
    add_shapes(42)  # type: ignore[arg-type]

    # PY6003: passing a string where Email's address must be a valid email.
    Email("not-an-email")

    # PY6003: passing the wrong UserId type to User.
    User(user_id="not-a-userid", email=Email("a@b.c"))  # type: ignore[arg-type]

    # PY6003: passing a tuple instead of a list of Point to Polygon.
    Polygon((Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(0.0, 1.0)))  # type: ignore[arg-type]

    # PY6003: Box[int] specialisation that gets a string.
    box: Boxy[int] = Boxy("not an int")  # type: ignore[arg-type]
    return None


# -- PY7003: missing attributes on real modules -----------------------------


def use_missing_module_attrs() -> None:
    """Each line should fire PY7003 for an attribute that doesn't exist."""
    import os

    # PY7003: os.no_such_function doesn't exist.
    os.no_such_function()  # type: ignore[attr-defined]

    # PY7003: math has no attribute "completely_made_up".
    import math
    math.completely_made_up(2.0)  # type: ignore[attr-defined]

    # PY7003: pathlib has no attribute "InventedPath".
    import pathlib
    pathlib.InventedPath("/tmp")  # type: ignore[attr-defined]


# -- Occurrence-highlight stress: same identifier names reused -------------


def occurrence_a() -> int:
    result = 0
    for i in range(5):
        result += i
    return result


def occurrence_b() -> int:
    result = 1
    for i in range(5):
        result *= max(1, i)
    return result


def occurrence_c() -> int:
    result = -1
    for value in [1, 2, 3]:
        result = max(result, value)
    return result


def occurrence_d() -> int:
    # `result` reused here — clicking it should highlight only this scope.
    result = 99
    return result


# -- Broken match statement (intentional fall-through) ---------------------


def classify(value: object) -> str:
    """Returns a label per value — the wildcard is missing on purpose."""
    match value:
        case int(x) if x < 0:
            return "negative-int"
        case int(0):
            return "zero"
        case int(x) if x > 0:
            return "positive-int"
        case str() as s:
            return f"string({s!r})"
        # Intentionally no `_:` arm — anything else falls through and
        # returns None implicitly. Type checker should flag the missing
        # return on some branches.


# -- Bad class hierarchy: subclass calls parent with wrong args ------------


class MisbehavingPolygon__(Polygon):
    """Triggers a PY6003 when calling super().__init__ with bad arg types."""

    def __init__(self) -> None:
        # Polygon expects list[Point], not list[int].
        super().__init__([1, 2, 3])  # type: ignore[arg-type]


# -- Storage misuse: setting wrong value type -------------------------------


def storage_misuse() -> None:
    """A typed store guarded by ``value_type=int`` rejects strings at runtime
    AND should be flagged statically."""
    store: InMemoryTypedStorage[str, int] = InMemoryTypedStorage(value_type=int)
    store.set("alpha", 1)
    # PY6003: "two" is str, not int.
    store.set("beta", "two")  # type: ignore[arg-type]
    # PY6003: returns int but caller treats it as str.
    value: str = store.get("alpha")  # type: ignore[assignment]
    print(value)


# -- Unreachable / dead code ------------------------------------------------


def has_dead_code(n: int) -> int:
    """Returns ``n`` always — the second return is unreachable."""
    return n
    # PY ... unreachable code below.
    print("never runs")  # type: ignore[unreachable]
    return -1


# -- Unused-import pile (linters typically warn) ----------------------------

# Imported above but never referenced — should produce unused-import diagnostics:
#   - join (from os.path)
#   - StorageError, ValidationError (from .errors)
#   - Rectangle, Circle (from .shapes)
#   - KeyMissingError (from .errors)
