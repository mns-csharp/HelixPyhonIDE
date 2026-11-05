add_executable(app1 src/main.cu)
# NewClass.cu/.cuh exist on disk but are intentionally not added to the target,
# mirroring PythonApplication1's NewClass.py being absent from the .pyproj Compile list.
