#pragma once

#include <concepts>

template <typename T>
concept Shape = requires(const T &t) {
    { t.area() } -> std::convertible_to<double>;
    { t.perimeter() } -> std::convertible_to<double>;
};

class Circle {
public:
    __host__ __device__ explicit Circle(double radius);
    __host__ __device__ double area() const;
    __host__ __device__ double perimeter() const;

private:
    double radius_;
};

class Square {
public:
    __host__ __device__ explicit Square(double side);
    __host__ __device__ double area() const;
    __host__ __device__ double perimeter() const;

private:
    double side_;
};
