"""Advanced geometry tests: bounding boxes, transforms, hulls, generics.

Heavier on parametrize / fixture chains than ``test_sample.py``; meant
to give the IDE plenty of pytest-flavoured navigation targets.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

import pytest  # noqa: E402

from lib_core.shapes import (  # noqa: E402
    Circle,
    Ellipse,
    Point3D,
    Polygon,
    Rectangle,
    RegularPolygon,
    Square,
    Star,
    Triangle,
    Vector,
)
from lib_utils.geometry import (  # noqa: E402
    BoundingBox,
    Matrix2x3,
    angle_between,
    bounding_box,
    centroid_of,
    convex_hull,
    cross_z,
    dot,
    project,
    total_extent,
    transform_polygon,
)


# -- Fixtures --------------------------------------------------------------


@pytest.fixture
def unit_triangle() -> Polygon:
    """A right triangle with legs along +x and +y axes."""
    return Polygon([Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(0.0, 1.0)])


@pytest.fixture
def unit_square() -> Polygon:
    """1×1 axis-aligned polygon (not the Rectangle Square subclass)."""
    return Polygon([
        Point3D(0.0, 0.0),
        Point3D(1.0, 0.0),
        Point3D(1.0, 1.0),
        Point3D(0.0, 1.0),
    ])


@pytest.fixture
def regular_hexagon() -> RegularPolygon:
    return RegularPolygon(6, side_length=1.0)


@pytest.fixture
def shapes_zoo() -> list:
    """A heterogenous list spanning every concrete shape type."""
    return [
        Circle(1.0),
        Rectangle(2.0, 3.0),
        Square(4.0),
        Triangle(5.0, 6.0),
        Ellipse(2.0, 1.0),
        RegularPolygon(5, side_length=1.0),
        Star(5, outer_radius=2.0, inner_radius=1.0),
    ]


# -- BoundingBox basics ----------------------------------------------------


class TestBoundingBox:
    @pytest.mark.parametrize(
        "lo_x,lo_y,hi_x,hi_y,expected_w,expected_h",
        [
            pytest.param(0.0, 0.0, 1.0, 1.0, 1.0, 1.0, id="unit"),
            pytest.param(-1.0, -2.0, 1.0, 2.0, 2.0, 4.0, id="symmetric"),
            pytest.param(0.0, 0.0, 5.0, 0.0, 5.0, 0.0, id="degenerate-line"),
        ],
    )
    def test_dimensions(self, lo_x, lo_y, hi_x, hi_y, expected_w, expected_h):
        bb = BoundingBox(lo_x, lo_y, hi_x, hi_y)
        assert bb.width == pytest.approx(expected_w)
        assert bb.height == pytest.approx(expected_h)

    def test_centre(self):
        bb = BoundingBox(0.0, 0.0, 4.0, 6.0)
        c = bb.centre
        assert c.x == pytest.approx(2.0)
        assert c.y == pytest.approx(3.0)

    def test_contains_point(self):
        bb = BoundingBox(0.0, 0.0, 1.0, 1.0)
        assert bb.contains_point(Point3D(0.5, 0.5))
        assert bb.contains_point(Point3D(0.0, 0.0))  # corner
        assert not bb.contains_point(Point3D(1.5, 0.5))

    def test_union(self):
        a = BoundingBox(0.0, 0.0, 1.0, 1.0)
        b = BoundingBox(2.0, 2.0, 3.0, 3.0)
        u = a.union(b)
        assert u == BoundingBox(0.0, 0.0, 3.0, 3.0)

    def test_intersect_disjoint_returns_none(self):
        a = BoundingBox(0.0, 0.0, 1.0, 1.0)
        b = BoundingBox(2.0, 2.0, 3.0, 3.0)
        assert a.intersect(b) is None

    def test_intersect_overlapping(self):
        a = BoundingBox(0.0, 0.0, 2.0, 2.0)
        b = BoundingBox(1.0, 1.0, 3.0, 3.0)
        result = a.intersect(b)
        assert result == BoundingBox(1.0, 1.0, 2.0, 2.0)

    def test_expanded(self):
        a = BoundingBox(0.0, 0.0, 1.0, 1.0)
        e = a.expanded(0.5, 0.5)
        assert e == BoundingBox(-0.5, -0.5, 1.5, 1.5)


# -- bounding_box dispatch --------------------------------------------------


class TestBoundingBoxDispatch:
    def test_circle(self):
        bb = bounding_box(Circle(2.0))
        assert bb == BoundingBox(-2.0, -2.0, 2.0, 2.0)

    def test_rectangle(self):
        bb = bounding_box(Rectangle(3.0, 4.0))
        assert bb == BoundingBox(0.0, 0.0, 3.0, 4.0)

    def test_square_specialised(self):
        bb = bounding_box(Square(2.0))
        assert bb == BoundingBox(0.0, 0.0, 2.0, 2.0)

    def test_ellipse(self):
        bb = bounding_box(Ellipse(3.0, 1.0))
        assert bb == BoundingBox(-3.0, -1.0, 3.0, 1.0)

    def test_polygon_uses_vertices(self, unit_triangle):
        bb = bounding_box(unit_triangle)
        assert bb == BoundingBox(0.0, 0.0, 1.0, 1.0)

    def test_total_extent_empty_returns_origin(self):
        bb = total_extent([])
        assert bb == BoundingBox(0.0, 0.0, 0.0, 0.0)

    def test_total_extent_zoo(self, shapes_zoo):
        bb = total_extent(shapes_zoo)
        # The Star reaches r=2.0; circle to r=1.0; ellipse to a=2.0.
        # Together they should at least span [-3, 3] in some axis.
        assert bb.width >= 4.0


# -- Vector math -----------------------------------------------------------


class TestVectorMath:
    @pytest.mark.parametrize(
        "a,b,expected",
        [
            (Vector(1.0, 0.0), Vector(0.0, 1.0), 0.0),
            (Vector(2.0, 3.0), Vector(4.0, 5.0), 23.0),
            (Vector(0.0, 0.0), Vector(1.0, 1.0), 0.0),
        ],
    )
    def test_dot(self, a, b, expected):
        assert dot(a, b) == pytest.approx(expected)

    def test_cross_z_perpendicular(self):
        # Orthogonal vectors have unit cross-z when both unit length.
        assert cross_z(Vector(1.0, 0.0), Vector(0.0, 1.0)) == pytest.approx(1.0)

    def test_cross_z_parallel(self):
        assert cross_z(Vector(1.0, 0.0), Vector(2.0, 0.0)) == pytest.approx(0.0)

    @pytest.mark.parametrize(
        "a,b,expected_radians",
        [
            pytest.param(Vector(1.0, 0.0), Vector(0.0, 1.0), math.pi / 2, id="90"),
            pytest.param(Vector(1.0, 0.0), Vector(1.0, 0.0), 0.0, id="parallel"),
            pytest.param(Vector(1.0, 0.0), Vector(-1.0, 0.0), math.pi, id="anti"),
        ],
    )
    def test_angle_between(self, a, b, expected_radians):
        assert angle_between(a, b) == pytest.approx(expected_radians)

    def test_project_onto_x_axis(self):
        result = project(Vector(3.0, 4.0), Vector(5.0, 0.0))
        assert result.dx == pytest.approx(3.0)
        assert result.dy == pytest.approx(0.0)

    def test_project_onto_zero_vector(self):
        result = project(Vector(1.0, 1.0), Vector(0.0, 0.0))
        assert result.dx == 0.0 and result.dy == 0.0


# -- Transformation matrix --------------------------------------------------


class TestMatrix2x3:
    def test_identity(self):
        m = Matrix2x3.identity()
        p = m.apply(Point3D(3.0, 4.0))
        assert p == Point3D(3.0, 4.0)

    def test_translation(self):
        m = Matrix2x3.translation(5.0, -2.0)
        p = m.apply(Point3D(0.0, 0.0))
        assert p == Point3D(5.0, -2.0)

    @pytest.mark.parametrize(
        "sx,sy,p,expected",
        [
            pytest.param(2.0, 2.0, Point3D(1.0, 1.0), Point3D(2.0, 2.0), id="uniform"),
            pytest.param(2.0, None, Point3D(1.0, 1.0), Point3D(2.0, 2.0), id="default-sy"),
            pytest.param(2.0, 0.5, Point3D(2.0, 4.0), Point3D(4.0, 2.0), id="non-uniform"),
        ],
    )
    def test_scale(self, sx, sy, p, expected):
        m = Matrix2x3.scale(sx, sy)
        result = m.apply(p)
        assert result.x == pytest.approx(expected.x)
        assert result.y == pytest.approx(expected.y)

    def test_rotation_quarter(self):
        m = Matrix2x3.rotation(math.pi / 2)
        p = m.apply(Point3D(1.0, 0.0))
        assert p.x == pytest.approx(0.0, abs=1e-9)
        assert p.y == pytest.approx(1.0, abs=1e-9)

    def test_compose_translate_then_scale(self):
        translate = Matrix2x3.translation(1.0, 0.0)
        scale = Matrix2x3.scale(2.0)
        # Apply ``translate`` first, then ``scale``: composed = scale ∘ translate.
        composed = scale.compose(translate)
        result = composed.apply(Point3D(0.0, 0.0))
        assert result.x == pytest.approx(2.0)
        assert result.y == pytest.approx(0.0)

    def test_transform_polygon(self, unit_square):
        m = Matrix2x3.scale(2.0)
        scaled = transform_polygon(unit_square, m)
        assert scaled.vertices[0] == Point3D(0.0, 0.0)
        assert scaled.vertices[2] == Point3D(2.0, 2.0)


# -- Convex hull -----------------------------------------------------------


class TestConvexHull:
    def test_three_points_triangle(self):
        pts = [Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(0.0, 1.0)]
        hull = convex_hull(pts)
        assert len(hull) == 3

    def test_collinear_points_skipped(self):
        pts = [Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(2.0, 0.0), Point3D(0.0, 1.0)]
        hull = convex_hull(pts)
        # Collinear midpoint should be skipped — hull has 3 vertices.
        assert len(hull) == 3

    def test_square_with_interior_point(self):
        pts = [
            Point3D(0.0, 0.0),
            Point3D(2.0, 0.0),
            Point3D(2.0, 2.0),
            Point3D(0.0, 2.0),
            Point3D(1.0, 1.0),  # inside the square
        ]
        hull = convex_hull(pts)
        assert len(hull) == 4
        assert Point3D(1.0, 1.0) not in hull

    def test_empty_input(self):
        assert convex_hull([]) == []


# -- Centroid --------------------------------------------------------------


class TestCentroid:
    def test_unit_square(self, unit_square):
        c = centroid_of(unit_square)
        assert c.x == pytest.approx(0.5)
        assert c.y == pytest.approx(0.5)

    def test_unit_triangle(self, unit_triangle):
        c = centroid_of(unit_triangle)
        # Expected centroid of (0,0), (1,0), (0,1) is (1/3, 1/3).
        assert c.x == pytest.approx(1 / 3)
        assert c.y == pytest.approx(1 / 3)

    def test_regular_hexagon_at_origin(self, regular_hexagon):
        c = centroid_of(regular_hexagon)
        assert c.x == pytest.approx(0.0, abs=1e-9)
        assert c.y == pytest.approx(0.0, abs=1e-9)


# -- Generic total_extent ---------------------------------------------------


class TestTotalExtent:
    def test_single_circle(self):
        bb = total_extent([Circle(3.0)])
        assert bb == BoundingBox(-3.0, -3.0, 3.0, 3.0)

    def test_two_disjoint_shapes(self):
        bb = total_extent([Circle(1.0), Rectangle(2.0, 1.0)])
        # Circle at origin + Rectangle (0, 0) → (2, 1)
        assert bb.lo_x == pytest.approx(-1.0)
        assert bb.hi_x == pytest.approx(2.0)

    @pytest.mark.parametrize("count", [0, 1, 5, 25])
    def test_scales_with_input(self, shapes_zoo, count):
        # Just sanity — never raises on prefixes of any length.
        prefix = shapes_zoo[:count] if count <= len(shapes_zoo) else shapes_zoo * (count // len(shapes_zoo) + 1)
        bb = total_extent(prefix)
        assert isinstance(bb, BoundingBox)
