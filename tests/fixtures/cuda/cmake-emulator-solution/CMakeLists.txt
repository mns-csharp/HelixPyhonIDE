cmake_minimum_required(VERSION 3.20)
project(EmulatedParticleLab LANGUAGES CXX CUDA)
include_directories(include)
add_executable(emulated_particle_lab
    src/kernel.cu
    src/main.cpp)
