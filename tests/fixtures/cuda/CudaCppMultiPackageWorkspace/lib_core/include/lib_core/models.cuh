#pragma once

// Generic value types and domain models for lib_core.
//
// Mirrors lib_core/models.py. Translation notes:
//   - Generic[T] dataclasses (Box, Pair, Ok, Err) -> class templates.
//   - NamedTuple (Coordinate, Coordinate3D, Range) -> small
//     __host__ __device__ value structs (they are exactly the
//     "frequently-copied value types" the CUDA style guide calls out).
//   - __post_init__ validation (UserId, Email, User) -> validation moved
//     into the constructor body, throwing the same FieldValidationError /
//     CrossFieldValidationError types as Python.
//   - `Result = Ok[T] | Err[E]` (a PEP 604 union) -> `std::variant<Ok<T>,
//     Err<E>>` aliased as `Result<T, E>`, the direct analogue of a
//     two-armed sum type.

#include <algorithm>
#include <exception>
#include <optional>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
#include <variant>
#include <vector>

#include "lib_core/errors.cuh"

namespace lib_core::models {

// -- Generic value containers -----------------------------------------------

// Single-value container -- useful as a uniform wrapper around a T.
template <typename T>
class Box {
public:
    explicit Box(T value) : value(std::move(value)) {}

    // Apply `fn` to the inner value, returning a new Box.
    template <typename F>
    auto map(F fn) const -> Box<decltype(std::declval<F>()(std::declval<T>()))> {
        return Box<decltype(std::declval<F>()(std::declval<T>()))>(fn(value));
    }

    const T& unwrap() const { return value; }

    T value;
};

// Heterogeneous two-tuple with named fields. Unlike std::pair, `.first`
// keeps the same name Python used, and `swapped()` mirrors the Python
// convenience method.
template <typename A, typename B>
class Pair {
public:
    Pair(A first, B second) : first(std::move(first)), second(std::move(second)) {}

    Pair<B, A> swapped() const { return Pair<B, A>(second, first); }

    std::pair<A, B> to_tuple() const { return {first, second}; }

    A first;
    B second;
};

// -- Coordinate / range value types ------------------------------------

// 2-D coordinate; NamedTuple in Python for tuple-like unpacking. C++ has
// no unpacking equivalent worth reproducing, so this is a plain value
// struct.
struct Coordinate {
    double x = 0.0;
    double y = 0.0;

    __host__ __device__ Coordinate shifted(double dx, double dy) const { return Coordinate{x + dx, y + dy}; }

    __host__ __device__ double magnitude() const;
};

// 3-D coordinate.
struct Coordinate3D {
    double x = 0.0;
    double y = 0.0;
    double z = 0.0;

    __host__ __device__ Coordinate3D shifted(double dx, double dy, double dz) const {
        return Coordinate3D{x + dx, y + dy, z + dz};
    }

    __host__ __device__ double magnitude() const;
};

// Closed numeric range [lo, hi].
struct Range {
    double lo = 0.0;
    double hi = 0.0;

    __host__ __device__ double width() const { return hi - lo; }

    // Inclusive containment check.
    __host__ __device__ bool contains(double value) const { return lo <= value && value <= hi; }

    // Open-set overlap predicate.
    __host__ __device__ bool overlaps(const Range& other) const { return lo <= other.hi && other.lo <= hi; }
};

// -- Domain models with constructor validation -------------------------

// Strongly-typed user identifier -- defeats accidental swap with a bare int.
class UserId {
public:
    explicit UserId(int value) : value(value) {
        if (value <= 0) {
            throw lib_core::errors::FieldValidationError("value", "must be positive, got " + std::to_string(value));
        }
    }

    int value;
};

// Validated email address. Intentionally minimal -- real world would use
// a proper validator, but for stress-testing we just check the @.
class Email {
public:
    explicit Email(std::string address) : address(std::move(address)) {
        if (this->address.find('@') == std::string::npos) {
            throw lib_core::errors::FieldValidationError("address", "missing @");
        }
        if (this->address.front() == '@' || this->address.back() == '@') {
            throw lib_core::errors::FieldValidationError("address", "@ at boundary");
        }
    }

    // Everything after the @.
    std::string domain() const { return address.substr(address.find('@') + 1); }

