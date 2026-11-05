// Mirrors my_unit_test_package/tests/test_advanced_geometry.py, using
// Catch2 instead of pytest. pytest fixtures become small local helper
// functions; `@pytest.mark.parametrize` becomes Catch2's GENERATE.

#include <cmath>
#include <numbers>
#include <optional>
#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>

#include "lib_core/shapes.cuh"
#include "lib_utils/geometry.cuh"

using namespace lib_core::shapes;
using namespace lib_utils::geometry;
using Catch::Approx;

namespace {

Polygon unit_triangle() {
    return Polygon({Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(0.0, 1.0)});
}

Polygon unit_square() {
    return Polygon({Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(1.0, 1.0), Point3D(0.0, 1.0)});
}

RegularPolygon regular_hexagon() { return RegularPolygon(6, 1.0); }

}  // namespace

// -- BoundingBox basics ------------------------------------------------

TEST_CASE("BoundingBox dimensions", "[geometry]") {
    struct Case {
        double lo_x, lo_y, hi_x, hi_y, expected_w, expected_h;
    };
    auto c = GENERATE(Case{0.0, 0.0, 1.0, 1.0, 1.0, 1.0}, Case{-1.0, -2.0, 1.0, 2.0, 2.0, 4.0},
                       Case{0.0, 0.0, 5.0, 0.0, 5.0, 0.0});
    BoundingBox bb{c.lo_x, c.lo_y, c.hi_x, c.hi_y};
    REQUIRE(bb.width() == Approx(c.expected_w));
    REQUIRE(bb.height() == Approx(c.expected_h));
}

TEST_CASE("BoundingBox centre", "[geometry]") {
    BoundingBox bb{0.0, 0.0, 4.0, 6.0};
    Point3D c = bb.centre();
    REQUIRE(c.x == Approx(2.0));
    REQUIRE(c.y == Approx(3.0));
}

TEST_CASE("BoundingBox contains_point", "[geometry]") {
    BoundingBox bb{0.0, 0.0, 1.0, 1.0};
    REQUIRE(bb.contains_point(Point3D(0.5, 0.5)));
    REQUIRE(bb.contains_point(Point3D(0.0, 0.0)));
    REQUIRE(!bb.contains_point(Point3D(1.5, 0.5)));
}

TEST_CASE("BoundingBox union", "[geometry]") {
    BoundingBox a{0.0, 0.0, 1.0, 1.0};
    BoundingBox b{2.0, 2.0, 3.0, 3.0};
    REQUIRE(a.union_with(b) == BoundingBox{0.0, 0.0, 3.0, 3.0});
}

TEST_CASE("BoundingBox intersect disjoint returns nullopt", "[geometry]") {
    BoundingBox a{0.0, 0.0, 1.0, 1.0};
    BoundingBox b{2.0, 2.0, 3.0, 3.0};
    REQUIRE(!a.intersect(b).has_value());
}

TEST_CASE("BoundingBox intersect overlapping", "[geometry]") {
    BoundingBox a{0.0, 0.0, 2.0, 2.0};
    BoundingBox b{1.0, 1.0, 3.0, 3.0};
    auto result = a.intersect(b);
    REQUIRE(result.has_value());
    REQUIRE(*result == BoundingBox{1.0, 1.0, 2.0, 2.0});
}

TEST_CASE("BoundingBox expanded", "[geometry]") {
    BoundingBox a{0.0, 0.0, 1.0, 1.0};
    REQUIRE(a.expanded(0.5, 0.5) == BoundingBox{-0.5, -0.5, 1.5, 1.5});
}

// -- bounding_box dispatch --------------------------------------------------

