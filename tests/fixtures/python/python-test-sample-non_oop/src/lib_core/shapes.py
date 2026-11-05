"""Shape hierarchy: a Protocol interface plus a deep tree of concrete types.

The originals (Circle, Rectangle, Square, Triangle) stay so the existing
tests pass. We add Polygon, RegularPolygon, Ellipse, Star, Polyhedron and
move the geometric primitives (Point, Vector) into ``frozen=True``
dataclasses with ``@property`` / ``@cached_property`` accessors.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from functools import cached_property
from typing import Protocol


# -- Geometric primitives ---------------------------------------------------


@dataclass(frozen=True, slots=True)
class Point3D:
    """A 2-D point. Frozen so it can serve as a dict key / set member."""

    x: float
    y: float

    def distance_to(self, other: "Point") -> float:
        """Euclidean distance to ``other``."""
        dx = self.x - other.x
        dy = self.y - other.y
        return math.hypot(dx, dy)

    def translated(self, dx: float, dy: float) -> "Point":
        """Return a new point shifted by ``(dx, dy)``."""
        return Point3D(self.x + dx, self.y + dy)


@dataclass(frozen=True, slots=True)
class Vector:
    """A 2-D vector with the usual algebra operators."""

    dx: float
    dy: float

    @property
    def magnitude(self) -> float:
        """``sqrt(dx**2 + dy**2)``."""
        return math.hypot(self.dx, self.dy)

    @property
    def angle(self) -> float:
        """Angle in radians, measured from the positive X axis."""
        return math.atan2(self.dy, self.dx)

    def __add__(self, other: "Vector") -> "Vector":
        return Vector(self.dx + other.dx, self.dy + other.dy)

    def __sub__(self, other: "Vector") -> "Vector":
        return Vector(self.dx - other.dx, self.dy - other.dy)

    def __mul__(self, scalar: float) -> "Vector":
        return Vector(self.dx * scalar, self.dy * scalar)

    def normalized(self) -> "Vector":
        """Unit-length vector with the same direction."""
        m = self.magnitude
        if m == 0.0:
            return Vector(0.0, 0.0)
        return Vector(self.dx / m, self.dy / m)


# -- Protocol ---------------------------------------------------------------


class Shape(Protocol):
    """Anything that knows its own area and perimeter.

    Most legacy code in this sample only required ``area``; we add
    ``perimeter`` so the IDE has a richer Protocol to validate against.
    Existing concrete types still satisfy ``Shape`` because the
    Protocol is structural — they didn't need to declare conformance.
    """

    def area(self) -> float:
        ...

    def perimeter(self) -> float:
        ...


# -- The original four (kept so test_sample.py keeps passing) ---------------


class Circle:
    """A circle with a given radius."""

    def __init__(self, radius: float) -> None:
        self.radius = radius

    def area(self) -> float:
        return math.pi * self.radius * self.radius

    def perimeter(self) -> float:
        return 2.0 * math.pi * self.radius

    @property
    def diameter(self) -> float:
        """Twice the radius."""
        return 2.0 * self.radius


class Rectangle:
    """An axis-aligned rectangle with given width and height."""

    def __init__(self, width: float, height: float) -> None:
        self.width = width
        self.height = height

    def area(self) -> float:
        return self.width * self.height

    def perimeter(self) -> float:
        return 2.0 * (self.width + self.height)

    @property
    def aspect(self) -> float:
        """Width-to-height ratio."""
        return self.width / self.height


class Square(Rectangle):
    """Square is a Rectangle with equal sides — concrete inheritance."""

    def __init__(self, side: float) -> None:
        super().__init__(side, side)

    @property
    def side(self) -> float:
        """The single side length (= width = height)."""
        return self.width


class Triangle:
    """An arbitrary triangle via base × height (not three points)."""

    def __init__(self, base: float, height: float) -> None:
        self.base = base
        self.height = height

    def area(self) -> float:
        return 0.5 * self.base * self.height

    def perimeter(self) -> float:
        # Without all three sides we can't compute this exactly. For the
        # right-angled case where base ⟂ height, the hypotenuse is:
        hyp = math.hypot(self.base, self.height)
        return self.base + self.height + hyp


# -- The richer hierarchy ---------------------------------------------------


class Ellipse:
    """An axis-aligned ellipse with given semi-major/semi-minor axes."""

    def __init__(self, semi_major: float, semi_minor: float) -> None:
        if semi_major < semi_minor:
            semi_major, semi_minor = semi_minor, semi_major
        self.semi_major = semi_major
        self.semi_minor = semi_minor

    def area(self) -> float:
        return math.pi * self.semi_major * self.semi_minor

    def perimeter(self) -> float:
        # Ramanujan's approximation — exact closed form needs an
        # elliptic integral.
        a, b = self.semi_major, self.semi_minor
        h = ((a - b) ** 2) / ((a + b) ** 2)
        return math.pi * (a + b) * (1 + (3 * h) / (10 + math.sqrt(4 - 3 * h)))

    @cached_property
    def eccentricity(self) -> float:
        """``sqrt(1 - (b/a)^2)`` — cached because it never changes."""
        ratio = self.semi_minor / self.semi_major
        return math.sqrt(1.0 - ratio * ratio)


class Polygon:
    """A simple polygon defined by an ordered list of vertices.

    The polygon is assumed non-self-intersecting; results are undefined
    otherwise. ``area`` uses the shoelace formula.
    """

    def __init__(self, vertices: list[Point3D]) -> None:
        if len(vertices) < 3:
            raise ValueError(f"polygon needs ≥3 vertices, got {len(vertices)}")
        self.vertices = list(vertices)

    def area(self) -> float:
        # Shoelace.
        n = len(self.vertices)
        acc = 0.0
        for i in range(n):
            x0, y0 = self.vertices[i].x, self.vertices[i].y
            x1, y1 = self.vertices[(i + 1) % n].x, self.vertices[(i + 1) % n].y
            acc += x0 * y1 - x1 * y0
        return abs(acc) / 2.0

    def perimeter(self) -> float:
        n = len(self.vertices)
        return sum(
            self.vertices[i].distance_to(self.vertices[(i + 1) % n])
            for i in range(n)
        )

    @cached_property
    def centroid(self) -> Point3D:
        """Centroid (centre of mass for uniform density)."""
        cx = sum(v.x for v in self.vertices) / len(self.vertices)
        cy = sum(v.y for v in self.vertices) / len(self.vertices)
        return Point3D(cx, cy)

    @property
    def vertex_count(self) -> int:
        """Number of vertices."""
        return len(self.vertices)


class RegularPolygon(Polygon):
    """A regular polygon: ``sides`` equal-length sides around a centre."""

    def __init__(self, sides: int, side_length: float, centre: Point3D | None = None) -> None:
        if sides < 3:
            raise ValueError(f"regular polygon needs ≥3 sides, got {sides}")
        self.sides = sides
        self.side_length = side_length
        c = centre if centre is not None else Point3D(0.0, 0.0)
        self.centre = c
        # Generate vertices on a circle of the appropriate radius.
        radius = side_length / (2.0 * math.sin(math.pi / sides))
        vertices = [
            Point3D(
                c.x + radius * math.cos(2 * math.pi * i / sides),
                c.y + radius * math.sin(2 * math.pi * i / sides),
            )
            for i in range(sides)
        ]
        super().__init__(vertices)

    @property
    def apothem(self) -> float:
        """Distance from centre to the midpoint of any side."""
        return self.side_length / (2.0 * math.tan(math.pi / self.sides))

    @property
    def circumradius(self) -> float:
        """Distance from centre to any vertex."""
        return self.side_length / (2.0 * math.sin(math.pi / self.sides))


class Star(RegularPolygon):
    """An n-pointed star: a regular polygon with alternating inner vertices.

    Constructed by interleaving the regular polygon's outer vertices with
    inner vertices placed on a smaller concentric circle. The shoelace
    area inherited from Polygon handles the concave outline correctly.
    """

    def __init__(
        self,
        points: int,
        outer_radius: float,
        inner_radius: float,
        centre: Point3D | None = None,
    ) -> None:
        if points < 3:
            raise ValueError(f"star needs ≥3 points, got {points}")
        self.points = points
        self.outer_radius = outer_radius
        self.inner_radius = inner_radius
        c = centre if centre is not None else Point3D(0.0, 0.0)
        # Build vertex list: alternating outer/inner around 2π.
        n = 2 * points
        vertices: list[Point3D] = []
        for i in range(n):
            r = outer_radius if i % 2 == 0 else inner_radius
            theta = math.pi * i / points
            vertices.append(Point3D(c.x + r * math.cos(theta), c.y + r * math.sin(theta)))
        # Skip RegularPolygon.__init__ (its assumptions don't hold) and
        # call Polygon.__init__ directly.
        Polygon.__init__(self, vertices)
        self.sides = n  # for type compatibility
        self.side_length = vertices[0].distance_to(vertices[1])
        self.centre = c


class Polyhedron:
    """A convex 3-D polyhedron, summarised by its faces and total surface area.

    For stress-testing the IDE the actual geometry is hand-waved — what
    matters is the class shape, not the numeric correctness.
    """

    def __init__(self, faces: list[Polygon], height: float) -> None:
        if not faces:
            raise ValueError("polyhedron needs ≥1 face")
        self.faces = list(faces)
        self.height = height

    def area(self) -> float:
        """Total surface area (sum of face areas)."""
        return sum(f.area() for f in self.faces)

    def perimeter(self) -> float:
        """Sum of face perimeters — not geometrically meaningful for a 3-D
        solid but exposed for Shape Protocol conformance."""
        return sum(f.perimeter() for f in self.faces)

    @property
    def face_count(self) -> int:
        """Number of faces."""
        return len(self.faces)

    @cached_property
    def volume(self) -> float:
        """Approximate volume (avg-face-area × height) — hand-waved."""
        avg_area = sum(f.area() for f in self.faces) / len(self.faces)
        return avg_area * self.height
