#include "lib_utils/decorators.cuh"

namespace lib_utils::decorators {

namespace {
int add_one(int x) { return x + 1; }
}  // namespace

// Instantiate each decorator against a real function -- proves the
// templates above compile cleanly (mirrors the `static_assert`
// instantiation pattern used elsewhere in this workspace).
void exercise_decorators_compile() {
    auto retried = retry(add_one, 3);
    auto timed_fn = timed(add_one, "add_one");
    auto memoized = memoize(std::function<int(int)>(add_one));
    auto traced_fn = traced(add_one, "add_one");
    (void)retried(1);
    (void)timed_fn(1);
    (void)memoized(1);
    (void)traced_fn(1);
}

}  // namespace lib_utils::decorators