    // Everything before the @.
    std::string local_part() const { return address.substr(0, address.find('@')); }

    std::string address;
};

// Mutable user record -- id is immutable, profile fields can change.
//
// Demonstrates cross-field validation: display_name defaults to the
// email's local part if not provided.
class User {
public:
    User(UserId user_id, Email email, std::string display_name = "", bool is_active = true,
         std::vector<std::string> tags = {})
        : user_id(std::move(user_id)),
          email(email),
          display_name(std::move(display_name)),
          is_active(is_active),
          tags(std::move(tags)) {
        if (this->display_name.empty()) {
            this->display_name = email.local_part();
        }
        if (this->is_active && this->email.address.empty()) {
            throw lib_core::errors::CrossFieldValidationError({"is_active", "email"},
                                                                "active users need a valid email");
        }
    }

    // Mark the user inactive. Idempotent.
    void deactivate() { is_active = false; }

    // Add `label` to tags if not already present.
    void tag(const std::string& label) {
        if (std::find(tags.begin(), tags.end(), label) == tags.end()) {
            tags.push_back(label);
        }
    }

    UserId user_id;
    Email email;
    std::string display_name;
    bool is_active;
    std::vector<std::string> tags;
};

// -- Result / Either sum type -----------------------------------------------

// Success branch of a Result.
template <typename T>
class Ok {
public:
    explicit Ok(T value) : value(std::move(value)) {}

    bool is_ok() const { return true; }
    bool is_err() const { return false; }
    const T& unwrap() const { return value; }
    T unwrap_or(T /*default_value*/) const { return value; }

    template <typename F>
    Ok<decltype(std::declval<F>()(std::declval<T>()))> map(F fn) const {
        return Ok<decltype(fn(value))>(fn(value));
    }

    T value;
};

// Error branch of a Result.
template <typename E>
class Err {
public:
    explicit Err(E error) : error(std::move(error)) {}

    bool is_ok() const { return false; }
    bool is_err() const { return true; }

    // Re-raises the wrapped error if it is an exception, else raises a
    // generic runtime_error -- mirrors Python's unwrap() re-raise logic.
    template <typename T>
    [[noreturn]] T unwrap() const {
        if constexpr (std::is_base_of_v<std::exception, E>) {
            throw error;
        } else {
            throw std::runtime_error("unwrap on Err(...)");
        }
    }

    template <typename T>
    T unwrap_or(T default_value) const {
        return default_value;
    }

    // Identity -- Err.map ignores the function (it can't run on an error).
    template <typename F>
    Err<E> map(F /*fn*/) const {
        return *this;
    }

    E error;
};

// A two-armed sum type: `Result = Ok[T] | Err[E]` in Python becomes a
// std::variant of the two branches.
template <typename T, typename E>
using Result = std::variant<Ok<T>, Err<E>>;

template <typename T, typename E>
bool is_ok(const Result<T, E>& r) {
    return std::holds_alternative<Ok<T>>(r);
}

// -- Convenience helpers -----------------------------------------------------

// Construct an Ok result. Sugar over Ok(value).
template <typename T>
Ok<T> ok(T value) {
    return Ok<T>(std::move(value));
}

// Construct an Err result. Sugar over Err(error).
template <typename E>
Err<E> err(E error) {
    return Err<E>(std::move(error));
}

// Return the first Ok in the list, or an Err with all collected errors.
template <typename T, typename E>
Result<T, std::vector<E>> first_ok(const std::vector<Result<T, E>>& results) {
    std::vector<E> errors;
    for (const auto& r : results) {
        if (std::holds_alternative<Ok<T>>(r)) {
            return Ok<T>(std::get<Ok<T>>(r).value);
        }
        errors.push_back(std::get<Err<E>>(r).error);
    }
    return Err<std::vector<E>>(errors);
}

// Return Ok([all values]) if every result is Ok, else the first Err.
template <typename T, typename E>
Result<std::vector<T>, E> all_ok(const std::vector<Result<T, E>>& results) {
    std::vector<T> values;
    for (const auto& r : results) {
        if (std::holds_alternative<Err<E>>(r)) {
            return Err<E>(std::get<Err<E>>(r).error);
        }
        values.push_back(std::get<Ok<T>>(r).value);
    }
    return Ok<std::vector<T>>(values);
}

}  // namespace lib_core::models
