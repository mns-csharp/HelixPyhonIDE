"""Diagnostic sandbox for ``app_cli`` — intentional bugs.

Application-level mistakes: wrong dispatch keys, wrong payload types,
broken pipeline stages.
"""
from __future__ import annotations

from lib_core.shapes import Circle, Polygon, Point3D

from .commands import (
    AddShape,
    AddShapePayload,
    Context,
    PersistPayload,
    Transform,
    TransformPayload,
    make_default_bus,
)
from .pipeline import Filter, Map, Pipeline, Reduce, Take


# -- Wrong dispatch key (catches typos at runtime; static checker may flag) -


def bad_dispatch() -> None:
    """Dispatch a command name that wasn't registered."""
    bus = make_default_bus()
    ctx = Context()
    # Runtime: returns CommandResult(ok=False) with ConfigurationError.
    # Static: the typo is invisible to a string-keyed dispatcher, but
    # listing it here gives the user something to grep for.
    result = bus.execute("add-shapee", AddShapePayload(Circle(1.0)), ctx)
    assert not result.ok


# -- Wrong payload type for command --------------------------------------


def wrong_payload_type() -> None:
    """``AddShape`` wants ``AddShapePayload``; pass the wrong dataclass."""
    # PY6003: AddShape needs AddShapePayload, not PersistPayload.
    cmd = AddShape(PersistPayload("/tmp/x"))  # type: ignore[arg-type]
    cmd.execute(Context())


# -- Transform a non-polygon -----------------------------------------------


def transform_non_polygon() -> None:
    """``Transform`` only works on Polygons. Passing a Circle returns
    a CommandResult(ok=False) at runtime; intentionally drives the bus."""
    bus = make_default_bus()
    ctx = Context()
    bus.execute("add_shape", AddShapePayload(Circle(1.0)), ctx)
    from lib_utils.geometry import Matrix2x3
    result = bus.execute("transform", TransformPayload(0, Matrix2x3.identity()), ctx)
    assert not result.ok


# -- Pipeline type chain mismatches ----------------------------------------


def bad_pipeline_chain() -> None:
    """Compose pipeline steps whose I/O types don't line up."""
    # Map(int -> str), then Reduce expects (acc, str) but the lambda
    # below treats the str as int — runtime error and static type miss.
    p: Pipeline[list[int], int] = Pipeline(
        [
            Map(lambda x: str(x), name="to_str"),
            # PY6003: lambda treats `n` as int but step yields str.
            Reduce(lambda acc, n: acc + n, 0, name="sum"),  # type: ignore[arg-type,operator]
        ]
    )
    p.run([1, 2, 3])


def bad_take_argument() -> None:
    """``Take`` needs a non-negative int."""
    # ValueError at runtime: n must be non-negative.
    t = Take(-1)
    t.run([1, 2, 3])


# -- Wrong polygon vertex types --------------------------------------------


def polygon_with_tuples() -> None:
    """Polygon expects list[Point]. Passing tuples is wrong."""
    # PY6003: each element should be Point, not tuple.
    poly = Polygon([(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)])  # type: ignore[list-item]
    print(poly.area())
