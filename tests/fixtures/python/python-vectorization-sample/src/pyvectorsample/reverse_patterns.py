"""Reverse vectorization patterns (R1, R2, R5, R10, R14): NumPy/idiomatic -> explicit loop.

Mirror image of forward_patterns.py: each function holds a realistic already-vectorized
block. Select it, right-click, choose "Un-vectorize" — HelixStudio should replace it with
the equivalent explicit `for` loop noted in the docstring. Block markers (`# BEGIN <ID>` /
`# END <ID>`) are read verbatim by VectorizationFixtureTests — keep them intact.
"""
from __future__ import annotations

import math

import numpy as np


def add_elementwise(a: list[float], b: list[float]):
    """R1 - devectorize element-wise binop -> explicit indexed loop."""
    # BEGIN R1
    c = np.asarray(a) + np.asarray(b)
    # END R1
    return c


def total_of(values: list[float]):
    """R2 - devectorize sum() -> accumulator loop."""
    # BEGIN R2
    total = sum(values)
    # END R2
    return total


def doubled_positive(xs: list[float]):
    """R5 - devectorize comprehension -> append loop."""
    def double(x: float) -> float:
        return x * 2

    # BEGIN R5
    result = [double(x) for x in xs]
    # END R5
    return result


def highest_reading(readings: list[float]):
    """R10 - devectorize max() -> scan loop."""
    # BEGIN R10
    best = max(readings)
    # END R10
    return best


def pick_by_condition(cond: list[bool], a: list[float], b: list[float]):
    """R14 - devectorize np.where() -> indexed conditional loop."""
    # BEGIN R14
    c = np.where(cond, a, b)
    # END R14
    return c
