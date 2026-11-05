"""Deterministic target for the exception-break debugger test.

`compute(5)` divides by `(i - 2)`, so at i == 2 it raises an uncaught
ZeroDivisionError. With "Break on Uncaught Exceptions" armed the debugger stops
at that line, and debugpy's exceptionInfo reports the type + message.
"""


def compute(n):
    total = 0
    for i in range(n):
        total += 10 // (i - 2)  # i == 2 -> ZeroDivisionError here
    return total


if __name__ == "__main__":
    print("RESULT", compute(5))