TEST_CASE("bounding_box dispatch", "[geometry]") {
    SECTION("circle") { REQUIRE(bounding_box(Circle(2.0)) == BoundingBox{-2.0, -2.0, 2.0, 2.0}); }
    SECTION("rectangle") { REQUIRE(bounding_box(Rectangle(3.0, 4.0)) == BoundingBox{0.0, 0.0, 3.0, 4.0}); }
    SECTION("square specialised") { REQUIRE(bounding_box(Square(2.0)) == BoundingBox{0.0, 0.0, 2.0, 2.0}); }
    SECTION("ellipse") { REQUIRE(bounding_box(Ellipse(3.0, 1.0)) == BoundingBox{-3.0, -1.0, 3.0, 1.0}); }
    SECTION("polygon uses vertices") {
        Polygon tri = unit_triangle();
        REQUIRE(bounding_box(tri) == BoundingBox{0.0, 0.0, 1.0, 1.0});
    }
    SECTION("total_extent empty returns origin") {
        REQUIRE(total_extent(std::vector<const IShape*>{}) == BoundingBox{0.0, 0.0, 0.0, 0.0});
    }
    SECTION("total_extent zoo") {
        Circle c(1.0);
        Rectangle r(2.0, 3.0);
        Square sq(4.0);
        Triangle t(5.0, 6.0);
        Ellipse e(2.0, 1.0);
        RegularPolygon rp(5, 1.0);
        Star st(5, 2.0, 1.0);
        std::vector<const IShape*> zoo{&c, &r, &sq, &t, &e, &rp, &st};
        BoundingBox bb = total_extent(zoo);
        // The Star reaches r=2.0; circle to r=1.0; ellipse to a=2.0.
        REQUIRE(bb.width() >= 4.0);
    }
}

// -- Vector math -----------------------------------------------------------

TEST_CASE("dot product", "[geometry]") {
    struct Case {
        Vector a, b;
        double expected;
    };
    auto c = GENERATE(Case{Vector(1.0, 0.0), Vector(0.0, 1.0), 0.0}, Case{Vector(2.0, 3.0), Vector(4.0, 5.0), 23.0},
                       Case{Vector(0.0, 0.0), Vector(1.0, 1.0), 0.0});
    REQUIRE(dot(c.a, c.b) == Approx(c.expected));
}

TEST_CASE("cross_z perpendicular", "[geometry]") {
    REQUIRE(cross_z(Vector(1.0, 0.0), Vector(0.0, 1.0)) == Approx(1.0));
}

TEST_CASE("cross_z parallel", "[geometry]") { REQUIRE(cross_z(Vector(1.0, 0.0), Vector(2.0, 0.0)) == Approx(0.0)); }

TEST_CASE("angle_between", "[geometry]") {
    struct Case {
        Vector a, b;
        double expected_radians;
    };
    auto c = GENERATE(Case{Vector(1.0, 0.0), Vector(0.0, 1.0), std::numbers::pi / 2},
                       Case{Vector(1.0, 0.0), Vector(1.0, 0.0), 0.0},
                       Case{Vector(1.0, 0.0), Vector(-1.0, 0.0), std::numbers::pi});
    REQUIRE(angle_between(c.a, c.b) == Approx(c.expected_radians));
}

TEST_CASE("project onto x axis", "[geometry]") {
    Vector result = project(Vector(3.0, 4.0), Vector(5.0, 0.0));
    REQUIRE(result.dx == Approx(3.0));
    REQUIRE(result.dy == Approx(0.0));
}

TEST_CASE("project onto zero vector", "[geometry]") {
    Vector result = project(Vector(1.0, 1.0), Vector(0.0, 0.0));
    REQUIRE(result.dx == 0.0);
    REQUIRE(result.dy == 0.0);
}

// -- Transformation matrix --------------------------------------------------

TEST_CASE("Matrix2x3 identity", "[geometry]") {
    Point3D p = Matrix2x3::identity().apply(Point3D(3.0, 4.0));
    REQUIRE(p == Point3D(3.0, 4.0));
}

TEST_CASE("Matrix2x3 translation", "[geometry]") {
    Point3D p = Matrix2x3::translation(5.0, -2.0).apply(Point3D(0.0, 0.0));
    REQUIRE(p == Point3D(5.0, -2.0));
}

TEST_CASE("Matrix2x3 scale", "[geometry]") {
    struct Case {
        double sx;
        std::optional<double> sy;
        Point3D p;
        Point3D expected;
    };
    auto c = GENERATE(Case{2.0, 2.0, Point3D(1.0, 1.0), Point3D(2.0, 2.0)},
                       Case{2.0, std::nullopt, Point3D(1.0, 1.0), Point3D(2.0, 2.0)},
                       Case{2.0, 0.5, Point3D(2.0, 4.0), Point3D(4.0, 2.0)});
    Point3D result = Matrix2x3::scale(c.sx, c.sy).apply(c.p);
    REQUIRE(result.x == Approx(c.expected.x));
    REQUIRE(result.y == Approx(c.expected.y));
}

