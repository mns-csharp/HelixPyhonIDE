#include "lib_utils/iterators.cuh"

// Explicit instantiations of the common specialisations used elsewhere in
// this workspace -- proves the templates above compile cleanly for a
// real element type (mirrors the instantiation pattern used in
// models.cu).
namespace lib_utils::iterators {

template std::vector<std::vector<int>> windowed<int>(const std::vector<int>&, int);
template std::vector<std::vector<int>> chunked<int>(const std::vector<int>&, int);
template std::vector<int> unique_preserving_order<int>(const std::vector<int>&);
template std::optional<int> reduce_inplace<int>(const std::vector<int>&, std::function<int(const int&, const int&)>);

}  // namespace lib_utils::iterators
