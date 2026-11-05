#pragma once

// Deterministic target for exercising conditional breakpoints.
//
// The loop body runs 5 times (i = 0..4); a breakpoint there with the
// condition `i == 3` must fire exactly once, with i == 3.
namespace pythonsimple::cond_demo {

int run();

} // namespace pythonsimple::cond_demo
