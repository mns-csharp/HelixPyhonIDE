include(FetchContent)
FetchContent_Declare(
    Catch2
    GIT_REPOSITORY https://github.com/catchorg/Catch2.git
    GIT_TAG v3.7.1
)
FetchContent_MakeAvailable(Catch2)

add_executable(pythonsimple_tests test_pythonsimple.cu)
target_link_libraries(pythonsimple_tests PRIVATE pythonsimple Catch2::Catch2WithMain)

add_test(NAME pythonsimple_tests COMMAND pythonsimple_tests)
