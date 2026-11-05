cmake_minimum_required(VERSION 3.20)
project(CudaCppMultiPackageWorkspace LANGUAGES CXX CUDA)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CUDA_STANDARD 20)
set(CMAKE_CUDA_STANDARD_REQUIRED ON)

# Mirrors the Python uv workspace's 5 members (lib_core, lib_utils,
# app_cli, app_server, my_unit_test_package) plus the standalone
# debug_fixtures target for the two root-level loose files
# (debug_target.py / exception_target.py) that aren't part of any
# sub-package.
add_subdirectory(lib_core)
add_subdirectory(lib_utils)
add_subdirectory(app_cli)
add_subdirectory(app_server)
add_subdirectory(debug_fixtures)

enable_testing()
add_subdirectory(my_unit_test_package)
