#include "shapes_demo.cuh"

namespace pythonsimple::shapes_demo {

__host__ __device__ Circle::Circle(double radius) : radius_(radius) {}

__host__ __device__ double Circle::area() const {
    return 3.14159 * radius_ * radius_;
}

} // namespace pythonsimple::shapes_demo
