#pragma once

#include <concepts>

// typing.Protocol -> a C++20 concept: structural, not nominal, typing -
// Circle satisfies Shape purely by having a matching area() method, with
// no inheritance relationship required (the closest analog to Protocol).
template <typename T>
concept Shape = requires(const T &t) {
    { t.area() } -> std::convertible_to<double>;
};

class Circle {
public:
    __host__ __device__ explicit Circle(double r);
    __host__ __device__ double area() const;

private:
    double r_;
};
