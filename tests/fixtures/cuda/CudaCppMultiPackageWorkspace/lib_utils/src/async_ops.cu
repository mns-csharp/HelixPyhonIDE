#include "lib_utils/async_ops.cuh"

namespace lib_utils::async_ops {

namespace {
int identity(int x) { return x; }
}  // namespace

// Instantiate the templates against real argument types -- proves they
// compile cleanly end to end.
void exercise_async_ops_compile() {
    std::vector<std::function<int()>> tasks;
    for (int i = 0; i < 3; ++i) tasks.push_back([i] { return i; });
    (void)gather_with_limit<int>(tasks, 2);
    (void)gather_settled<int>(tasks);
    (void)with_timeout<int>([] { return 1; }, 1.0);
    (void)timeout_scope(1.0, [] { return 1; });
    (void)retry_async([] { return identity(1); });
}

}  // namespace lib_utils::async_ops
