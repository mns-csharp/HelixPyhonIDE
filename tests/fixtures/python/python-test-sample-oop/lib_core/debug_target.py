"""Deterministic debug target for `helix_cli debug` end-to-end verification.

Run under the debugger with a breakpoint on the marked line; the engine must
stop there, expose locals (n, acc), then continue to a clean exit.
"""


def factorial(n):
    acc = 1
    for i in range(1, n + 1):
        acc = acc * i          # line 11: loop body, hit n times per call
    return acc                 # line 12: hit once per call


