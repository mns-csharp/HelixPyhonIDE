#pragma once

#include <cstddef>
#include <vector>

// PEP 695 generic in the Python original — `first[T: Sequence](items: T)` —
// exercises a name used ONLY in a type-parameter bound: `Sequence` never
// appears in the function body, only in the bound `[T: Sequence]`, and a
// naive linter walking only the body would wrongly flag its import as
// unused.
//
// The C++20 analog is a concept referenced only in the template parameter
// list of `first`, never in the body's own name lookup: `Indexable` (like
// the Rust translation's `std::ops::Index` bound in generic_demo.rs) is used
// solely as a constraint, not as a value in the function.
namespace pythonsimple::generic_demo {

template <typename T>
concept Indexable = requires(T &t, std::size_t i) {
    { t[i] };
};

template <Indexable T>
auto first(T &items) -> decltype(items[0]) {
    return items[0];
}

// Concrete instantiation check declared here (defined in generic_demo.cu)
// so this header-only template still has a real, compiled translation unit,
// analogous to how the Python generic is exercised without a "matching"
// implementation file of its own.
int first_of_int_vector(std::vector<int> &items);

} // namespace pythonsimple::generic_demo
