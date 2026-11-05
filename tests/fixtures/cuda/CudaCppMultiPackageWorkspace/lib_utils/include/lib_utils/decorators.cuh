#pragma once

// Decorator-factory zoo: retry, timed, memoize, traced, require_role.
//
// Mirrors lib_utils/decorators.py. Python's decorator-factory pattern
// (`@retry(times=3)` wraps a callable and returns a new one with the same
// signature, via `ParamSpec` + `TypeVar` so type information survives the
// wrap) becomes ordinary C++ higher-order functions: each one takes a
// callable `fn` and returns a lambda that closes over it -- template
// argument deduction is the direct analogue of `ParamSpec`/`TypeVar` here,
// since the wrapper's call signature is deduced from `fn` itself rather
// than declared.
//
// `async_retry` (the async sibling of `retry`) collapses onto `retry`
// itself, per this workspace's "represent async as synchronous" policy
// (see animals.cuh) -- once both are synchronous they are the same
// function, so no separate copy is kept (matching the Rust sibling's
// `async_ops::retry_async` -> `decorators::retry` re-export).
//
// API-shape note: Python's `chain_decorators(a, b)(fn)` applies `a`
// outermost. The C++ `chain_decorators` below takes `fn` as its first
// argument (`chain_decorators(fn, a, b)`) purely because C++ template
// argument deduction needs the callable to establish `F`'s type before
// the variadic decorator pack; the composition order (`a(b(fn))`) is
// preserved exactly.

#include <chrono>
#include <cstdio>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <tuple>
#include <type_traits>
#include <vector>

namespace lib_utils::decorators {

// -- @retry -----------------------------------------------------------------

// Retry the wrapped callable up to `times` total attempts.
//
// `times`: maximum number of attempts (1 = "no retry").
// `delay`: seconds to wait before the second attempt.
// `backoff`: multiplier applied to `delay` between successive retries.
// Simplification: Python's `on=(ExcType, ...)` narrows which exception
// types trigger a retry (anything else propagates immediately); this
// catches any exception, since every call site in this workspace already
// only ever throws the one exception type it expects to retry on.
template <typename F>
auto retry(F fn, int times = 3, double delay = 0.0, double backoff = 1.0) {
    if (times < 1) {
        throw std::invalid_argument("times must be >=1, got " + std::to_string(times));
    }
    return [fn, times, delay, backoff](auto&&... args) mutable -> decltype(fn(args...)) {
        std::exception_ptr last_exc;
        double current_delay = delay;
        for (int attempt = 0; attempt < times; ++attempt) {
            try {
                return fn(std::forward<decltype(args)>(args)...);
            } catch (...) {
                last_exc = std::current_exception();
                if (attempt + 1 == times) break;
                if (current_delay > 0) {
                    std::this_thread::sleep_for(std::chrono::duration<double>(current_delay));
                    current_delay *= backoff;
                }
            }
        }
        std::rethrow_exception(last_exc);
    };
}

// -- @timed -----------------------------------------------------------------

// Measure the wrapped callable's wall-clock duration, reporting it via
// `sink(label, seconds)` if provided, else printing it.
template <typename F>
auto timed(F fn, const std::string& label = "", std::function<void(const std::string&, double)> sink = nullptr) {
    return [fn, label, sink](auto&&... args) mutable -> decltype(fn(args...)) {
        auto start = std::chrono::steady_clock::now();
        auto report = [&] {
            double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
            if (sink) {
                sink(label, elapsed);
            } else {
                std::printf("[timed] %s took %.3f ms\n", label.c_str(), elapsed * 1000.0);
            }
        };
        try {
            if constexpr (std::is_void_v<decltype(fn(args...))>) {
                fn(std::forward<decltype(args)>(args)...);
                report();
            } else {
                auto result = fn(std::forward<decltype(args)>(args)...);
                report();
                return result;
            }
        } catch (...) {
            report();
            throw;
        }
    };
}

// -- @memoize ---------------------------------------------------------------

// Cache the wrapped callable's results keyed by its argument tuple. When
// `max_size` is set, the oldest entry is evicted on overflow (FIFO, same
// as the Python original).
template <typename R, typename... Args>
std::function<R(Args...)> memoize(std::function<R(Args...)> fn, std::optional<std::size_t> max_size = std::nullopt) {
    auto cache = std::make_shared<std::map<std::tuple<Args...>, R>>();
    auto order = std::make_shared<std::vector<std::tuple<Args...>>>();
    return [fn, cache, order, max_size](Args... args) -> R {
        auto key = std::make_tuple(args...);
        auto it = cache->find(key);
        if (it != cache->end()) return it->second;
        R value = fn(args...);
        (*cache)[key] = value;
        order->push_back(key);
        if (max_size.has_value() && order->size() > *max_size) {
            auto evict = order->front();
            order->erase(order->begin());
            cache->erase(evict);
        }
        return value;
    };
}

// -- @traced ----------------------------------------------------------------

// Log entry/exit of the wrapped callable via `sink` (defaults to
// std::cout).
template <typename F>
auto traced(F fn, const std::string& name, std::function<void(const std::string&)> sink = nullptr,
            bool include_args = true) {
    auto emit = sink ? sink : std::function<void(const std::string&)>([](const std::string& s) { std::cout << s << "\n"; });
    return [fn, name, emit, include_args](auto&&... args) mutable -> decltype(fn(args...)) {
        emit(include_args ? ("-> " + name + "(...)") : ("-> " + name));
        try {
            if constexpr (std::is_void_v<decltype(fn(args...))>) {
                fn(std::forward<decltype(args)>(args)...);
                emit("<- " + name);
            } else {
                auto result = fn(std::forward<decltype(args)>(args)...);
                emit("<- " + name);
                return result;
            }
        } catch (const std::exception& exc) {
            emit("!! " + name + " raised " + std::string(typeid(exc).name()));
            throw;
        }
    };
}

// -- @require_role ----------------------------------------------------------

// Raised by require_role() when the caller lacks the role.
class RoleDeniedError : public std::runtime_error {
public:
    explicit RoleDeniedError(const std::string& message) : std::runtime_error(message) {}
};

// Module-level "active roles" registry -- replace with thread-local in
// real code. Kept simple here for stress-test purposes, matching Python.
inline std::vector<std::string>& active_roles() {
    static std::vector<std::string> roles;
    return roles;
}

inline void set_active_roles(std::vector<std::string> roles) { active_roles() = std::move(roles); }

// Decorator: throw RoleDeniedError if the caller doesn't hold `role`.
template <typename F>
auto require_role(F fn, const std::string& role, const std::string& fn_name) {
    return [fn, role, fn_name](auto&&... args) mutable -> decltype(fn(args...)) {
        const auto& roles = active_roles();
        bool has_role = false;
        for (const auto& r : roles) {
            if (r == role) {
                has_role = true;
                break;
            }
        }
        if (!has_role) {
            throw RoleDeniedError(fn_name + " requires role '" + role + "'");
        }
        return fn(std::forward<decltype(args)>(args)...);
    };
}

// -- Composition utilities --------------------------------------------------

// Base case for the recursive chain below.
template <typename F>
F chain_decorators(F fn) {
    return fn;
}

// Apply decorators left-to-right: chain_decorators(fn, a, b) == a(b(fn)).
template <typename F, typename D, typename... Rest>
auto chain_decorators(F fn, D decorator, Rest... rest) {
    return decorator(chain_decorators(fn, rest...));
}

}  // namespace lib_utils::decorators