TEST_CASE("Matrix2x3 rotation quarter turn", "[geometry]") {
    Point3D p = Matrix2x3::rotation(std::numbers::pi / 2).apply(Point3D(1.0, 0.0));
    REQUIRE(p.x == Approx(0.0).margin(1e-9));
    REQUIRE(p.y == Approx(1.0).margin(1e-9));
}

TEST_CASE("Matrix2x3 compose translate then scale", "[geometry]") {
    Matrix2x3 translate = Matrix2x3::translation(1.0, 0.0);
    Matrix2x3 scale = Matrix2x3::scale(2.0);
    Matrix2x3 composed = scale.compose(translate);
    Point3D result = composed.apply(Point3D(0.0, 0.0));
    REQUIRE(result.x == Approx(2.0));
    REQUIRE(result.y == Approx(0.0));
}

TEST_CASE("transform_polygon scales vertices", "[geometry]") {
    Polygon sq = unit_square();
    Matrix2x3 m = Matrix2x3::scale(2.0);
    Polygon scaled = transform_polygon(sq, m);
    REQUIRE(scaled.vertices[0] == Point3D(0.0, 0.0));
    REQUIRE(scaled.vertices[2] == Point3D(2.0, 2.0));
}

// -- Convex hull -----------------------------------------------------------

TEST_CASE("convex_hull three points triangle", "[geometry]") {
    std::vector<Point3D> pts{Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(0.0, 1.0)};
    REQUIRE(convex_hull(pts).size() == 3);
}

TEST_CASE("convex_hull collinear points skipped", "[geometry]") {
    std::vector<Point3D> pts{Point3D(0.0, 0.0), Point3D(1.0, 0.0), Point3D(2.0, 0.0), Point3D(0.0, 1.0)};
    REQUIRE(convex_hull(pts).size() == 3);
}

TEST_CASE("convex_hull square with interior point", "[geometry]") {
    std::vector<Point3D> pts{Point3D(0.0, 0.0), Point3D(2.0, 0.0), Point3D(2.0, 2.0), Point3D(0.0, 2.0),
                              Point3D(1.0, 1.0)};
    auto hull = convex_hull(pts);
    REQUIRE(hull.size() == 4);
    bool contains_interior = false;
    for (const auto& p : hull) {
        if (p == Point3D(1.0, 1.0)) contains_interior = true;
    }
    REQUIRE(!contains_interior);
}

TEST_CASE("convex_hull empty input", "[geometry]") { REQUIRE(convex_hull({}).empty()); }

// -- Centroid --------------------------------------------------------------

TEST_CASE("centroid_of unit square", "[geometry]") {
    Point3D c = centroid_of(unit_square());
    REQUIRE(c.x == Approx(0.5));
    REQUIRE(c.y == Approx(0.5));
}

TEST_CASE("centroid_of unit triangle", "[geometry]") {
    Point3D c = centroid_of(unit_triangle());
    REQUIRE(c.x == Approx(1.0 / 3.0));
    REQUIRE(c.y == Approx(1.0 / 3.0));
}

TEST_CASE("centroid_of regular hexagon at origin", "[geometry]") {
    Point3D c = centroid_of(regular_hexagon());
    REQUIRE(c.x == Approx(0.0).margin(1e-9));
    REQUIRE(c.y == Approx(0.0).margin(1e-9));
}

// -- Generic total_extent ---------------------------------------------------

TEST_CASE("total_extent single circle", "[geometry]") {
    Circle c(3.0);
    std::vector<const IShape*> shapes{&c};
    REQUIRE(total_extent(shapes) == BoundingBox{-3.0, -3.0, 3.0, 3.0});
}

TEST_CASE("total_extent two disjoint shapes", "[geometry]") {
    Circle c(1.0);
    Rectangle r(2.0, 1.0);
    std::vector<const IShape*> shapes{&c, &r};
    BoundingBox bb = total_extent(shapes);
    REQUIRE(bb.lo_x == Approx(-1.0));
    REQUIRE(bb.hi_x == Approx(2.0));
}
