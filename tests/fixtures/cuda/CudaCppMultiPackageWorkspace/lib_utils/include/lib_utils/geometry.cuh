#pragma once

// Geometry helpers that operate on any Shape.
//
// Mirrors lib_utils/geometry.py. Translation notes:
//   - Functions that take Python's `List[Shape]` (a runtime-heterogeneous
//     list under the Shape Protocol) take `const std::vector<const
//     lib_core::shapes::IShape*>&` here -- the runtime-polymorphic
//     sibling of the Shape concept (see shapes.cuh's file header).
//   - `total_extent[S: Shape]` (a bounded-TypeVar generic function) stays
//     a template constrained by the `Shape` concept for the homogeneous
//     case, with a second `total_extent` overload taking `IShape*` for
//     the heterogeneous case the app/test demos actually exercise.
//   - Python's `match shape: case Circle(radius=r): ...` structural
//     dispatch becomes an ordinary `dynamic_cast` chain in
//     `bounding_box()` -- the direct analogue for a closed, known set of
//     concrete subclasses.

#include <algorithm>
#include <cmath>
#include <concepts>
#include <numbers>
#include <optional>
#include <vector>

#include "lib_core/shapes.cuh"

namespace lib_utils::geometry {

using lib_core::shapes::Circle;
using lib_core::shapes::Ellipse;
using lib_core::shapes::IShape;
using lib_core::shapes::Point3D;
using lib_core::shapes::Polygon;
using lib_core::shapes::Rectangle;
using lib_core::shapes::RegularPolygon;
using lib_core::shapes::Shape;
using lib_core::shapes::Square;
using lib_core::shapes::Star;
using lib_core::shapes::Triangle;
using lib_core::shapes::Vector;

// -- Original helpers (kept) ------------------------------------------------

// Sum the area() of every shape in the list.
double total_area(const std::vector<const IShape*>& shapes);

// Return the shape with the greatest area. Precondition: non-empty.
const IShape* largest(const std::vector<const IShape*>& shapes);

// -- Bounding boxes and ranges ---------------------------------------------

// Axis-aligned bounding rectangle [lo_x, hi_x] x [lo_y, hi_y].
struct BoundingBox {
    double lo_x = 0.0;
    double lo_y = 0.0;
    double hi_x = 0.0;
    double hi_y = 0.0;

    __host__ __device__ bool operator==(const BoundingBox& other) const {
        return lo_x == other.lo_x && lo_y == other.lo_y && hi_x == other.hi_x && hi_y == other.hi_y;
    }

    __host__ __device__ double width() const { return hi_x - lo_x; }
    __host__ __device__ double height() const { return hi_y - lo_y; }
    __host__ __device__ double area() const { return width() * height(); }

    __host__ __device__ Point3D centre() const { return Point3D((lo_x + hi_x) / 2, (lo_y + hi_y) / 2); }

    __host__ __device__ bool contains_point(const Point3D& p) const {
        return lo_x <= p.x && p.x <= hi_x && lo_y <= p.y && p.y <= hi_y;
    }

    // Return a new box grown by (dx, dy) on each side.
    __host__ __device__ BoundingBox expanded(double dx, double dy) const {
        return BoundingBox{lo_x - dx, lo_y - dy, hi_x + dx, hi_y + dy};
    }

    // Smallest box that contains both self and other.
    __host__ __device__ BoundingBox union_with(const BoundingBox& other) const {
        return BoundingBox{
            lo_x < other.lo_x ? lo_x : other.lo_x,
            lo_y < other.lo_y ? lo_y : other.lo_y,
            hi_x > other.hi_x ? hi_x : other.hi_x,
            hi_y > other.hi_y ? hi_y : other.hi_y,
        };
    }

