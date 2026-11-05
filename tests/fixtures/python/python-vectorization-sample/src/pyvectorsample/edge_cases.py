"""Boundary conditions for the vectorization matcher: in-place updates, single-element
inputs, attribute-based array access, and alternate iteration styles that a human would
consider "basically the same pattern" but that the strict structural matcher may or may
not recognize. Each docstring states the expected behavior; VectorizationFixtureTests
pins down which side of the line the real engine actually falls on.
"""
from __future__ import annotations


def double_in_place(a: list[float]) -> list[float]:
    """Edge case: F3 (scalar broadcast) where the write target IS the source array
    (in-place update), not a separate output array. Structurally this is still a single
    assignment of `a[i] = a[i] * 2`, so it should still match F3, producing `a = np.asarray(a) * 2`
    (rebinding the name rather than truly mutating in place — a caveat worth surfacing)."""
    # BEGIN EDGE_INPLACE_SCALE
    for i in range(len(a)):
        a[i] = a[i] * 2
    # END EDGE_INPLACE_SCALE
    return a


def sum_of_single_element(values: list[float]) -> float:
    """Edge case: F8 (sum accumulator) over an iterable that happens to hold exactly one
    element at runtime. The pattern shape is identical to the general case — the matcher
    works on AST structure, not runtime list length — so this must match exactly like
    total_of() in forward_patterns.py."""
    # BEGIN EDGE_SUM_SINGLE
    total = 0
    for x in values:
        total += x
    # END EDGE_SUM_SINGLE
    return total


def scale_attribute_array(container) -> None:
    """Edge case: the array being looped over is reached through an attribute
    (`container.values`), not a bare local Name. `range(len(container.values))` does not
    reduce to the simple RangeOverLen(Name) shape the matcher expects, so this should
    NOT match F3, even though it is semantically identical to double_in_place() above."""
    # BEGIN EDGE_ATTRIBUTE_ARRAY
    for i in range(len(container.values)):
        container.values[i] = container.values[i] * 2
    # END EDGE_ATTRIBUTE_ARRAY


def sum_via_enumerate(values: list[float]) -> float:
    """Edge case: the same accumulation as F8, but the loop header uses
    `enumerate(values)` and discards the index, instead of a bare `for x in values`.
    Since F8's matcher keys off ForOverIter's plain-name loop variable, this alternate
    iteration style should NOT match F8 as written (a human reading it recognizes the
    same sum immediately; the structural matcher does not)."""
    total = 0
    # BEGIN EDGE_ENUMERATE_SUM
    for _, x in enumerate(values):
        total += x
    # END EDGE_ENUMERATE_SUM
    return total


def sum_nested_matrix(matrix: list[list[float]]) -> float:
    """Edge case: a nested (2D) accumulation loop. Forward pattern F8 matches a single
    `for` over a flat iterable with one accumulate statement in its body — a for-loop
    nested inside another for-loop is a different (2D) shape entirely and must NOT match
    F8 on either the inner or the outer loop in isolation."""
    total = 0
    # BEGIN EDGE_NESTED_SUM
    for row in matrix:
        for x in row:
            total += x
    # END EDGE_NESTED_SUM
    return total
