add_library(pythonsimple STATIC
    callhier_demo.cu
    cond_demo.cu
    exc_demo.cu
    extract_demo.cu
    generic_demo.cu
    impl_demo.cu
    imports_demo.cu
    shapes_demo.cu
    vars_demo.cu
)
target_include_directories(pythonsimple PUBLIC include)
