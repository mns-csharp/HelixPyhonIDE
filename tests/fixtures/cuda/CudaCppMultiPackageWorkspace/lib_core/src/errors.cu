#include "lib_core/errors.cuh"

#include <type_traits>

// errors.cuh is header-only (every member is a small inline constructor);
// this translation unit exists so the library has a concrete .cu source
// mirroring errors.py, and to give the linker a place to instantiate any
// out-of-line pieces should the header grow them later.

namespace lib_core::errors {

static_assert(std::is_base_of_v<HelixError, StorageError>);
static_assert(std::is_base_of_v<StorageError, KeyMissingError>);
static_assert(std::is_base_of_v<StorageError, TypeMismatchError>);
static_assert(std::is_base_of_v<StorageError, IntegrityError>);
static_assert(std::is_base_of_v<StorageError, CapacityError>);
static_assert(std::is_base_of_v<HelixError, HandlerError>);
static_assert(std::is_base_of_v<HelixError, ValidationError>);
static_assert(std::is_base_of_v<ValidationError, FieldValidationError>);
static_assert(std::is_base_of_v<ValidationError, CrossFieldValidationError>);
static_assert(std::is_base_of_v<HelixError, ConfigurationError>);
static_assert(std::is_base_of_v<HelixError, RetryableError>);

}  // namespace lib_core::errors
