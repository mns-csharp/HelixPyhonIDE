"""Deterministic debug demo — lives inside the app_cli project (has a pyproject.toml),
so it appears in the navigation pane and can be opened by double-clicking it. Used to
verify the GUI debugger: set a breakpoint on the loop body, watch acc/i/n, step, and
read the call stack. Self-contained (no imports) so it runs standalone under debugpy.
"""


def factorial(n):
    acc = 1
    for i in range(1, n + 1):
        acc = acc * i          # line 11: loop body — breakpoint here, hit n times
    return acc                 # line 12


def run_demo():
    total = 0
    for k in range(1, 5):
        total += factorial(k)
    print("RESULT", total)     # 1 + 2 + 6 + 24 = 33


run_demo()
