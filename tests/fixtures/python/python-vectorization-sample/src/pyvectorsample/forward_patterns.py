"""Forward vectorization patterns (F1-F16): loop -> NumPy / comprehension.

Every function below is a realistic (renamed, commented, slightly more context than the
one-liner canonical form) instance of ONE of the 16 forward patterns HelixStudio's
VectorizationRefactor recognizes. Select the marked loop, right-click, choose "Vectorize" —
each should produce the NumPy/idiomatic replacement noted in its docstring, with
`import numpy as np` inserted automatically the first time it's needed.

Each function is independently selectable and independently correct (they don't depend on
each other), so this file doubles as a manual click-through checklist and a source the
C# fixture tests (VectorizationFixtureTests) extract exact blocks from via the
`# BEGIN <ID>` / `# END <ID>` markers — keep those markers exactly as they are if you edit
the loop bodies, since the tests locate blocks by that literal text.
"""
from __future__ import annotations

import math


def add_elementwise(a: list[float], b: list[float]) -> list[float]:
    """F1 - element-wise binop. Vectorize -> c = np.asarray(a) + np.asarray(b)."""
    c = [0.0] * len(a)
    # BEGIN F1
    for i in range(len(a)):
        c[i] = a[i] + b[i]
    # END F1
    return c


def negate_all(a: list[float]) -> list[float]:
    """F2 - element-wise negate. Vectorize -> c = -np.asarray(a)."""
    c = [0.0] * len(a)
    # BEGIN F2
    for i in range(len(a)):
        c[i] = -a[i]
    # END F2
    return c


def scale_by_two(a: list[float]) -> list[float]:
    """F3 - scalar broadcast. Vectorize -> c = np.asarray(a) * 2."""
    c = [0.0] * len(a)
    # BEGIN F3
    for i in range(len(a)):
        c[i] = a[i] * 2
    # END F3
    return c


def sine_of_each(angles: list[float]) -> list[float]:
    """F4 - math.fn -> numpy. Vectorize -> c = np.sin(np.asarray(angles))."""
    c = [0.0] * len(angles)
    # BEGIN F4
    for i in range(len(angles)):
        c[i] = math.sin(angles[i])
    # END F4
    return c


def squares_of_positive(xs: list[int]) -> list[int]:
    """F5 - map-append -> comprehension. Vectorize -> [square(x) for x in xs]."""
    def square(x: int) -> int:
        return x * x

    # BEGIN F5
    result = []
    for x in xs:
        result.append(square(x))
    # END F5
    return result


def keep_positive(xs: list[float]) -> list[float]:
    """F6 - filter-append -> comprehension. Vectorize -> [x for x in xs if x > 0]."""
    # BEGIN F6
    result = []
    for x in xs:
        if x > 0:
            result.append(x)
    # END F6
    return result


def doubled_positive_readings(readings: list[float]) -> list[float]:
    """F7 - filter+map-append -> comprehension."""
    # BEGIN F7
    result = []
    for x in readings:
        if x > 0:
            result.append(x * 2)
    # END F7
    return result


def total_of(values: list[float]) -> float:
    """F8 - additive accumulator -> sum(). Vectorize -> total = sum(values)."""
    # BEGIN F8
    total = 0
    for x in values:
        total += x
    # END F8
    return total


def factorial_like_product(values: list[int]) -> int:
    """F9 - multiplicative accumulator -> math.prod()."""
    # BEGIN F9
    prod = 1
    for x in values:
        prod *= x
    # END F9
    return prod


def highest_reading(readings: list[float]) -> float:
    """F10 - min/max scan -> max(). Vectorize -> best = max(readings)."""
    # BEGIN F10
    best = readings[0]
    for x in readings[1:]:
        if x > best:
            best = x
    # END F10
    return best


def count_above_threshold(values: list[float]) -> int:
    """F11 - conditional counter -> sum(1 for ... )."""
    # BEGIN F11
    n = 0
    for x in values:
        if x > 0:
            n += 1
    # END F11
    return n


def any_reading_positive(readings: list[float]) -> bool:
    """F12 - early-true scan -> any()."""
    # BEGIN F12
    for x in readings:
        if x > 0:
            return True
    return False
    # END F12


def all_readings_positive(readings: list[float]) -> bool:
    """F13 - early-false scan -> all()."""
    # BEGIN F13
    for x in readings:
        if not (x > 0):
            return False
    return True
    # END F13


def pick_by_condition(cond: list[bool], a: list[float], b: list[float]) -> list[float]:
    """F14 - conditional selection -> np.where(cond, a, b)."""
    c = [0.0] * len(a)
    # BEGIN F14
    for i in range(len(a)):
        c[i] = a[i] if cond[i] else b[i]
    # END F14
    return c


def running_total(xs: list[float]) -> list[float]:
    """F15 - running sum -> np.cumsum(). Vectorize -> list(np.cumsum(xs))."""
    # BEGIN F15
    running = 0
    out = []
    for x in xs:
        running += x
        out.append(running)
    # END F15
    return out


def doubled_indices(n: int) -> list[int]:
    """F16 - range index fill -> comprehension. Vectorize -> [i * 2 for i in range(n)]."""
    # BEGIN F16
    out = [None] * n
    for i in range(n):
        out[i] = i * 2
    # END F16
    return out
