"""Deterministic throwing target for `helix_cli debug --exceptions=...` verification.

`safe_divide` raises ZeroDivisionError on the third call; `main` catches the first
(so it is a *raised-but-handled* exception) and lets the second propagate uncaught.
Run with --exceptions=raised to stop on both; --exceptions=uncaught for only the
propagating one.
"""


def safe_divide(a, b):
    return a / b                    # line 11: raises ZeroDivisionError when b == 0



