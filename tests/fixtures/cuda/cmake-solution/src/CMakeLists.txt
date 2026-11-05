add_library(particle_cuda STATIC particle.cu)
add_executable(particle_lab main.cpp)
target_link_libraries(particle_lab PRIVATE particle_cuda)
