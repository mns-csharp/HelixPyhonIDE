"""Diagnostic sandbox for ``lib_utils`` — intentional bugs.

See ``lib_core/_diagnostics_demo.py`` for the format. Each bug below
is preceded by the diagnostic code it should trigger.
"""
from __future__ import annotations

# PY7001: unknown module
import helix_doesnt_exist  # noqa: F401

# PY7003: real module, missing attribute
from itertools import inexistent_iterator  # noqa: F401

from lib_core.errors import StorageError, ValidationError  # noqa: F401
from lib_core.models import Boxy, Pair, ok
from lib_core.shapes import Circle, Polygon, Rectangle, Shape

from .geometry import BoundingBox, total_extent, total_area
from .iterators import chunked, classify_numbers, windowed


# -- Generic mis-parameterization: Box[int] populated with str --------------


def generic_box_misuse() -> None:
    """``Box[int]`` should not accept a string at construction."""
    # PY6003: Box[int] expects int, got str.
    box: Boxy[int] = Boxy("not an int")  # type: ignore[arg-type]

    # PY6003: passing wrong-typed value to Box.map.
    same: Boxy[int] = box.map(lambda v: v + 1)  # ok
    bad: Boxy[int] = box.map(lambda v: str(v))  # type: ignore[assignment]
    print(box, same, bad)


# -- Pair[A, B] order swapped at call site ---------------------------------


def pair_misuse() -> Pair[int, str]:
    """The annotation says (int, str) but the constructor's args are reversed."""
    # PY6003: Pair[int, str] but construction supplies (str, int).
    return Pair("hello", 42)  # type: ignore[arg-type]


# -- Iterator helpers given wrong shapes -----------------------------------


def iterator_misuse() -> None:
    """Pass non-iterable / wrong-shaped inputs to the iterator helpers."""

    # PY6003: chunked expects an Iterable; passing an int.
    list(chunked(42, size=2))  # type: ignore[arg-type]

    # PY6003: classify_numbers wants Iterable[float]; passing dict.
    classify_numbers({"a": 1.0, "b": 2.0})  # type: ignore[arg-type]

    # ValueError at runtime: window size 0. Static checker may flag it
    # depending on the rules engine.
    list(windowed([1, 2, 3], 0))


# -- Geometry: total_area expects Shape, got Polygon (fine) but list of Polygon
#    is expected to be list[Shape]. PY6003 should NOT fire here because of
#    the recently-added bare-class-vs-generic rule. Kept as a regression
#    pin for that fix.


def regression_no_py6003_for_list() -> None:
    """This call SHOULD pass clean. If PY6003 fires, the assignability
    rule for ``builtins.list`` ↔ ``typing.List`` regressed.
    """
    poly = Polygon([])
    shapes: list[Shape] = [poly, Circle(1.0), Rectangle(2.0, 3.0)]
    total_area(shapes)


# -- Wrong number of TypeVar arguments --------------------------------------


def wrong_typevar_count() -> None:
    """Pair takes two TypeVars; supplying one is intentionally invalid."""
    # PY6003: Pair[int] has only one type arg — Pair needs (A, B).
    p: Pair[int] = Pair(1, 2)  # type: ignore[type-arg]
    print(p)


# -- BoundingBox direct misuse ---------------------------------------------


def bb_misuse() -> None:
    """Construct BoundingBox with the wrong types."""
    # PY6003: BoundingBox expects floats; passes strings.
    bb = BoundingBox("0", "0", "1", "1")  # type: ignore[arg-type]
    print(bb)
    # PY6003: BoundingBox.contains_point expects Point; passes a tuple.
    real = BoundingBox(0.0, 0.0, 1.0, 1.0)
    real.contains_point((0.5, 0.5))  # type: ignore[arg-type]


# -- ok() generic helper used wrong --------------------------------------


def ok_misuse() -> None:
    """``ok(value)`` returns ``Ok[T]``. Misusing the result type triggers PY6003."""
    o = ok(42)
    # PY6003: int has no .startswith
    o.value.startswith("4")  # type: ignore[attr-defined]


# -- Same-name across scopes (occurrence-highlight stress) -----------------


def first_outer() -> None:
    value = 1
    def inner() -> int:
        value = 2
        return value
    print(value, inner())


def second_outer() -> None:
    value = "hello"
    def inner() -> str:
        value = "world"
        return value
    print(value, inner())


def third_outer() -> None:
    value = 3.14
    return value  # type: ignore[return-value]
