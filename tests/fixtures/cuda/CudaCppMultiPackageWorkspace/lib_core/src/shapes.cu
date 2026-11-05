#include "lib_core/shapes.cuh"

#include <cmath>
#include <numbers>

namespace lib_core::shapes {

__host__ __device__ double Point3D::distance_to(const Point3D& other) const {
    double dx = x - other.x;
    double dy = y - other.y;
#ifdef __CUDA_ARCH__
    return hypot(dx, dy);
#else
    return std::hypot(dx, dy);
#endif
}

__host__ __device__ double Vector::magnitude() const {
#ifdef __CUDA_ARCH__
    return hypot(dx, dy);
#else
    return std::hypot(dx, dy);
#endif
}

__host__ __device__ double Vector::angle() const {
#ifdef __CUDA_ARCH__
    return atan2(dy, dx);
#else
    return std::atan2(dy, dx);
#endif
}

__host__ __device__ Vector Vector::normalized() const {
    double m = magnitude();
    if (m == 0.0) {
        return Vector(0.0, 0.0);
    }
    return Vector(dx / m, dy / m);
}

double Circle::area() const {
    return std::numbers::pi * radius * radius;
}

double Circle::perimeter() const {
    return 2.0 * std::numbers::pi * radius;
}

double Triangle::perimeter() const {
    // Without all three sides we can't compute this exactly. For the
    // right-angled case where base perp height, the hypotenuse is:
#ifdef __CUDA_ARCH__
    double hyp = hypot(base, height);
#else
    double hyp = std::hypot(base, height);
#endif
    return base + height + hyp;
}

Ellipse::Ellipse(double semi_major_, double semi_minor_) {
    if (semi_major_ < semi_minor_) {
        std::swap(semi_major_, semi_minor_);
    }
    semi_major = semi_major_;
    semi_minor = semi_minor_;
}

double Ellipse::area() const {
    return std::numbers::pi * semi_major * semi_minor;
}

double Ellipse::perimeter() const {
    // Ramanujan's approximation -- exact closed form needs an elliptic
    // integral.
    double a = semi_major;
    double b = semi_minor;
    double h = ((a - b) * (a - b)) / ((a + b) * (a + b));
    return std::numbers::pi * (a + b) * (1.0 + (3.0 * h) / (10.0 + std::sqrt(4.0 - 3.0 * h)));
}

double Ellipse::eccentricity() const {
    if (!eccentricity_cache_.has_value()) {
        double ratio = semi_minor / semi_major;
        eccentricity_cache_ = std::sqrt(1.0 - ratio * ratio);
    }
    return *eccentricity_cache_;
}

Polygon::Polygon(std::vector<Point3D> vertices_) {
    if (vertices_.size() < 3) {
        throw std::invalid_argument("polygon needs >=3 vertices, got " + std::to_string(vertices_.size()));
    }
    vertices = std::move(vertices_);
}

double Polygon::area() const {
    // Shoelace.
    std::size_t n = vertices.size();
    double acc = 0.0;
    for (std::size_t i = 0; i < n; ++i) {
        double x0 = vertices[i].x;
        double y0 = vertices[i].y;
        double x1 = vertices[(i + 1) % n].x;
        double y1 = vertices[(i + 1) % n].y;
        acc += x0 * y1 - x1 * y0;
    }
    return std::abs(acc) / 2.0;
}

double Polygon::perimeter() const {
    std::size_t n = vertices.size();
    double total = 0.0;
    for (std::size_t i = 0; i < n; ++i) {
        total += vertices[i].distance_to(vertices[(i + 1) % n]);
    }
    return total;
}

Point3D Polygon::centroid() const {
    if (!centroid_cache_.has_value()) {
        double cx = 0.0;
        double cy = 0.0;
        for (const auto& v : vertices) {
            cx += v.x;
            cy += v.y;
        }
        centroid_cache_ = Point3D(cx / vertices.size(), cy / vertices.size());
    }
    return *centroid_cache_;
}

namespace {
std::vector<Point3D> regular_polygon_vertices(int sides, double side_length, const Point3D& centre) {
    double radius = side_length / (2.0 * std::sin(std::numbers::pi / sides));
    std::vector<Point3D> vertices;
    vertices.reserve(static_cast<std::size_t>(sides));
    for (int i = 0; i < sides; ++i) {
        double theta = 2.0 * std::numbers::pi * i / sides;
        vertices.emplace_back(centre.x + radius * std::cos(theta), centre.y + radius * std::sin(theta));
    }
    return vertices;
}
}  // namespace

RegularPolygon::RegularPolygon(int sides_, double side_length_, std::optional<Point3D> centre_)
    : Polygon(regular_polygon_vertices(
          [sides_]() {
              if (sides_ < 3) {
                  throw std::invalid_argument("regular polygon needs >=3 sides, got " + std::to_string(sides_));
              }
              return sides_;
          }(),
          side_length_,
          centre_.value_or(Point3D(0.0, 0.0)))),
      sides(sides_),
      side_length(side_length_),
      centre(centre_.value_or(Point3D(0.0, 0.0))) {}

double RegularPolygon::apothem() const {
    return side_length / (2.0 * std::tan(std::numbers::pi / sides));
}

double RegularPolygon::circumradius() const {
    return side_length / (2.0 * std::sin(std::numbers::pi / sides));
}

namespace {
std::vector<Point3D> star_vertices(int points, double outer_radius, double inner_radius, const Point3D& centre) {
    if (points < 3) {
        throw std::invalid_argument("star needs >=3 points, got " + std::to_string(points));
    }
    int n = 2 * points;
    std::vector<Point3D> vertices;
    vertices.reserve(static_cast<std::size_t>(n));
    for (int i = 0; i < n; ++i) {
        double r = (i % 2 == 0) ? outer_radius : inner_radius;
        double theta = std::numbers::pi * i / points;
        vertices.emplace_back(centre.x + r * std::cos(theta), centre.y + r * std::sin(theta));
    }
    return vertices;
}
}  // namespace

Star::Star(int points_, double outer_radius_, double inner_radius_, std::optional<Point3D> centre_)
    // Skips RegularPolygon's public "compute a regular polygon" constructor
    // and calls Polygon.__init__ directly via RegularPolygon's protected
    // delegating constructor -- mirrors Python's
    // `Polygon.__init__(self, vertices)` call inside Star.__init__.
    : RegularPolygon(
          star_vertices(points_, outer_radius_, inner_radius_, centre_.value_or(Point3D(0.0, 0.0))),
          2 * points_,
          0.0,  // patched below once vertices exist
          centre_.value_or(Point3D(0.0, 0.0))),
      points(points_),
      outer_radius(outer_radius_),
      inner_radius(inner_radius_) {
    side_length = vertices[0].distance_to(vertices[1]);
}

Polyhedron::Polyhedron(std::vector<Polygon> faces_, double height_) : faces(std::move(faces_)), height(height_) {
    if (faces.empty()) {
        throw std::invalid_argument("polyhedron needs >=1 face");
    }
}

double Polyhedron::area() const {
    double total = 0.0;
    for (const auto& f : faces) {
        total += f.area();
    }
    return total;
}

double Polyhedron::perimeter() const {
    double total = 0.0;
    for (const auto& f : faces) {
        total += f.perimeter();
    }
    return total;
}

double Polyhedron::volume() const {
    if (!volume_cache_.has_value()) {
        double total = 0.0;
        for (const auto& f : faces) {
            total += f.area();
        }
        double avg_area = total / faces.size();
        volume_cache_ = avg_area * height;
    }
    return *volume_cache_;
}

}  // namespace lib_core::shapes
