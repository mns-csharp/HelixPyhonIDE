#include "lib_core/models.cuh"

#include <cmath>

namespace lib_core::models {

__host__ __device__ double Coordinate::magnitude() const {
#ifdef __CUDA_ARCH__
    return sqrt(x * x + y * y);
#else
    return std::sqrt(x * x + y * y);
#endif
}

__host__ __device__ double Coordinate3D::magnitude() const {
#ifdef __CUDA_ARCH__
    return sqrt(x * x + y * y + z * z);
#else
    return std::sqrt(x * x + y * y + z * z);
#endif
}

// Explicit instantiations of the common specializations used elsewhere in
// this workspace -- proves the templates above compile cleanly for real
// argument types (mirrors the `static_assert(Shape<Circle>, ...)` pattern
// used in shapes.cuh).
template class Box<int>;
template class Pair<int, std::string>;
template class Ok<int>;
template class Err<std::string>;

}  // namespace lib_core::models
