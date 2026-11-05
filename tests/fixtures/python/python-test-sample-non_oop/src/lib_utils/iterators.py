"""Iterator / generator helpers — including walrus, match/case, async iter.

Each helper is intentionally self-contained so the IDE has many small
named scopes to render in the symbol picker.
"""
from __future__ import annotations

from typing import (
    AsyncIterable,
    AsyncIterator,
    Awaitable,
    Callable,
    Iterable,
    Iterator,
    TypeVar,
)


T = TypeVar("T")
U = TypeVar("U")


# -- Sliding windows -------------------------------------------------------


def windowed(seq: Iterable[T], n: int) -> Iterator[tuple[T, ...]]:
    """Yield successive n-length windows from ``seq``.

    Example::

        >>> list(windowed([1, 2, 3, 4], 2))
        [(1, 2), (2, 3), (3, 4)]
    """
    if n <= 0:
        raise ValueError(f"window size must be positive, got {n}")
    buf: list[T] = []
    for item in seq:
        buf.append(item)
        if len(buf) == n:
            yield tuple(buf)
            buf.pop(0)


def chunked(seq: Iterable[T], size: int) -> Iterator[list[T]]:
    """Yield non-overlapping chunks of ``size``. The last may be shorter."""
    if size <= 0:
        raise ValueError(f"chunk size must be positive, got {size}")
    buf: list[T] = []
    for item in seq:
        buf.append(item)
        if len(buf) == size:
            yield buf
            buf = []
    if buf:
        yield buf


# -- Walrus-driven helpers --------------------------------------------------


def take_while(seq: Iterable[T], pred: Callable[[T], bool]) -> Iterator[T]:
    """Yield items while ``pred`` returns truthy. Walrus keeps the loop tight."""
    it = iter(seq)
    while (item := next(it, _SENTINEL)) is not _SENTINEL:
        if not pred(item):  # type: ignore[arg-type]
            return
        yield item  # type: ignore[misc]


def drop_while(seq: Iterable[T], pred: Callable[[T], bool]) -> Iterator[T]:
    """Skip items while ``pred`` is truthy, then yield the rest."""
    it = iter(seq)
    while (item := next(it, _SENTINEL)) is not _SENTINEL:
        if not pred(item):  # type: ignore[arg-type]
            yield item  # type: ignore[misc]
            break
    yield from it


_SENTINEL = object()


# -- yield from chaining ---------------------------------------------------


def chain_iters(*iters: Iterable[T]) -> Iterator[T]:
    """Concatenate any number of iterables. Stops at the first exhausted."""
    for it in iters:
        yield from it


def flatten(nested: Iterable[Iterable[T]]) -> Iterator[T]:
    """Flatten one level of nesting."""
    for inner in nested:
        yield from inner


def deep_flatten(nested: Iterable) -> Iterator:
    """Flatten arbitrary depth of nested iterables, but NOT strings/bytes."""
    for item in nested:
        if isinstance(item, (str, bytes)):
            yield item
            continue
        if isinstance(item, Iterable):
            yield from deep_flatten(item)
        else:
            yield item


# -- Comprehensions / dispatch via match -----------------------------------


def classify_numbers(items: Iterable[float]) -> dict[str, list[float]]:
    """Bucket numbers into ``negative`` / ``zero`` / ``small`` / ``large``.

    Uses match/case so the IDE has a non-trivial pattern-match block to
    fold and indent. ``small`` means absolute value ≤ 1.0.
    """
    result: dict[str, list[float]] = {"negative": [], "zero": [], "small": [], "large": []}
    for item in items:
        match item:
            case 0 | 0.0:
                result["zero"].append(item)
            case x if x < 0:
                result["negative"].append(item)
            case x if abs(x) <= 1.0:
                result["small"].append(item)
            case _:
                result["large"].append(item)
    return result


def unique_preserving_order(seq: Iterable[T]) -> list[T]:
    """Deduplicate while keeping the first-seen order."""
    seen: set[T] = set()
    out: list[T] = []
    for item in seq:
        if item in seen:
            continue
        seen.add(item)
        out.append(item)
    return out


def count_by(seq: Iterable[T], key: Callable[[T], U]) -> dict[U, int]:
    """Histogram of ``seq`` grouped by ``key``."""
    result: dict[U, int] = {}
    for item in seq:
        k = key(item)
        result[k] = result.get(k, 0) + 1
    return result


# -- Async iteration -------------------------------------------------------


async def aiter_pages(
    fetch_page: Callable[[int], Awaitable[list[T]]],
    *,
    start_page: int = 0,
    max_pages: int | None = None,
) -> AsyncIterator[T]:
    """Yield items from each page returned by ``fetch_page``.

    Stops when a page is empty (= caller's "no more pages" sentinel) or
    when ``max_pages`` is reached.
    """
    page = start_page
    while max_pages is None or (page - start_page) < max_pages:
        items = await fetch_page(page)
        if not items:
            return
        for item in items:
            yield item
        page += 1


async def amap(source: AsyncIterable[T], fn: Callable[[T], Awaitable[U]]) -> AsyncIterator[U]:
    """Async-map over ``source`` — ``fn`` may itself be async."""
    async for item in source:
        yield await fn(item)


async def afilter(source: AsyncIterable[T], pred: Callable[[T], Awaitable[bool]]) -> AsyncIterator[T]:
    """Async-filter — keep items where ``await pred(item)`` is truthy."""
    async for item in source:
        if await pred(item):
            yield item


# -- Generic single-pass aggregators ---------------------------------------


def first_or(seq: Iterable[T], default: T) -> T:
    """Return the first element if any, else ``default``."""
    for item in seq:
        return item
    return default


def reduce_inplace(seq: Iterable[T], op: Callable[[T, T], T]) -> T | None:
    """Variant of functools.reduce that returns None on empty input."""
    it = iter(seq)
    if (first := next(it, _SENTINEL)) is _SENTINEL:
        return None
    acc: T = first  # type: ignore[assignment]
    for item in it:
        acc = op(acc, item)
    return acc
