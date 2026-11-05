cmake_minimum_required(VERSION 3.20)
project(VisualStudioSolution LANGUAGES CXX CUDA)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CUDA_STANDARD 20)

add_subdirectory(app1)
add_subdirectory(app2)
