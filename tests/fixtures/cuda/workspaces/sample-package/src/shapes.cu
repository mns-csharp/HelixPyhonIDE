#include "shapes.cuh"

__host__ __device__ Circle::Circle(double radius) : radius_(radius) {}

__host__ __device__ double Circle::area() const {
    return 3.14159 * radius_ * radius_;
}

__host__ __device__ double Circle::perimeter() const {
    return 2 * 3.14159 * radius_;
}

__host__ __device__ Square::Square(double side) : side_(side) {}

__host__ __device__ double Square::area() const {
    return side_ * side_;
}

__host__ __device__ double Square::perimeter() const {
    return 4 * side_;
}

static_assert(Shape<Circle>, "Circle must satisfy the Shape concept");
static_assert(Shape<Square>, "Square must satisfy the Shape concept");
