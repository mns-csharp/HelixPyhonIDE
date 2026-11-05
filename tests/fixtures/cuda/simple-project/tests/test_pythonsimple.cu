// Translated from tests/test_pythonsimple.py (pytest, one placeholder test).
//
// Framework choice: Catch2 (via CMake FetchContent). Chosen over GoogleTest
// because its TEST_CASE/REQUIRE style reads closest to a bare pytest
// `assert`, and this environment has network access to github.com for
// FetchContent to pull the dependency at configure time.
//
// This file is a .cu (not .cpp) because it exercises shapes_demo.cuh's
// __host__ __device__-qualified Circle: those qualifiers only expand to
// something meaningful when the translation unit is preprocessed by nvcc,
// so, unlike app/main_gui.cpp, this test file must go through nvcc too.
#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>

#include "callhier_demo.cuh"
#include "cond_demo.cuh"
#include "extract_demo.cuh"
#include "generic_demo.cuh"
#include "impl_demo.cuh"
#include "shapes_demo.cuh"
#include "vars_demo.cuh"

#include <vector>

TEST_CASE("placeholder") {
    // Direct translation of test_pythonsimple.py::test_placeholder.
    REQUIRE(true);
}

// The smoke tests below are not present in the Python original (whose test
// suite only has the placeholder above) but give this translation's demo
// library real build/link/run verification, one per module — the same
// pattern the Rust translation uses for vars_demo and exc_demo (embedded
// #[cfg(test)] modules) rather than something added ad hoc.

TEST_CASE("callhier_demo: helper is reachable from alpha, beta, and Service::run") {
    using namespace pythonsimple::callhier_demo;
    REQUIRE(alpha() == 2);
    REQUIRE(beta() == 2);
    REQUIRE(Service{}.run() == 1);
}

TEST_CASE("cond_demo: run() is deterministic") {
    REQUIRE(pythonsimple::cond_demo::run() == 10);
}

TEST_CASE("extract_demo: total_price") {
    REQUIRE(pythonsimple::extract_demo::total_price(10.0, 3.0) == Catch::Approx(36.0));
}

TEST_CASE("generic_demo: first() on a std::vector") {
    std::vector<int> v{7, 8, 9};
    REQUIRE(pythonsimple::generic_demo::first(v) == 7);
    REQUIRE(pythonsimple::generic_demo::first_of_int_vector(v) == 7);
}

TEST_CASE("impl_demo: SqlService resolves run() through helper()") {
    pythonsimple::impl_demo::SqlService svc;
    REQUIRE(svc.run() == 1);
    REQUIRE(svc.validate() == true);
}

TEST_CASE("shapes_demo: Circle area") {
    pythonsimple::shapes_demo::Circle c(2.0);
    REQUIRE(c.area() == Catch::Approx(3.14159 * 4.0));
}

TEST_CASE("vars_demo: build() sums the numbers list") {
    REQUIRE(pythonsimple::vars_demo::build() == 60);
}

// exc_demo and imports_demo are intentionally not asserted here:
//  - exc_demo::compute triggers a hardware divide-by-zero fault (see the
//    comment in exc_demo.cu) rather than a catchable C++ exception, so it
//    is not suited to a portable in-process unit-test assertion; like the
//    Python/Rust originals it is meant to be exercised interactively under
//    a debugger with break-on-exception armed, not asserted here.
//  - imports_demo exists only to give the unused-#include refactor a real
//    include list to operate on and has no interesting return value.
