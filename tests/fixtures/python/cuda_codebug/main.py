"""One program with two debuggers in it.

Nothing here knows about Helix. It is ordinary CuPy code that loads a kernel from a ``.cu``
file, and it is the fixture for the claim Stage 3 makes: a breakpoint in THIS file and a
breakpoint in ``saxpy.cu`` are both live in a single debug session, and control passes from
one to the other at the launch with nothing for the user to arrange.
"""

import array
import os

import cupy


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "saxpy.cu"), "r") as handle:
        source = handle.read()

    host = array.array("i", [1, 2, 3, 4, 5, 6, 7, 8])
    factor = 3
    print("before =", list(host))          # PYTHON BREAKPOINT 1 (before the bridge even exists)

    kernel = cupy.RawKernel(source, "scale")
    device = cupy.mem_alloc(host.itemsize * len(host))
    cupy.memcpy_htod(device, host)
    kernel((1,), (len(host),), (device, factor, len(host)))
    cupy.memcpy_dtoh(host, device)

    print("scaled =", list(host))          # PYTHON BREAKPOINT 2 (back in Python, kernel done)


main()
