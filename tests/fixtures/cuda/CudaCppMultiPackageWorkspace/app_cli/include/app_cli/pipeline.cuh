#pragma once

// Generic pipeline of typed transformation steps.
//
// Mirrors app_cli/pipeline.py. Translation notes:
//   - Python's `Step[I, O]` lets EVERY stage change both its input and
//     output type, composed via `__rshift__` into a `Pipeline[I, O]`
//     whose own I/O types are inferred from the whole chain -- a fully
//     dynamic, type-erased-at-runtime chain despite the static
//     annotations (the source file itself is littered with
//     `# type: ignore` at nearly every use of `.run()`/`>>`, since even
//     Python's own type checker cannot verify it). Reproducing that full
//     generality statically would need per-stage type erasure
//     (std::any boxing, as `commands.cuh`'s CommandBus uses for its
//     payloads) for comparatively little payoff, since every pipeline
//     actually built in this workspace (`make_demo_pipeline`) only ever
//     changes ONE type: `T` flows through Filter/Map/Take unchanged, and
//     only the terminal `Reduce` step collapses it to a scalar `R`. This
//     translation models exactly that shape: `Step<T>` stages transform
//     `vector<T> -> vector<T>`, and a single terminal `Reduce<T, R>`
//     collapses to `R` -- still a real ABC + several concrete subclasses
//     (Filter/Map/Tap/Take/Sorted/Distinct) for the "class hierarchy
//     folding" stress test, still a generic class with multiple
//     TypeVars (`Pipeline<T, R>`), and the `>>` operator overload is
//     preserved on `StepChain<T>` -- crucially, its RETURN TYPE differs
//     by overload (`StepChain<T> >> Step` stays a `StepChain<T>`, but
//     `StepChain<T> >> Reduce<T,R>` becomes a `Pipeline<T,R>`), which is
//     exactly the "operator overloading with non-trivial type returns"
//     stress point the Python docstring calls out.

#include <algorithm>
#include <functional>
#include <memory>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace app_cli::pipeline {

// -- Step ABC ----------------------------------------------------------

// One node of a pipeline: transforms vector<T> into vector<T>. Steps are
// pure: the same input always produces the same output, so pipelines are
// safe to re-run for replay/debugging.
template <typename T>
class Step {
public:
    explicit Step(std::string name) : step_name(std::move(name)) {}
    virtual ~Step() = default;

    virtual std::vector<T> run(const std::vector<T>& source) const = 0;

    std::string step_name;
};

// -- Concrete steps ------------------------------------------------------

// Yield items where `pred` is truthy.
template <typename T>
class Filter : public Step<T> {
public:
    Filter(std::function<bool(const T&)> pred, std::string name = "filter")
        : Step<T>(std::move(name)), pred_(std::move(pred)) {}

    std::vector<T> run(const std::vector<T>& source) const override {
        std::vector<T> out;
        for (const auto& item : source) {
            if (pred_(item)) out.push_back(item);
        }
        return out;
    }

private:
    std::function<bool(const T&)> pred_;
};

// Apply `fn` to every item (same-type map -- see file header).
template <typename T>
class Map : public Step<T> {
public:
    Map(std::function<T(const T&)> fn, std::string name = "map") : Step<T>(std::move(name)), fn_(std::move(fn)) {}

    std::vector<T> run(const std::vector<T>& source) const override {
        std::vector<T> out;
        out.reserve(source.size());
        for (const auto& item : source) out.push_back(fn_(item));
        return out;
    }

private:
    std::function<T(const T&)> fn_;
};

// Inspect each item without modifying the stream.
template <typename T>
class Tap : public Step<T> {
public:
    Tap(std::function<void(const T&)> observer, std::string name = "tap")
        : Step<T>(std::move(name)), observer_(std::move(observer)) {}

    std::vector<T> run(const std::vector<T>& source) const override {
        for (const auto& item : source) observer_(item);
        return source;
    }

private:
    std::function<void(const T&)> observer_;
};

// Keep only the first `n` items.
template <typename T>
class Take : public Step<T> {
public:
    Take(int n, std::string name = "take") : Step<T>(std::move(name)), n_(n) {
        if (n < 0) throw std::invalid_argument("n must be non-negative, got " + std::to_string(n));
    }

    std::vector<T> run(const std::vector<T>& source) const override {
        std::vector<T> out;
        for (std::size_t i = 0; i < source.size() && static_cast<int>(i) < n_; ++i) out.push_back(source[i]);
        return out;
    }

private:
    int n_;
};