    // Largest box contained in both, or nullopt if disjoint.
    std::optional<BoundingBox> intersect(const BoundingBox& other) const {
        double lx = std::max(lo_x, other.lo_x);
        double ly = std::max(lo_y, other.lo_y);
        double hx = std::min(hi_x, other.hi_x);
        double hy = std::min(hi_y, other.hi_y);
        if (lx >= hx || ly >= hy) return std::nullopt;
        return BoundingBox{lx, ly, hx, hy};
    }
};

// -- Per-shape bounding box dispatch ---------------------------------------

// Return the axis-aligned bounding box for `shape`. Uses a dynamic_cast
// chain for dispatch -- the closed set of concrete shape subclasses is
// the direct analogue of Python's exhaustive match/case here.
BoundingBox bounding_box(const IShape& shape);

// -- Generic extent over a homogeneous shape collection ---------------------

// Single bounding box containing every shape's bounding box (homogeneous,
// concept-constrained container -- the direct analogue of `Iterable[S]`
// with `S: Shape`).
template <typename S>
    requires Shape<S> && std::derived_from<S, IShape>
BoundingBox total_extent(const std::vector<S>& shapes) {
    std::optional<BoundingBox> extent;
    for (const auto& shape : shapes) {
        BoundingBox bb = bounding_box(static_cast<const IShape&>(shape));
        extent = extent.has_value() ? extent->union_with(bb) : bb;
    }
    return extent.value_or(BoundingBox{0.0, 0.0, 0.0, 0.0});
}

// Heterogeneous sibling used by the app/test demos, which build a
// `list[Shape]` mixing several concrete types.
BoundingBox total_extent(const std::vector<const IShape*>& shapes);

// -- Vector math ------------------------------------------------------------

__host__ __device__ inline double dot(const Vector& a, const Vector& b) { return a.dx * b.dx + a.dy * b.dy; }

// Z-component of the 3-D cross product (a x b) for 2-D inputs.
__host__ __device__ inline double cross_z(const Vector& a, const Vector& b) { return a.dx * b.dy - a.dy * b.dx; }

// Smallest angle between two vectors, in radians.
double angle_between(const Vector& a, const Vector& b);

// Project `a` onto `onto`.
Vector project(const Vector& a, const Vector& onto);

// -- 2-D transformations ---------------------------------------------------

// A 2x3 affine matrix [[a, b, tx], [c, d, ty]].
struct Matrix2x3 {
    double a = 1.0, b = 0.0, c = 0.0, d = 1.0, tx = 0.0, ty = 0.0;

    __host__ __device__ Point3D apply(const Point3D& p) const {
        return Point3D(a * p.x + b * p.y + tx, c * p.x + d * p.y + ty);
    }

    // self ∘ other -- the effect of applying `other` then `self`.
    __host__ __device__ Matrix2x3 compose(const Matrix2x3& other) const {
        return Matrix2x3{
            a * other.a + b * other.c,
            a * other.b + b * other.d,
            c * other.a + d * other.c,
            c * other.b + d * other.d,
            a * other.tx + b * other.ty + tx,
            c * other.tx + d * other.ty + ty,
        };
    }

    __host__ __device__ static Matrix2x3 identity() { return Matrix2x3{1.0, 0.0, 0.0, 1.0, 0.0, 0.0}; }

    __host__ __device__ static Matrix2x3 translation(double dx, double dy) {
        return Matrix2x3{1.0, 0.0, 0.0, 1.0, dx, dy};
    }

    __host__ __device__ static Matrix2x3 scale(double sx, std::optional<double> sy = std::nullopt) {
        double sy_value = sy.value_or(sx);
        return Matrix2x3{sx, 0.0, 0.0, sy_value, 0.0, 0.0};
    }

    static Matrix2x3 rotation(double theta) {
        double cos_t = std::cos(theta);
        double sin_t = std::sin(theta);
        return Matrix2x3{cos_t, -sin_t, sin_t, cos_t, 0.0, 0.0};
    }
};

// Return a new polygon with every vertex transformed by `matrix`.
Polygon transform_polygon(const Polygon& polygon, const Matrix2x3& matrix);

// -- Convex hull (Graham scan) ----------------------------------------------

// Return the convex hull of `points` in counter-clockwise order. Uses
// Andrew's monotone chain (Graham scan variant); collinear points are
// skipped.
std::vector<Point3D> convex_hull(std::vector<Point3D> points);

// -- Centroid -----------------------------------------------------------

// Geometric centroid of a simple polygon (uniform density).
Point3D centroid_of(const Polygon& polygon);

}  // namespace lib_utils::geometry
