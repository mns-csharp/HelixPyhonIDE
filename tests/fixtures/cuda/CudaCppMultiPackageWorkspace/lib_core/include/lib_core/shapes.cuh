#pragma once

// Shape hierarchy: a Protocol interface plus a deep tree of concrete types.
//
// Mirrors lib_core/shapes.py. Translation notes:
//   - typing.Protocol -> a C++20 concept (`Shape`): purely structural, just
//     like Python's Protocol -- any type with matching area()/perimeter()
//     methods satisfies it, no declared conformance needed.
//   - Python code also stores heterogeneous shapes in one `list[Shape]`
//     (see geometry.py's total_area/largest and the test fixtures' shapes
//     zoo). A compile-time concept alone cannot express that runtime
//     mixed-type container, so every concrete shape ALSO publicly inherits
//     the small virtual interface `IShape` -- the concept is satisfied
//     structurally by the very same area()/perimeter() overrides, so both
//     mechanisms coexist without duplication.
//   - Point3D keeps its Python name verbatim even though it is a 2-D point
//     (the original docstring even says "A 2-D point") -- kept as-is
//     rather than "fixed", to preserve the 1:1 name mapping used by the
//     Rust/C# sibling translations.
//   - Small, frequently-copied value types (Point3D, Vector, Circle,
//     Rectangle, Square, Triangle) get __host__ __device__ qualifiers,
//     matching this repo's cuda/workspaces convention, even though this
//     fixture has no dedicated kernel using them.
//   - @cached_property (Ellipse.eccentricity, Polygon.centroid,
//     Polyhedron.volume) becomes a mutable std::optional<double> cache
//     populated lazily by a const accessor.
//   - Star(RegularPolygon) skips RegularPolygon.__init__ and calls
//     Polygon.__init__ directly in Python. C++ constructors must delegate
//     to their immediate base, so RegularPolygon exposes a `protected`
//     delegating constructor that takes pre-computed vertices; Star calls
//     that instead of the public "compute a regular polygon" constructor.
//     This preserves "Star is-a RegularPolygon" while remaining valid C++.

#include <concepts>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace lib_core::shapes {

// -- Geometric primitives ---------------------------------------------------

// A 2-D point. No Python `frozen=True` equivalent is needed since C++
// value types are copied, not aliased, by default.
struct Point3D {
    double x = 0.0;
    double y = 0.0;

    __host__ __device__ Point3D() {}
    __host__ __device__ Point3D(double x_, double y_) : x(x_), y(y_) {}

    __host__ __device__ bool operator==(const Point3D& other) const {
        return x == other.x && y == other.y;
    }

    // Euclidean distance to `other`.
    __host__ __device__ double distance_to(const Point3D& other) const;

    // Return a new point shifted by (dx, dy).
    __host__ __device__ Point3D translated(double dx, double dy) const {
        return Point3D(x + dx, y + dy);
    }
};

// A 2-D vector with the usual algebra operators.
struct Vector {
    double dx = 0.0;
    double dy = 0.0;

    __host__ __device__ Vector() {}
    __host__ __device__ Vector(double dx_, double dy_) : dx(dx_), dy(dy_) {}

    // sqrt(dx**2 + dy**2)
    __host__ __device__ double magnitude() const;

    // Angle in radians, measured from the positive X axis.
    __host__ __device__ double angle() const;

    __host__ __device__ Vector operator+(const Vector& other) const {
        return Vector(dx + other.dx, dy + other.dy);
    }

    __host__ __device__ Vector operator-(const Vector& other) const {
        return Vector(dx - other.dx, dy - other.dy);
    }

    __host__ __device__ Vector operator*(double scalar) const {
        return Vector(dx * scalar, dy * scalar);
    }

    // Unit-length vector with the same direction.
    __host__ __device__ Vector normalized() const;
};

// -- Protocol -----------------------------------------------------------

// Anything that knows its own area and perimeter. Structural, like the
// Python Protocol it mirrors -- no base class or "implements" declaration
// is required for a type to satisfy this concept.
template <typename T>
concept Shape = requires(const T& t) {
    { t.area() } -> std::convertible_to<double>;
    { t.perimeter() } -> std::convertible_to<double>;
};

// Runtime-polymorphic sibling of the `Shape` concept: needed anywhere the
// Python original stores mixed concrete shapes in one `list[Shape]` (a
// template alone cannot hold heterogeneous element types in one
// container). Every concrete shape class below inherits this, so the very
// same area()/perimeter() overrides satisfy `Shape` and `IShape` at once.
class IShape {
public:
    virtual ~IShape() = default;
    virtual double area() const = 0;
    virtual double perimeter() const = 0;
};

// -- The original four (kept so behaviour mirrors test_sample.py) ----------

// A circle with a given radius.
class Circle : public IShape {
public:
    double radius;

    __host__ __device__ explicit Circle(double radius_) : radius(radius_) {}

    // nvcc requires a virtual override's execution-space annotation to
    // match its base exactly; IShape::area/perimeter are __host__-only
    // (IShape is shared with the host-only Polygon/Ellipse family below),
    // so these overrides stay __host__-only too even though Circle is
    // otherwise a __host__ __device__ value type.
    double area() const override;
    double perimeter() const override;

    // Twice the radius.
    __host__ __device__ double diameter() const { return 2.0 * radius; }
};

// An axis-aligned rectangle with given width and height.
class Rectangle : public IShape {
public:
    double width;
    double height;

