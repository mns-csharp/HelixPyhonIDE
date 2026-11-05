#pragma once

// Deterministic target for the exception-break debugger test.
//
// `compute(5)` divides by `(i - 2)`, so at i == 2 it divides an int by zero.
// With "Break on Uncaught Exceptions" armed the debugger stops at that line.
namespace pythonsimple::exc_demo {

int compute(int n);

} // namespace pythonsimple::exc_demo
