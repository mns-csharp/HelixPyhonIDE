#pragma once

#include <concepts>

// OOP demo for the class-diagram designer: an interface (Protocol -> concept),
// an abstract base (ABC -> abstract class), and a concrete subclass — so the
// category tabs and inheritance edges have something to render.
namespace pythonsimple::shapes_demo {

// Drawable (Protocol) -> a C++20 concept. Nothing currently models it (just
// like the Python Circle never implements draw()); it exists structurally so
// the designer has an "interface" node to draw.
template <typename T>
concept Drawable = requires(const T &t) {
    { t.draw() };
};

// Shape (ABC) -> abstract base class with a pure-virtual area().
class Shape {
public:
    __host__ __device__ virtual ~Shape() = default;
    __host__ __device__ virtual double area() const = 0;
};

// Circle (concrete) -> small value type. Qualified __host__ __device__ on
// every method (matching the workspaces fixture's convention for small
// value types) even though nothing currently launches a kernel using it.
class Circle : public Shape {
public:
    __host__ __device__ explicit Circle(double radius);
    __host__ __device__ double area() const override;

private:
    double radius_;
};

} // namespace pythonsimple::shapes_demo
