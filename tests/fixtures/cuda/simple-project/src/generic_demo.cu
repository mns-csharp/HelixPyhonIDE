#include "generic_demo.cuh"

namespace pythonsimple::generic_demo {

static_assert(Indexable<std::vector<int>>,
              "std::vector<int> must satisfy Indexable so `first` can be instantiated for it");

int first_of_int_vector(std::vector<int> &items) {
    return first(items);
}

} // namespace pythonsimple::generic_demo
