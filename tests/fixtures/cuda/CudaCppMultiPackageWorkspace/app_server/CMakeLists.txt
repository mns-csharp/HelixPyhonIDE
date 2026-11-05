add_executable(app_server
    src/routes.cu
    src/middleware.cu
    src/main.cu
)
target_include_directories(app_server PRIVATE include)
target_link_libraries(app_server PRIVATE lib_core lib_utils)
set_target_properties(app_server PROPERTIES CUDA_SEPARABLE_COMPILATION ON)
