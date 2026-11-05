#include "exc_demo.cuh"

namespace pythonsimple::exc_demo {

int compute(int n) {
    int total = 0;
    for (int i = 0; i < n; ++i) {
        // i == 2 -> integer division by zero here.
        //
        // NOTE (native-code caveat): unlike Python's clean, catchable
        // ZeroDivisionError, dividing an int by zero is undefined behavior
        // per the C++ standard. In practice on real hardware/OSes it is not
        // silently UB but a hard fault: x86 raises a #DE trap that Windows
        // surfaces as the structured exception EXCEPTION_INT_DIVIDE_BY_ZERO
        // and POSIX surfaces as SIGFPE. That hardware/OS-level fault is the
        // closest available native analog to an "uncaught exception" for a
        // debugger's break-on-exception feature, so this is the intended
        // translation of the Python demo's intent even though the underlying
        // mechanism (signal/SEH vs. a language-level exception) differs.
        total += 10 / (i - 2);
    }
    return total;
}

} // namespace pythonsimple::exc_demo
