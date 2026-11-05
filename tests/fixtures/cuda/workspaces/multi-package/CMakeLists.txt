cmake_minimum_required(VERSION 3.20)
project(MultiPackage LANGUAGES CXX CUDA)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CUDA_STANDARD 20)

add_subdirectory(pkg_alpha)
add_subdirectory(pkg_beta)
