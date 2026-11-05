#pragma once

// Deterministic debug demo -- lives inside the app_cli target so it's
// reachable from the same binary the debugger attaches to. Used to
// verify the GUI debugger: set a breakpoint on the loop body, watch
// acc/i/n, step, and read the call stack.
//
// Mirrors app_cli/_debug_demo.py.

namespace app_cli::debug_demo {

inline long long factorial(long long n) {
    long long acc = 1;
    for (long long i = 1; i <= n; ++i) {
        acc = acc * i;  // loop body -- breakpoint here, hit n times per call
    }
    return acc;  // hit once per call
}

inline long long run_demo() {
    long long total = 0;
    for (long long k = 1; k < 5; ++k) {
        total += factorial(k);
    }
    return total;  // 1 + 2 + 6 + 24 = 33
}

}  // namespace app_cli::debug_demo
