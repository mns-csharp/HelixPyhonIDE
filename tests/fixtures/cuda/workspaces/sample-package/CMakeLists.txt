cmake_minimum_required(VERSION 3.20)
project(Sample LANGUAGES CXX CUDA)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CUDA_STANDARD 20)

add_library(sample STATIC src/animals.cu src/shapes.cu)
target_include_directories(sample PUBLIC include)
