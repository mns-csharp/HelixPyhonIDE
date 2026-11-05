"""Deterministic throwing target for `helix_cli debug --exceptions=...` verification.

`safe_divide` raises ZeroDivisionError on the third call; `main` catches the first
(so it is a *raised-but-handled* exception) and lets the second propagate uncaught.
Run with --exceptions=raised to stop on both; --exceptions=uncaught for only the
propagating one.
"""


def safe_divide(a, b):
    return a / b                    # line 11: raises ZeroDivisionError when b == 0


def main():
    try:
        safe_divide(10, 0)          # caught below -> "raised" but not "uncaught"
    except ZeroDivisionError:
        print("CAUGHT")
    safe_divide(1, 0)               # uncaught -> propagates out of main
    print("UNREACHABLE")


if __name__ == "__main__":
    main()
