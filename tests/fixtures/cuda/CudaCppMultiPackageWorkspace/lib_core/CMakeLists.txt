add_library(lib_core STATIC
    src/shapes.cu
    src/animals.cu
    src/errors.cu
    src/events.cu
    src/models.cu
    src/storage.cu
    src/new_class.cu
    src/diagnostics_demo.cu
)
target_include_directories(lib_core PUBLIC include)
set_target_properties(lib_core PROPERTIES CUDA_SEPARABLE_COMPILATION ON)
