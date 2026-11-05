add_executable(app_cli
    src/commands.cu
    src/pipeline.cu
    src/debug_demo.cu
    src/diagnostics_demo.cu
    src/main.cu
)
target_include_directories(app_cli PRIVATE include)
target_link_libraries(app_cli PRIVATE lib_core lib_utils)
set_target_properties(app_cli PROPERTIES CUDA_SEPARABLE_COMPILATION ON)
