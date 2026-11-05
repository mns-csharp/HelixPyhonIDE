#include "lib_core/storage.cuh"

namespace lib_core::storage {

static_assert(TypedStorage<InMemoryTypedStorage<std::string, int>, std::string, int>,
              "InMemoryTypedStorage<string,int> must satisfy the TypedStorage concept");

template class InMemoryTypedStorage<std::string, int>;
template class TransactionalStorage<std::string, int>;

}  // namespace lib_core::storage
