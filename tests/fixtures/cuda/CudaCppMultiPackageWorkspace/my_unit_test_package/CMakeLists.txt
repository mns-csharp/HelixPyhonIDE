include(FetchContent)

# Catch2 v3, fetched from GitHub -- same version already used by this
# repo's cpp/simple-project fixture, so a build that already fetched it
# there reuses the cache instead of downloading twice.
FetchContent_Declare(
    Catch2
    GIT_REPOSITORY https://github.com/catchorg/Catch2.git
    GIT_TAG v3.7.1
)
FetchContent_MakeAvailable(Catch2)

add_executable(my_unit_test_package_tests
    tests/test_sample.cu
    tests/test_advanced_geometry.cu
    tests/test_async_ops.cu
    tests/test_events.cu
)
target_link_libraries(my_unit_test_package_tests PRIVATE lib_core lib_utils Catch2::Catch2WithMain)

list(APPEND CMAKE_MODULE_PATH "${catch2_SOURCE_DIR}/extras")
include(CTest)
include(Catch)
catch_discover_tests(my_unit_test_package_tests)
