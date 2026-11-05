"""Deliberate near-misses: code that LOOKS like a forward/reverse vectorization pattern
but differs in one semantically important way. For most functions below, HelixStudio's
matcher must refuse to offer Vectorize/Un-vectorize on the marked block at all (a hard
skeleton mismatch: a different loop-header shape, an extra statement, nested control
flow). A couple of functions (`shifted_difference`, `accumulator_with_wrong_operator`)
are instead within the engine's Tier-2 relaxation tolerance: it still offers a rewrite,
but only at reduced confidence with an explicit caveat naming exactly what's suspicious
— see each docstring for which behavior applies. Either way, offering a *clean,
uncaveated* rewrite here would silently change program behavior, and that never happens.

Each docstring states which real pattern this resembles and exactly why. VectorizationFixtureTests
asserts `VectorizationRefactor.Match`'s actual behavior against each claim for every
`# BEGIN` / `# END` block.
"""
from __future__ import annotations


def prefix_sum_self_referential(a: list[float]) -> list[float]:
    """Looks like F1 (elementwise binop) but reads the array being written, one slot back
    (a true sequential dependency / prefix sum). Rewriting this as `a + shift(a)` would
    silently read POST-update values instead of pre-update ones. Must NOT match F1."""
    # BEGIN NEARMISS_PREFIX_SUM
    for i in range(1, len(a)):
        a[i] = a[i] + a[i - 1]
    # END NEARMISS_PREFIX_SUM
    return a


def shifted_difference(a: list[float], b: list[float]) -> list[float]:
    """Looks like F1 but the right operand is offset by one from the loop index (a
    windowed/lag difference, not a true elementwise zip). The engine's Tier-2 relaxation
    engine DOES still offer this as a rewrite (an index-mismatch caveat, not a hard
    skeleton mismatch, since `range(len(a))` is still the plain shape it expects) — but
    only at reduced confidence with an explicit caveat explaining the mismatched index,
    never as a clean uncaveated `c = np.asarray(a) + np.asarray(b)`, which would silently
    read the wrong slice of `b`. Caller must keep `len(b) == len(a) + 1`."""
    c = [0.0] * len(a)
    # BEGIN NEARMISS_SHIFTED_INDEX
    for i in range(len(a)):
        c[i] = a[i] + b[i + 1]
    # END NEARMISS_SHIFTED_INDEX
    return c


def strided_downsample(a: list[float]) -> list[float]:
    """Looks like F3 (scalar broadcast over every element) but only visits every other
    index (a stride-2 downsample). Vectorizing as `a * 2` would touch elements the
    original loop never read or wrote. Must NOT match F3."""
    c = list(a)
    # BEGIN NEARMISS_STRIDE
    for i in range(0, len(a), 2):
        c[i] = a[i] * 2
    # END NEARMISS_STRIDE
    return c


def accumulator_with_wrong_operator(values: list[float]) -> float:
    """Looks like F8 (sum accumulator) but actually SUBTRACTS each element, so
    `sum(values)` would produce the wrong number entirely, not just a differently
    -shaped equivalent. Must NOT match F8."""
    # BEGIN NEARMISS_WRONG_ACCUM_OP
    total = 0
    for x in values:
        total -= x
    # END NEARMISS_WRONG_ACCUM_OP
    return total


def early_exit_scan(readings: list[float]) -> float:
    """Looks like F10 (max scan) but bails out on the first negative reading instead of
    scanning to the end, so the result depends on ORDER and early termination, not just
    "the largest value present". `max(readings)` is not equivalent. Must NOT match F10."""
    best = readings[0]
    # BEGIN NEARMISS_EARLY_EXIT_SCAN
    for x in readings[1:]:
        if x < 0:
            break
        if x > best:
            best = x
    # END NEARMISS_EARLY_EXIT_SCAN
    return best


def filter_append_with_side_effect(xs: list[float]) -> list[float]:
    """Looks like F6 (filter-append comprehension) but the loop body also logs every
    kept element as a side effect; collapsing this into `[x for x in xs if ...]` would
    silently drop the logging. Must NOT match F6."""
    log: list[float] = []
    result: list[float] = []
    # BEGIN NEARMISS_FILTER_SIDE_EFFECT
    for x in xs:
        if x > 0:
            result.append(x)
            log.append(x)
    # END NEARMISS_FILTER_SIDE_EFFECT
    return result


def map_append_indexes_a_different_list(xs: list[int], weights: list[int]) -> list[int]:
    """Looks like F5 (map-append) but the appended expression indexes a SECOND list by
    position instead of transforming the loop variable itself — a disguised zip/gather,
    not a pure per-element map of `xs`. Must NOT match F5."""
    result: list[int] = []
    # BEGIN NEARMISS_GATHER_NOT_MAP
    for i in range(len(xs)):
        result.append(xs[i] * weights[i])
    # END NEARMISS_GATHER_NOT_MAP
    return result


def conditional_fill_with_function_calls(cond: list[bool], a: list[float]) -> list[float]:
    """Looks like F14 (np.where selection) but the "true" branch calls a function with
    side effects (a counter) rather than reading a plain array slot, so collapsing to
    `np.where(cond, f(a), a)` would change how many times the side effect fires (NumPy
    evaluates both branches eagerly for every element, `if/else` only evaluates one).
    Must NOT match F14 (or must surface a caveat if it matches at all)."""
    counter = {"calls": 0}

    def touch(v: float) -> float:
        counter["calls"] += 1
        return v * 2

    c = [0.0] * len(a)
    # BEGIN NEARMISS_EAGER_SIDE_EFFECT
    for i in range(len(a)):
        c[i] = touch(a[i]) if cond[i] else a[i]
    # END NEARMISS_EAGER_SIDE_EFFECT
    return c


def product_with_non_multiplicative_reset(values: list[int]) -> int:
    """Looks like F9 (running product) but resets to 1 mid-loop whenever a zero is seen,
    which `math.prod(values)` cannot express (it would just multiply in the zero and stay
    zero forever after). Must NOT match F9."""
    prod = 1
    # BEGIN NEARMISS_PRODUCT_RESET
    for x in values:
        if x == 0:
            prod = 1
        else:
            prod *= x
    # END NEARMISS_PRODUCT_RESET
    return prod
