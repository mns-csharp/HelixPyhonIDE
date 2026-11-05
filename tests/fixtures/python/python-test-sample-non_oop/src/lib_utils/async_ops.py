"""Async helpers: gather-with-limit, retry, timeout, async context managers.

Pure stdlib (asyncio + contextlib + functools) so no extra dependencies
are required.
"""
from __future__ import annotations

import asyncio
import functools
import time
from contextlib import asynccontextmanager
from typing import (
    Any,
    AsyncIterator,
    Awaitable,
    Callable,
    ParamSpec,
    Protocol,
    TypeVar,
)


P = ParamSpec("P")
R = TypeVar("R")


# -- Protocol ---------------------------------------------------------------


class AsyncRunnable(Protocol[R]):
    """Anything with a zero-argument async ``run()`` returning ``R``.

    Lets helpers in this module accept either a bare coroutine function
    or an object bundling extra state alongside its ``run`` behavior.
    """

    async def run(self) -> R:
        ...


# -- Bounded concurrency ---------------------------------------------------


async def gather_with_limit(
    coros: list[Awaitable[R]],
    *,
    max_concurrent: int,
) -> list[R]:
    """``asyncio.gather`` but at most ``max_concurrent`` coroutines run at once.

    Useful when fanning out to a slow downstream where unlimited
    concurrency would overwhelm the remote.
    """
    if max_concurrent <= 0:
        raise ValueError(f"max_concurrent must be positive, got {max_concurrent}")
    semaphore = asyncio.Semaphore(max_concurrent)

    async def guarded(coro: Awaitable[R]) -> R:
        async with semaphore:
            return await coro

    return await asyncio.gather(*(guarded(c) for c in coros))


async def gather_settled(
    coros: list[Awaitable[R]],
) -> list[R | BaseException]:
    """Like ``asyncio.gather(return_exceptions=True)``, with explicit typing."""
    return await asyncio.gather(*coros, return_exceptions=True)


# -- Timeouts --------------------------------------------------------------


async def with_timeout(coro: Awaitable[R], *, seconds: float) -> R:
    """Run ``coro`` with a wall-clock timeout.

    Raises ``asyncio.TimeoutError`` if it doesn't finish in time.
    """
    return await asyncio.wait_for(coro, timeout=seconds)


@asynccontextmanager
async def timeout_scope(seconds: float) -> AsyncIterator[None]:
    """Async context manager that cancels the protected block on timeout.

    Usage::

        async with timeout_scope(2.0):
            await long_running()
    """
    task = asyncio.current_task()
    if task is None:
        # No running loop / no task — degenerate to a no-op.
        yield
        return
    handle = asyncio.get_running_loop().call_later(seconds, task.cancel)
    try:
        yield
    finally:
        handle.cancel()


# -- Retry / backoff -------------------------------------------------------


async def retry_async(
    coro_factory: Callable[[], Awaitable[R]],
    *,
    times: int = 3,
    on: tuple[type[BaseException], ...] = (Exception,),
    delay: float = 0.1,
    backoff: float = 2.0,
) -> R:
    """Retry an async operation. ``coro_factory`` builds a fresh coroutine each attempt.

    Coroutines can only be awaited once, so callers pass a factory rather
    than a single coroutine. This is the canonical asyncio-retry pattern.
    """
    if times < 1:
        raise ValueError(f"times must be ≥1, got {times}")
    last_exc: BaseException | None = None
    current_delay = delay
    for attempt in range(times):
        try:
            return await coro_factory()
        except on as exc:
            last_exc = exc
            if attempt + 1 == times:
                break
            await asyncio.sleep(current_delay)
            current_delay *= backoff
    assert last_exc is not None
    raise last_exc


# -- Rate limiting ---------------------------------------------------------
 

class AsyncRateLimiter:
    """Simple async rate limiter: at most ``per_second`` permits per second.

    The limiter is non-bursty — if you call ``acquire()`` twice in the
    same instant, the second call awaits until ``1 / per_second``
    seconds have elapsed since the first.
    """

    def __init__(self, *, per_second: float) -> None:
        if per_second <= 0:
            raise ValueError(f"per_second must be positive, got {per_second}")
        self.per_second = per_second
        self._interval = 1.0 / per_second
        self._lock = asyncio.Lock()
        self._next_at = 0.0

    async def acquire(self) -> None:
        """Block until a permit is available."""
        async with self._lock:
            now = time.monotonic()
            wait_time = self._next_at - now
            if wait_time > 0:
                await asyncio.sleep(wait_time)
                self._next_at += self._interval
            else:
                self._next_at = now + self._interval

    async def __aenter__(self) -> "AsyncRateLimiter":
        await self.acquire()
        return self

    async def __aexit__(self, exc_type, exc, tb) -> None:
        return None


# -- Async function timing -------------------------------------------------


def aclocked(
    sink: Callable[[str, float], None] | None = None,
) -> Callable[[Callable[P, Awaitable[R]]], Callable[P, Awaitable[R]]]:
    """Async-aware ``@timed``. Times the awaited result, not the coroutine."""
    def decorator(fn: Callable[P, Awaitable[R]]) -> Callable[P, Awaitable[R]]:
        @functools.wraps(fn)
        async def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            start = time.perf_counter()
            try:
                return await fn(*args, **kwargs)
            finally:
                elapsed = time.perf_counter() - start
                qualname = getattr(fn, "__qualname__", repr(fn))
                if sink is not None:
                    sink(qualname, elapsed)
                else:
                    print(f"[aclocked] {qualname} took {elapsed * 1000:.3f} ms")

        return wrapper

    return decorator


# -- Background task management --------------------------------------------


class TaskGroup:
    """Lightweight wrapper that tracks tasks and ensures clean shutdown.

    Modelled after ``asyncio.TaskGroup`` (3.11+) but kept here as a
    portable example for IDE stress-testing.
    """

    def __init__(self) -> None:
        self._tasks: set[asyncio.Task[Any]] = set()

    def spawn(self, coro: Awaitable[R]) -> asyncio.Task[R]:
        """Schedule a coroutine and track its task."""
        task = asyncio.create_task(coro)  # type: ignore[arg-type]
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)
        return task

    async def join(self) -> None:
        """Wait for every tracked task to finish."""
        if not self._tasks:
            return
        await asyncio.gather(*self._tasks, return_exceptions=True)

    async def cancel_all(self) -> None:
        """Cancel every tracked task and wait for cancellation to propagate."""
        for task in list(self._tasks):
            if not task.done():
                task.cancel()
        await self.join()

    @property
    def active_count(self) -> int:
        """Number of tracked tasks that haven't finished."""
        return sum(1 for t in self._tasks if not t.done())