    __host__ __device__ Rectangle(double width_, double height_) : width(width_), height(height_) {}

    // See Circle's comment above re: virtual override execution-space
    // matching.
    double area() const override { return width * height; }
    double perimeter() const override { return 2.0 * (width + height); }

    // Width-to-height ratio.
    __host__ __device__ double aspect() const { return width / height; }
};

// Square is a Rectangle with equal sides -- concrete inheritance, exactly
// like the Python original (real C++ single inheritance, no workaround
// needed here since there is no diamond to worry about).
class Square : public Rectangle {
public:
    __host__ __device__ explicit Square(double side) : Rectangle(side, side) {}

    // The single side length (= width = height).
    __host__ __device__ double side() const { return width; }
};

// An arbitrary triangle via base x height (not three points).
class Triangle : public IShape {
public:
    double base;
    double height;

    __host__ __device__ Triangle(double base_, double height_) : base(base_), height(height_) {}

    // See Circle's comment above re: virtual override execution-space
    // matching.
    double area() const override { return 0.5 * base * height; }
    double perimeter() const override;
};

// -- The richer hierarchy ---------------------------------------------------

// An axis-aligned ellipse with given semi-major/semi-minor axes.
class Ellipse : public IShape {
public:
    double semi_major;
    double semi_minor;

    Ellipse(double semi_major_, double semi_minor_);

    double area() const override;
    double perimeter() const override;

    // sqrt(1 - (b/a)^2) -- cached because it never changes.
    double eccentricity() const;

private:
    mutable std::optional<double> eccentricity_cache_;
};

// A simple polygon defined by an ordered list of vertices.
//
// The polygon is assumed non-self-intersecting; results are undefined
// otherwise. area() uses the shoelace formula.
class Polygon : public IShape {
public:
    std::vector<Point3D> vertices;

    explicit Polygon(std::vector<Point3D> vertices_);

    double area() const override;
    double perimeter() const override;

    // Centroid (centre of mass for uniform density).
    Point3D centroid() const;

    // Number of vertices.
    std::size_t vertex_count() const { return vertices.size(); }

protected:
    // Delegating constructor used by RegularPolygon/Star to install an
    // already-validated vertex list without re-running the >=3 check.
    struct AlreadyValidated {};
    Polygon(std::vector<Point3D> vertices_, AlreadyValidated) : vertices(std::move(vertices_)) {}

private:
    mutable std::optional<Point3D> centroid_cache_;
};

// A regular polygon: `sides` equal-length sides around a centre.
class RegularPolygon : public Polygon {
public:
    int sides;
    double side_length;
    Point3D centre;

    RegularPolygon(int sides_, double side_length_, std::optional<Point3D> centre_ = std::nullopt);

    // Distance from centre to the midpoint of any side.
    double apothem() const;

    // Distance from centre to any vertex.
    double circumradius() const;

protected:
    // Protected delegating constructor: installs a pre-computed vertex
    // list directly (mirrors Python's Star calling Polygon.__init__
    // directly instead of RegularPolygon.__init__).
    RegularPolygon(std::vector<Point3D> vertices_, int sides_, double side_length_, Point3D centre_)
        : Polygon(std::move(vertices_), Polygon::AlreadyValidated{}),
          sides(sides_),
          side_length(side_length_),
          centre(centre_) {}
};

// An n-pointed star: a regular polygon with alternating inner vertices.
//
// Constructed by interleaving the regular polygon's outer vertices with
// inner vertices placed on a smaller concentric circle. The shoelace area
// inherited from Polygon handles the concave outline correctly.
class Star : public RegularPolygon {
public:
    int points;
    double outer_radius;
    double inner_radius;

    Star(int points_, double outer_radius_, double inner_radius_, std::optional<Point3D> centre_ = std::nullopt);
};

// A convex 3-D polyhedron, summarised by its faces and total surface area.
//
// For stress-testing the IDE the actual geometry is hand-waved -- what
// matters is the class shape, not the numeric correctness.
class Polyhedron : public IShape {
public:
    std::vector<Polygon> faces;
    double height;

    Polyhedron(std::vector<Polygon> faces_, double height_);

    // Total surface area (sum of face areas).
    double area() const override;

    // Sum of face perimeters -- not geometrically meaningful for a 3-D
    // solid but exposed for Shape conformance.
    double perimeter() const override;

    // Number of faces.
    std::size_t face_count() const { return faces.size(); }

    // Approximate volume (avg-face-area x height) -- hand-waved.
    double volume() const;

private:
    mutable std::optional<double> volume_cache_;
};

static_assert(Shape<Circle>, "Circle must satisfy the Shape concept");
static_assert(Shape<Rectangle>, "Rectangle must satisfy the Shape concept");
static_assert(Shape<Square>, "Square must satisfy the Shape concept");
static_assert(Shape<Triangle>, "Triangle must satisfy the Shape concept");
static_assert(Shape<Ellipse>, "Ellipse must satisfy the Shape concept");
static_assert(Shape<Polygon>, "Polygon must satisfy the Shape concept");
static_assert(Shape<RegularPolygon>, "RegularPolygon must satisfy the Shape concept");
static_assert(Shape<Star>, "Star must satisfy the Shape concept");
static_assert(Shape<Polyhedron>, "Polyhedron must satisfy the Shape concept");

}  // namespace lib_core::shapes
