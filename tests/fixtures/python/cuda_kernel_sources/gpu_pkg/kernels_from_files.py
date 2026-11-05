"""Styles 2 and 3: the kernel lives in a separate file the program reads."""

import pathlib

import cupy as cp


class FileKernels:
    @staticmethod
    def saxpy():
        source = open("kernels/saxpy.cu").read()
        return cp.RawKernel(source, "saxpy")

    @staticmethod
    def reduce_sum():
        source = pathlib.Path("kernels/reduce.txt").read_text()
        return cp.RawKernel(source, "reduce_sum")
