#include "lib_utils/geometry.cuh"

#include <cmath>
#include <stdexcept>

namespace lib_utils::geometry {

double total_area(const std::vector<const IShape*>& shapes) {
    double total = 0.0;
    for (const auto* s : shapes) total += s->area();
    return total;
}

const IShape* largest(const std::vector<const IShape*>& shapes) {
    const IShape* best = shapes.at(0);
    double best_area = best->area();
    for (std::size_t i = 1; i < shapes.size(); ++i) {
        double area = shapes[i]->area();
        if (area > best_area) {
            best = shapes[i];
            best_area = area;
        }
    }
    return best;
}

namespace {
BoundingBox polygon_bounds(const std::vector<Point3D>& vertices) {
    if (vertices.empty()) return BoundingBox{0.0, 0.0, 0.0, 0.0};
    double lo_x = vertices[0].x, hi_x = vertices[0].x;
    double lo_y = vertices[0].y, hi_y = vertices[0].y;
    for (const auto& v : vertices) {
        lo_x = std::min(lo_x, v.x);
        hi_x = std::max(hi_x, v.x);
        lo_y = std::min(lo_y, v.y);
        hi_y = std::max(hi_y, v.y);
    }
    return BoundingBox{lo_x, lo_y, hi_x, hi_y};
}
}  // namespace

BoundingBox bounding_box(const IShape& shape) {
    if (const auto* c = dynamic_cast<const Circle*>(&shape)) {
        return BoundingBox{-c->radius, -c->radius, c->radius, c->radius};
    }
    // Square must be checked before Rectangle -- it publicly inherits
    // Rectangle, so a Rectangle-shaped dynamic_cast would also match a
    // Square (mirrors Python's match/case ordering, where the more
    // specific pattern is listed first).
    if (const auto* sq = dynamic_cast<const Square*>(&shape)) {
        double s = sq->side();
        return BoundingBox{0.0, 0.0, s, s};
    }
    if (const auto* r = dynamic_cast<const Rectangle*>(&shape)) {
        return BoundingBox{0.0, 0.0, r->width, r->height};
    }
    if (const auto* t = dynamic_cast<const Triangle*>(&shape)) {
        return BoundingBox{0.0, 0.0, t->base, t->height};
    }
    if (const auto* e = dynamic_cast<const Ellipse*>(&shape)) {
        return BoundingBox{-e->semi_major, -e->semi_minor, e->semi_major, e->semi_minor};
    }
    // Star / RegularPolygon must be checked before the plain Polygon
    // case, same reasoning as Square-before-Rectangle above.
    if (const auto* star = dynamic_cast<const Star*>(&shape)) {
        return polygon_bounds(star->vertices);
    }
    if (const auto* rp = dynamic_cast<const RegularPolygon*>(&shape)) {
        return polygon_bounds(rp->vertices);
    }
    if (const auto* p = dynamic_cast<const Polygon*>(&shape)) {
        return polygon_bounds(p->vertices);
    }
    // Unknown shape -- fallback to a square approximation centred on
    // origin with side = sqrt(area).
    double side = std::sqrt(std::max(shape.area(), 0.0));
    return BoundingBox{0.0, 0.0, side, side};
}

BoundingBox total_extent(const std::vector<const IShape*>& shapes) {
    std::optional<BoundingBox> extent;
    for (const auto* shape : shapes) {
        BoundingBox bb = bounding_box(*shape);
        extent = extent.has_value() ? extent->union_with(bb) : bb;
    }
    return extent.value_or(BoundingBox{0.0, 0.0, 0.0, 0.0});
}

double angle_between(const Vector& a, const Vector& b) {
    if (a.magnitude() == 0.0 || b.magnitude() == 0.0) return 0.0;
    double cos_theta = std::max(-1.0, std::min(1.0, dot(a, b) / (a.magnitude() * b.magnitude())));
    return std::acos(cos_theta);
}

Vector project(const Vector& a, const Vector& onto) {
    if (onto.magnitude() == 0.0) return Vector(0.0, 0.0);
    double scalar = dot(a, onto) / (onto.magnitude() * onto.magnitude());
    return Vector(onto.dx * scalar, onto.dy * scalar);
}

Polygon transform_polygon(const Polygon& polygon, const Matrix2x3& matrix) {
    std::vector<Point3D> transformed;
    transformed.reserve(polygon.vertices.size());
    for (const auto& v : polygon.vertices) transformed.push_back(matrix.apply(v));
    return Polygon(std::move(transformed));
}

std::vector<Point3D> convex_hull(std::vector<Point3D> points) {
    if (points.size() <= 1) return points;
    std::sort(points.begin(), points.end(), [](const Point3D& a, const Point3D& b) {
        return a.x != b.x ? a.x < b.x : a.y < b.y;
    });

    auto cross = [](const Point3D& o, const Point3D& a, const Point3D& b) {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    };

    std::vector<Point3D> lower;
    for (const auto& p : points) {
        while (lower.size() >= 2 && cross(lower[lower.size() - 2], lower.back(), p) <= 0) {
            lower.pop_back();
        }
        lower.push_back(p);
    }
    std::vector<Point3D> upper;
    for (auto it = points.rbegin(); it != points.rend(); ++it) {
        while (upper.size() >= 2 && cross(upper[upper.size() - 2], upper.back(), *it) <= 0) {
            upper.pop_back();
        }
        upper.push_back(*it);
    }
    lower.pop_back();
    upper.pop_back();
    lower.insert(lower.end(), upper.begin(), upper.end());
    return lower;
}

Point3D centroid_of(const Polygon& polygon) {
    std::size_t n = polygon.vertex_count();
    if (n == 0) return Point3D(0.0, 0.0);
    double cx = 0.0, cy = 0.0, a_total = 0.0;
    for (std::size_t i = 0; i < n; ++i) {
        const Point3D& v0 = polygon.vertices[i];
        const Point3D& v1 = polygon.vertices[(i + 1) % n];
        double cr = v0.x * v1.y - v1.x * v0.y;
        a_total += cr;
        cx += (v0.x + v1.x) * cr;
        cy += (v0.y + v1.y) * cr;
    }
    if (a_total == 0.0) {
        double sx = 0.0, sy = 0.0;
        for (const auto& v : polygon.vertices) {
            sx += v.x;
            sy += v.y;
        }
        return Point3D(sx / n, sy / n);
    }
    a_total *= 0.5;
    return Point3D(cx / (6 * a_total), cy / (6 * a_total));
}

}  // namespace lib_utils::geometry
