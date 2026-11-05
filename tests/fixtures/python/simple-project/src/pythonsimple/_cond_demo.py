"""Deterministic target for exercising conditional breakpoints.

The loop body runs 5 times (i = 0..4); a breakpoint there with the condition
`i == 3` must fire exactly once, with i == 3.
"""


def run():
    total = 0
    for i in range(5):
        total += i  # conditional breakpoint here: only stop when i == 3
    return total


if __name__ == "__main__":
    print("DONE", run())
