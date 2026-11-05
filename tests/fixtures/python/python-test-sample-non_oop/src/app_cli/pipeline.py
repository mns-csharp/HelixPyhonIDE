"""Generic pipeline of typed transformation steps.

A ``Pipeline[I, O]`` is a chain of ``Step`` instances where the first
step takes ``I`` and the last produces ``O``. Steps are composed via
the ``>>`` operator (``__rshift__``) which returns a new pipeline.

This file stresses the IDE on:
    - generic class with multiple TypeVars (I, M, O)
    - operator overloading with non-trivial type returns
    - ABC + several concrete subclasses (folding test)
    - higher-order callable typing via ``Callable[[I], O]``
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Callable, Generic, Iterable, Iterator, Protocol, TypeVar


I = TypeVar("I")
M = TypeVar("M")
O = TypeVar("O")
T = TypeVar("T")
U = TypeVar("U")


# -- Protocol ----------------------------------------------------------------


class Runnable(Protocol, Generic[I, O]):
    """Anything with a ``run(source)`` that turns an ``Iterable[I]`` into ``O``.

    ``Step`` satisfies this structurally in addition to inheriting the
    ABC below — the two typing styles coexist on the same class.
    """

    def run(self, source: Iterable[I]) -> O:
        ...


# -- Step ABC --------------------------------------------------------------


class Step(ABC, Generic[I, O]):
    """One node of a pipeline. Transforms an iterable of ``I`` into ``O``.

    Steps are pure: the same input always produces the same output, so
    pipelines are safe to re-run for replay/debugging.
    """

    name: str = ""

    @abstractmethod
    def run(self, source: Iterable[I]) -> O:
        """Process ``source`` and return the result of type ``O``."""
        ...

    def __rshift__(self, other: Step[O, T]) -> Pipeline[I, T]:
        """Compose two steps into a pipeline. ``a >> b`` runs ``a`` then ``b``."""
        return Pipeline([self, other])  # type: ignore[list-item]


# -- Concrete steps --------------------------------------------------------


class Filter(Step[T, Iterable[T]]):
    """Yield items where ``pred`` is truthy."""

    def __init__(self, pred: Callable[[T], bool], *, name: str = "filter") -> None:
        self.pred = pred
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[T]:
        return [item for item in source if self.pred(item)]


class Map(Step[T, Iterable[U]]):
    """Apply ``fn`` to every item."""

    def __init__(self, fn: Callable[[T], U], *, name: str = "map") -> None:
        self.fn = fn
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[U]:
        return [self.fn(item) for item in source]


class Reduce(Step[T, U]):
    """Fold ``op`` over the source starting from ``initial``."""

    def __init__(
        self,
        op: Callable[[U, T], U],
        initial: U,
        *,
        name: str = "reduce",
    ) -> None:
        self.op = op
        self.initial = initial
        self.name = name

    def run(self, source: Iterable[T]) -> U:
        acc = self.initial
        for item in source:
            acc = self.op(acc, item)
        return acc


class Tap(Step[T, Iterable[T]]):
    """Inspect each item without modifying the stream."""

    def __init__(self, observer: Callable[[T], None], *, name: str = "tap") -> None:
        self.observer = observer
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[T]:
        out: list[T] = []
        for item in source:
            self.observer(item)
            out.append(item)
        return out


class Take(Step[T, Iterable[T]]):
    """Keep only the first ``n`` items."""

    def __init__(self, n: int, *, name: str = "take") -> None:
        if n < 0:
            raise ValueError(f"n must be non-negative, got {n}")
        self.n = n
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[T]:
        out: list[T] = []
        for i, item in enumerate(source):
            if i >= self.n:
                break
            out.append(item)
        return out


class Sorted(Step[T, Iterable[T]]):
    """Sort items by an optional key function."""

    def __init__(
        self,
        key: Callable[[T], object] | None = None,
        *,
        reverse: bool = False,
        name: str = "sorted",
    ) -> None:
        self.key = key
        self.reverse = reverse
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[T]:
        return sorted(source, key=self.key, reverse=self.reverse)  # type: ignore[arg-type]


class Distinct(Step[T, Iterable[T]]):
    """Drop duplicates while preserving order."""

    def __init__(self, *, name: str = "distinct") -> None:
        self.name = name

    def run(self, source: Iterable[T]) -> Iterable[T]:
        seen: set[T] = set()
        out: list[T] = []
        for item in source:
            if item in seen:
                continue
            seen.add(item)
            out.append(item)
        return out


# -- Pipeline --------------------------------------------------------------


class Pipeline(Generic[I, O]):
    """Composed sequence of steps. Run via ``pipeline.run(source)``.

    Pipelines support ``>>`` for further composition, so::

        Map(int) >> Filter(lambda n: n > 0) >> Take(3)

    produces a single Pipeline[Iterable[str], Iterable[int]] (the type
    is inferred from the chain).
    """

    def __init__(self, steps: list[Step]) -> None:  # type: ignore[type-arg]
        if not steps:
            raise ValueError("pipeline needs at least one step")
        self.steps: list[Step] = list(steps)  # type: ignore[type-arg]

    def run(self, source: Iterable[I]) -> O:
        result: object = source
        for step in self.steps:
            result = step.run(result)  # type: ignore[arg-type]
        return result  # type: ignore[return-value]

    def __rshift__(self, other: Step[O, T] | Pipeline[O, T]) -> Pipeline[I, T]:
        if isinstance(other, Pipeline):
            return Pipeline(self.steps + other.steps)
        return Pipeline(self.steps + [other])  # type: ignore[list-item]

    def __len__(self) -> int:
        return len(self.steps)

    def __iter__(self) -> Iterator[Step]:  # type: ignore[type-arg]
        return iter(self.steps)

    def describe(self) -> str:
        """Render the chain as ``name1 >> name2 >> name3``."""
        return " >> ".join(s.name or type(s).__name__ for s in self.steps)


# -- Convenience constructors ----------------------------------------------


def from_iterable(it: Iterable[T]) -> Pipeline[T, Iterable[T]]:
    """Wrap an iterable in a no-op pipeline so the ``>>`` chaining lands cleanly."""
    return Pipeline([Map(lambda x: x, name="passthrough")])


def make_demo_pipeline() -> Pipeline[int, int]:
    """Build a small demo pipeline that sums the squares of positive evens.

    ``I = int`` because Step[I, O].run takes Iterable[I] — the pipeline
    consumes a stream of ints, not a stream of streams.
    """
    return Pipeline(
        [
            Filter(lambda x: x > 0, name="positives"),
            Filter(lambda x: x % 2 == 0, name="evens"),
            Map(lambda x: x * x, name="square"),
            Take(10, name="take_10"),
            Reduce(lambda acc, n: acc + n, 0, name="sum"),
        ]
    )