// Sort items by an optional key function.
template <typename T>
class Sorted : public Step<T> {
public:
    Sorted(std::function<bool(const T&, const T&)> less = nullptr, std::string name = "sorted")
        : Step<T>(std::move(name)), less_(std::move(less)) {}

    std::vector<T> run(const std::vector<T>& source) const override {
        std::vector<T> out = source;
        if (less_) {
            std::sort(out.begin(), out.end(), less_);
        } else {
            std::sort(out.begin(), out.end());
        }
        return out;
    }

private:
    std::function<bool(const T&, const T&)> less_;
};

// Drop duplicates while preserving order.
template <typename T>
class Distinct : public Step<T> {
public:
    explicit Distinct(std::string name = "distinct") : Step<T>(std::move(name)) {}

    std::vector<T> run(const std::vector<T>& source) const override {
        std::vector<T> out;
        for (const auto& item : source) {
            if (std::find(out.begin(), out.end(), item) == out.end()) out.push_back(item);
        }
        return out;
    }
};

// -- Terminal reduce step -------------------------------------------------

// Fold `op` over the source starting from `initial`, collapsing to a
// (possibly different) scalar type R.
template <typename T, typename R>
struct Reduce {
    std::function<R(const R&, const T&)> op;
    R initial;
    std::string step_name = "reduce";

    R run(const std::vector<T>& source) const {
        R acc = initial;
        for (const auto& item : source) acc = op(acc, item);
        return acc;
    }
};

// -- Composed pipeline --------------------------------------------------

// Composed sequence of vector<T> -> vector<T> steps ending in a terminal
// Reduce<T, R>. Built via StepChain<T>'s `>>` operator (see below).
template <typename T, typename R>
class Pipeline {
public:
    Pipeline(std::vector<std::shared_ptr<Step<T>>> steps, Reduce<T, R> reduce)
        : steps_(std::move(steps)), reduce_(std::move(reduce)) {}

    R run(const std::vector<T>& source) const {
        std::vector<T> result = source;
        for (const auto& step : steps_) result = step->run(result);
        return reduce_.run(result);
    }

    // Render the chain as "name1 >> name2 >> name3".
    std::string describe() const {
        std::string out;
        for (const auto& step : steps_) {
            if (!out.empty()) out += " >> ";
            out += step->step_name;
        }
        if (!out.empty()) out += " >> ";
        out += reduce_.step_name;
        return out;
    }

    std::size_t size() const { return steps_.size() + 1; }

private:
    std::vector<std::shared_ptr<Step<T>>> steps_;
    Reduce<T, R> reduce_;
};

// A chain of same-type steps not yet terminated by a Reduce. `>>` is
// overloaded twice with two DIFFERENT return types -- chaining another
// Step keeps it a StepChain<T>, but chaining a Reduce<T,R> produces a
// Pipeline<T,R> -- mirroring the Python original's "operator overloading
// with non-trivial type returns" stress point.
template <typename T>
class StepChain {
public:
    StepChain() = default;

    StepChain<T> operator>>(std::shared_ptr<Step<T>> step) const {
        StepChain<T> next = *this;
        next.steps_.push_back(std::move(step));
        return next;
    }

    template <typename R>
    Pipeline<T, R> operator>>(Reduce<T, R> reduce) const {
        return Pipeline<T, R>(steps_, std::move(reduce));
    }

    std::vector<T> run(const std::vector<T>& source) const {
        std::vector<T> result = source;
        for (const auto& step : steps_) result = step->run(result);
        return result;
    }

private:
    std::vector<std::shared_ptr<Step<T>>> steps_;
};

// -- Convenience constructors ----------------------------------------------

// Build a small demo pipeline that sums the squares of positive evens.
inline Pipeline<int, int> make_demo_pipeline() {
    StepChain<int> chain;
    chain = chain >> std::static_pointer_cast<Step<int>>(
                          std::make_shared<Filter<int>>([](const int& x) { return x > 0; }, "positives"));
    chain = chain >> std::static_pointer_cast<Step<int>>(
                          std::make_shared<Filter<int>>([](const int& x) { return x % 2 == 0; }, "evens"));
    chain = chain >> std::static_pointer_cast<Step<int>>(
                          std::make_shared<Map<int>>([](const int& x) { return x * x; }, "square"));
    chain = chain >> std::static_pointer_cast<Step<int>>(std::make_shared<Take<int>>(10, "take_10"));
    Reduce<int, int> sum_reduce{[](const int& acc, const int& n) { return acc + n; }, 0, "sum"};
    return chain >> sum_reduce;
}

}  // namespace app_cli::pipeline
