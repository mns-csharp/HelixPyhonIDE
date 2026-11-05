#include "lib_utils/logger.cuh"

namespace lib_utils::logger {

static_assert(Formatter<TextFormatter>, "TextFormatter must satisfy the Formatter concept");
static_assert(Formatter<JSONFormatter>, "JSONFormatter must satisfy the Formatter concept");

}  // namespace lib_utils::logger
