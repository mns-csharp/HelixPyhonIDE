#include "callhier_demo.cuh"

namespace pythonsimple::callhier_demo {

int helper() { return 1; }

int alpha() { return helper() + 1; }

int beta() { return helper() * 2; }

int Service::run() const { return helper(); }

} // namespace pythonsimple::callhier_demo
