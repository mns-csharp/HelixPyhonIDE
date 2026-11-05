"""Geometry helpers that operate on any ``Shape`` (Protocol).

The legacy two functions (``total_area``, ``largest``) stay so existing
tests pass. Added: bounding boxes, vector math, transformation matrices,
intersection / union predicates, polygon utilities, generic
``total_extent[S: Shape]``. Uses ``match``/``case`` to dispatch on
shape kind, exercising the IDE's pattern-match folding and indentation.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Iterable, List, Protocol, Sequence, TypeVar

from lib_core.shapes import (
    Circle,
    Ellipse,
    Point3D,
    Polygon,
    Rectangle,
    RegularPolygon,
    Shape,
    Square,
    Star,
    Triangle,
    Vector,
)


# -- Original helpers (kept) ------------------------------------------------


def total_area(shapes: List[Shape]) -> float:
    """Sum the area() of every shape in the list."""
    total = 0.0
    for s in shapes:
        total += s.area()
    return total


def largest(shapes: List[Shape]) -> Shape:
    """Return the shape with the greatest area.

    Precondition: ``shapes`` is non-empty.
    """
    best = shapes[0]
    best_area = best.area()
    for s in shapes[1:]:
        area = s.area()
        if area > best_area:
            best = s
            best_area = area
    return best


# -- Bounding boxes and ranges ---------------------------------------------


@dataclass(frozen=True, slots=True)
class BoundingBox:
    """Axis-aligned bounding rectangle ``[lo_x, hi_x] × [lo_y, hi_y]``."""

    lo_x: float
    lo_y: float
    hi_x: float
    hi_y: float

    @property
    def width(self) -> float:
        return self.hi_x - self.lo_x

    @property
    def height(self) -> float:
        return self.hi_y - self.lo_y

    @property
    def area(self) -> float:
        return self.width * self.height

    @property
    def centre(self) -> Point3D:
        return Point3D((self.lo_x + self.hi_x) / 2, (self.lo_y + self.hi_y) / 2)

    def contains_point(self, p: Point3D) -> bool:
        return self.lo_x <= p.x <= self.hi_x and self.lo_y <= p.y <= self.hi_y

    def expanded(self, dx: float, dy: float) -> BoundingBox:
        """Return a new box grown by ``(dx, dy)`` on each side."""
        return BoundingBox(self.lo_x - dx, self.lo_y - dy, self.hi_x + dx, self.hi_y + dy)

    def union(self, other: BoundingBox) -> BoundingBox:
        """Smallest box that contains both self and other."""
        return BoundingBox(
            min(self.lo_x, other.lo_x),
            min(self.lo_y, other.lo_y),
            max(self.hi_x, other.hi_x),
            max(self.hi_y, other.hi_y),
        )

    def intersect(self, other: BoundingBox) -> BoundingBox | None:
        """Largest box contained in both, or None if disjoint."""
        lo_x = max(self.lo_x, other.lo_x)
        lo_y = max(self.lo_y, other.lo_y)
        hi_x = min(self.hi_x, other.hi_x)
        hi_y = min(self.hi_y, other.hi_y)
        if lo_x >= hi_x or lo_y >= hi_y:
            return None
        return BoundingBox(lo_x, lo_y, hi_x, hi_y)


# -- Per-shape bounding box dispatch ---------------------------------------


def bounding_box(shape: Shape) -> BoundingBox:
    """Return the axis-aligned bounding box for ``shape``.

    Uses ``match``/``case`` for dispatch — the canonical exhaustiveness
    pattern in modern Python. Adds a default that handles arbitrary
    ``Shape`` Protocol values via ``area()`` when geometry isn't known.
    """
    match shape:
        case Circle(radius=r):
            return BoundingBox(-r, -r, r, r)
        case Square(width=s):
            return BoundingBox(0.0, 0.0, s, s)
        case Rectangle(width=w, height=h):
            return BoundingBox(0.0, 0.0, w, h)
        case Triangle(base=b, height=h):
            return BoundingBox(0.0, 0.0, b, h)
        case Ellipse(semi_major=a, semi_minor=b):
            return BoundingBox(-a, -b, a, b)
        case Star() | RegularPolygon() | Polygon():
            return _polygon_bounds(shape.vertices)
        case _:
            # Unknown shape — fallback to a square approximation
            # centred on origin with side = sqrt(area).
            side = math.sqrt(max(shape.area(), 0.0))
            return BoundingBox(0.0, 0.0, side, side)


def _polygon_bounds(vertices: Sequence[Point3D]) -> BoundingBox:
    """Tight bounds enclosing every vertex."""
    if not vertices:
        return BoundingBox(0.0, 0.0, 0.0, 0.0)
    xs = [v.x for v in vertices]
    ys = [v.y for v in vertices]
    return BoundingBox(min(xs), min(ys), max(xs), max(ys))


# -- Generic extent over arbitrary shape collections -----------------------


S = TypeVar("S", bound=Shape)


def total_extent(shapes: Iterable[S]) -> BoundingBox:
    """Single bounding box that contains every shape's bounding box.

    Returns an empty box at the origin when ``shapes`` is empty.
    """
    extent: BoundingBox | None = None
    for shape in shapes:
        bb = bounding_box(shape)
        extent = bb if extent is None else extent.union(bb)
    return extent if extent is not None else BoundingBox(0.0, 0.0, 0.0, 0.0)


# -- Vector math ------------------------------------------------------------


def dot(a: Vector, b: Vector) -> float:
    """Dot product."""
    return a.dx * b.dx + a.dy * b.dy


def cross_z(a: Vector, b: Vector) -> float:
    """Z-component of the 3-D cross product (a × b) for 2-D inputs."""
    return a.dx * b.dy - a.dy * b.dx


def angle_between(a: Vector, b: Vector) -> float:
    """Smallest angle between two vectors, in radians."""
    if a.magnitude == 0.0 or b.magnitude == 0.0:
        return 0.0
    cos_theta = max(-1.0, min(1.0, dot(a, b) / (a.magnitude * b.magnitude)))
    return math.acos(cos_theta)


def project(a: Vector, onto: Vector) -> Vector:
    """Project ``a`` onto ``onto``."""
    if onto.magnitude == 0.0:
        return Vector(0.0, 0.0)
    scalar = dot(a, onto) / (onto.magnitude * onto.magnitude)
    return Vector(onto.dx * scalar, onto.dy * scalar)


# -- 2-D transformations ---------------------------------------------------


@dataclass(frozen=True, slots=True)
class Matrix2x3:
    """A 2×3 affine matrix ``[[a, b, tx], [c, d, ty]]``."""

    a: float
    b: float
    c: float
    d: float
    tx: float = 0.0
    ty: float = 0.0

    def apply(self, p: Point3D) -> Point3D:
        """Transform a point."""
        return Point3D(self.a * p.x + self.b * p.y + self.tx,
                     self.c * p.x + self.d * p.y + self.ty)

    def compose(self, other: Matrix2x3) -> Matrix2x3:
        """Return ``self ∘ other`` — the effect of applying ``other`` then ``self``."""
        return Matrix2x3(
            a=self.a * other.a + self.b * other.c,
            b=self.a * other.b + self.b * other.d,
            c=self.c * other.a + self.d * other.c,
            d=self.c * other.b + self.d * other.d,
            tx=self.a * other.tx + self.b * other.ty + self.tx,
            ty=self.c * other.tx + self.d * other.ty + self.ty,
        )

    @classmethod
    def identity(cls) -> Matrix2x3:
        """The 2×3 identity (no transformation)."""
        return cls(1.0, 0.0, 0.0, 1.0, 0.0, 0.0)

    @classmethod
    def translation(cls, dx: float, dy: float) -> Matrix2x3:
        """Pure translation by ``(dx, dy)``."""
        return cls(1.0, 0.0, 0.0, 1.0, dx, dy)

    @classmethod
    def scale(cls, sx: float, sy: float | None = None) -> Matrix2x3:
        """Pure scaling. If ``sy`` is None, uses ``sx`` for both axes."""
        return cls(sx, 0.0, 0.0, sx if sy is None else sy, 0.0, 0.0)

    @classmethod
    def rotation(cls, theta: float) -> Matrix2x3:
        """Rotation by ``theta`` radians around the origin."""
        cos_t = math.cos(theta)
        sin_t = math.sin(theta)
        return cls(cos_t, -sin_t, sin_t, cos_t, 0.0, 0.0)


class Transformable(Protocol):
    """Anything that can return a transformed copy of itself given a matrix.

    ``transform_polygon`` below is the free-function form for ``Polygon``;
    this Protocol lets other call sites accept any type with its own
    ``transformed()`` method instead.
    """

    def transformed(self, matrix: Matrix2x3) -> "Transformable":
        ...


def transform_polygon(polygon: Polygon, matrix: Matrix2x3) -> Polygon:
    """Return a new polygon with every vertex transformed by ``matrix``."""
    return Polygon([matrix.apply(v) for v in polygon.vertices])


# -- Convex hull (Graham scan) ---------------------------------------------


def convex_hull(points: Sequence[Point3D]) -> list[Point3D]:
    """Return the convex hull of ``points`` in counter-clockwise order.

    Uses Andrew's monotone chain (Graham scan variant). Handles
    collinear edge cases by skipping them.
    """
    if len(points) <= 1:
        return list(points)
    sorted_pts = sorted(points, key=lambda p: (p.x, p.y))

    def cross(o: Point3D, a: Point3D, b: Point3D) -> float:
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x)

    lower: list[Point3D] = []
    for p in sorted_pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    upper: list[Point3D] = []
    for p in reversed(sorted_pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    # Concatenate, dropping the duplicate endpoints.
    return lower[:-1] + upper[:-1]


# -- Centroid -----------------------------------------------------------


def centroid_of(polygon: Polygon) -> Point3D:
    """Geometric centroid of a simple polygon (uniform density).

    Uses the classic ``Σ(x_i + x_{i+1}) × cross_i / (6 × A)`` formulae.
    """
    n = polygon.vertex_count
    if n == 0:
        return Point3D(0.0, 0.0)
    cx = cy = 0.0
    a_total = 0.0
    for i in range(n):
        v0 = polygon.vertices[i]
        v1 = polygon.vertices[(i + 1) % n]
        cross = v0.x * v1.y - v1.x * v0.y
        a_total += cross
        cx += (v0.x + v1.x) * cross
        cy += (v0.y + v1.y) * cross
    if a_total == 0.0:
        # Degenerate: return centroid of vertices.
        return Point3D(
            sum(v.x for v in polygon.vertices) / n,
            sum(v.y for v in polygon.vertices) / n,
        )
    a_total *= 0.5
    return Point3D(cx / (6 * a_total), cy / (6 * a_total))
