add_library(lib_utils STATIC
    src/geometry.cu
    src/logger.cu
    src/decorators.cu
    src/iterators.cu
    src/async_ops.cu
    src/complexity_demo.cu
    src/diagnostics_demo.cu
    src/lint_demo.cu
)
target_include_directories(lib_utils PUBLIC include)
target_link_libraries(lib_utils PUBLIC lib_core)
set_target_properties(lib_utils PROPERTIES CUDA_SEPARABLE_COMPILATION ON)

find_package(Threads REQUIRED)
target_link_libraries(lib_utils PUBLIC Threads::Threads)
