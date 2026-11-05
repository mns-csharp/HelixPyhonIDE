#include "impl_demo.cuh"

namespace pythonsimple::impl_demo {

int BaseService::helper() const { return 1; }

int SqlService::run() const { return helper(); }

bool SqlService::validate() const { return true; }

} // namespace pythonsimple::impl_demo
