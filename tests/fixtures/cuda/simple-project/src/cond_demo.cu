#include "cond_demo.cuh"

namespace pythonsimple::cond_demo {

int run() {
    int total = 0;
    for (int i = 0; i < 5; ++i) {
        total += i; // conditional breakpoint here: only stop when i == 3
    }
    return total;
}

} // namespace pythonsimple::cond_demo
