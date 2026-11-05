// Deterministic debug target for IDE `debug` end-to-end verification.
//
// Mirrors debug_target.py. Run under the debugger with a breakpoint on
// the marked line; the engine must stop there, expose locals (n, acc),
// then continue to a clean exit.

#include <cstdio>

long long factorial(long long n) {
    long long acc = 1;
    for (long long i = 1; i <= n; ++i) {
        acc = acc * i;  // line: loop body, hit n times per call
    }
    return acc;  // line: hit once per call
}

int main() {
    long long total = 0;
    for (long long k = 1; k < 5; ++k) {
        total += factorial(k);
    }
    std::printf("RESULT %lld\n", total);  // 1 + 2 + 6 + 24 = 33
    return 0;
}
