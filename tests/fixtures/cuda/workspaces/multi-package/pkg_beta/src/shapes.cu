#include "shapes.cuh"

__host__ __device__ Circle::Circle(double r) : r_(r) {}

__host__ __device__ double Circle::area() const {
    return 3.14159 * r_ * r_;
}

static_assert(Shape<Circle>, "Circle must satisfy the Shape concept");
