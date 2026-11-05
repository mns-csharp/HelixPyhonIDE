"""Redirect CUDA to the Helix emulator in a program that knows nothing about Helix.

CPython imports ``sitecustomize`` automatically at start-up, before any user code runs, if
one is importable. Putting this directory on ``PYTHONPATH`` is therefore enough to make an
UNMODIFIED program's ``import cupy`` resolve to the emulator shim -- no edit to the program,
no wrapper script, no command-line flag it has to understand.

Two properties make this safe to leave switched on:

* It is opt-in. Nothing happens unless ``HELIX_CUDA_EMULATE`` is set, so this directory
  sitting on the path of an unrelated interpreter changes nothing.
* It never takes precedence over a working GPU by accident. The IDE sets the variable when
  the user chose the emulator; with it unset, a real CuPy on the machine is imported exactly
  as before.

If the shim cannot be loaded, the failure is printed and start-up CONTINUES. A broken
sitecustomize that raised would stop every Python program in the environment, including ones
that have nothing to do with CUDA -- a far worse failure than the one it was reporting.
"""

import os
import sys


def _enable():
    if os.environ.get("HELIX_CUDA_EMULATE", "") not in ("1", "true", "True"):
        return

    try:
        import helix_cuda
    except ImportError as error:
        sys.stderr.write(
            "helix: CUDA emulation was requested but helix_cuda could not be imported (%s).\n"
            "       The program will run against whatever CUDA it finds instead.\n" % error)
        return

    try:
        helix_cuda.install()
    except Exception as error:                      # pragma: no cover - defensive
        sys.stderr.write("helix: could not install the CUDA emulator shim (%s).\n" % error)
        return

    if os.environ.get("HELIX_CUDA_ANNOUNCE", "1") == "1":
        sys.stderr.write("helix: CUDA calls are being emulated on the CPU (no GPU in use).\n")


_enable()
