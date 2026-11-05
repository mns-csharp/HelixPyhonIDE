cmake_minimum_required(VERSION 3.20)
project(PythonSimple LANGUAGES CXX CUDA)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CUDA_STANDARD 20)
set(CMAKE_CUDA_STANDARD_REQUIRED ON)

# Library with one .cuh/.cu pair per pythonsimple/_*_demo.py module.
add_subdirectory(src)

# __main__.py's tkinter hello-world -> a real native Win32 window (see
# app/main_gui.cpp for why this one file is plain C++ instead of .cu).
add_executable(pythonsimple_gui WIN32 app/main_gui.cpp)
target_link_libraries(pythonsimple_gui PRIVATE user32)

enable_testing()
add_subdirectory(tests)
