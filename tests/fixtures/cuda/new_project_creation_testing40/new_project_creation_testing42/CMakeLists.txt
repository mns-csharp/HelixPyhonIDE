cmake_minimum_required(VERSION 3.18)
project(new_project_creation_testing42 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
include(FetchContent)
FetchContent_Declare(googletest
  URL https://github.com/google/googletest/archive/refs/tags/v1.14.0.zip
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)
enable_testing()
add_executable(new_project_creation_testing42 test_main.cpp)
target_link_libraries(new_project_creation_testing42 GTest::gtest_main)
include(GoogleTest)
gtest_discover_tests(new_project_creation_testing42)
