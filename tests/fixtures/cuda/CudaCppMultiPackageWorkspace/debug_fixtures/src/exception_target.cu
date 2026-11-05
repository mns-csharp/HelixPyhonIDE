// Deterministic throwing target for IDE exception-break verification.
//
// Mirrors exception_target.py. `safe_divide` throws on the third call;
// `main` catches the first (a *thrown-but-handled* exception) and lets
// the second propagate uncaught. Break on all thrown exceptions to stop
// on both; break on uncaught-only to stop on just the propagating one.

#include <cstdio>
#include <stdexcept>

int safe_divide(int a, int b) {
    if (b == 0) {
        throw std::overflow_error("division by zero");  // line: throws when b == 0
    }
    return a / b;
}

int main() {
    try {
        safe_divide(10, 0);  // caught below -> "thrown" but not "uncaught"
    } catch (const std::overflow_error&) {
        std::printf("CAUGHT\n");
    }
    safe_divide(1, 0);  // uncaught -> propagates out of main
    std::printf("UNREACHABLE\n");
    return 0;
}
