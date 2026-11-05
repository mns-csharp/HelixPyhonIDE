"""Tests for :mod:`lib_utils.async_ops` — needs ``pytest-asyncio``."""
from __future__ import annotations

import sys
from pathlib import Path

_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

import asyncio  # noqa: E402

import pytest  # noqa: E402

from lib_utils.async_ops import (  # noqa: E402
    AsyncRateLimiter,
    TaskGroup,
    aclocked,
    gather_settled,
    gather_with_limit,
    retry_async,
    timeout_scope,
    with_timeout,
)


# -- gather_with_limit -----------------------------------------------------


class TestGatherWithLimit:
    @pytest.mark.asyncio
    async def test_returns_results_in_order(self):
        async def make(i: int) -> int:
            await asyncio.sleep(0)
            return i * 2

        result = await gather_with_limit(
            [make(i) for i in range(5)],
            max_concurrent=3,
        )
        assert result == [0, 2, 4, 6, 8]

    @pytest.mark.asyncio
    async def test_respects_concurrency_cap(self):
        active = 0
        peak = 0
        lock = asyncio.Lock()

        async def task() -> int:
            nonlocal active, peak
            async with lock:
                active += 1
                peak = max(peak, active)
            await asyncio.sleep(0.01)
            async with lock:
                active -= 1
            return 1

        await gather_with_limit([task() for _ in range(8)], max_concurrent=2)
        assert peak <= 2

    @pytest.mark.asyncio
    async def test_zero_max_concurrent_raises(self):
        with pytest.raises(ValueError):
            await gather_with_limit([], max_concurrent=0)


# -- gather_settled --------------------------------------------------------


class TestGatherSettled:
    @pytest.mark.asyncio
    async def test_all_succeed(self):
        async def ok(i: int) -> int:
            return i

        result = await gather_settled([ok(i) for i in range(3)])
        assert result == [0, 1, 2]

    @pytest.mark.asyncio
    async def test_mixed_results(self):
        async def ok() -> int:
            return 1

        async def fail() -> int:
            raise ValueError("boom")

        result = await gather_settled([ok(), fail(), ok()])
        assert result[0] == 1
        assert isinstance(result[1], ValueError)
        assert result[2] == 1


# -- with_timeout / timeout_scope ------------------------------------------


class TestTimeout:
    @pytest.mark.asyncio
    async def test_with_timeout_completes(self):
        async def quick() -> str:
            await asyncio.sleep(0)
            return "done"

        assert await with_timeout(quick(), seconds=1.0) == "done"

    @pytest.mark.asyncio
    async def test_with_timeout_expires(self):
        async def slow() -> None:
            await asyncio.sleep(1.0)

        with pytest.raises(asyncio.TimeoutError):
            await with_timeout(slow(), seconds=0.05)

    @pytest.mark.asyncio
    async def test_timeout_scope_exit_clean(self):
        async with timeout_scope(1.0):
            await asyncio.sleep(0)


# -- retry_async -----------------------------------------------------------


class TestRetryAsync:
    @pytest.mark.asyncio
    async def test_succeeds_first_try(self):
        async def factory() -> int:
            return 42

        assert await retry_async(factory, times=3) == 42

    @pytest.mark.asyncio
    async def test_retries_until_success(self):
        attempts = 0

        async def factory() -> str:
            nonlocal attempts
            attempts += 1
            if attempts < 3:
                raise ValueError("transient")
            return "ok"

        result = await retry_async(factory, times=5, on=(ValueError,), delay=0.0)
        assert result == "ok"
        assert attempts == 3

    @pytest.mark.asyncio
    async def test_exhausts_attempts(self):
        async def factory() -> None:
            raise RuntimeError("permanent")

        with pytest.raises(RuntimeError, match="permanent"):
            await retry_async(factory, times=2, on=(RuntimeError,), delay=0.0)

    def test_zero_times_raises(self):
        with pytest.raises(ValueError):
            asyncio.run(retry_async(lambda: asyncio.sleep(0), times=0))  # type: ignore[arg-type]


# -- AsyncRateLimiter ------------------------------------------------------


class TestRateLimiter:
    @pytest.mark.asyncio
    async def test_first_acquire_is_immediate(self):
        limiter = AsyncRateLimiter(per_second=10.0)
        import time
        start = time.perf_counter()
        await limiter.acquire()
        elapsed = time.perf_counter() - start
        assert elapsed < 0.05

    @pytest.mark.asyncio
    async def test_second_acquire_waits(self):
        limiter = AsyncRateLimiter(per_second=20.0)  # 50 ms interval
        import time
        await limiter.acquire()
        start = time.perf_counter()
        await limiter.acquire()
        elapsed = time.perf_counter() - start
        # Should be roughly 50 ms; allow generous tolerance.
        assert elapsed >= 0.03

    @pytest.mark.asyncio
    async def test_async_with_acquires(self):
        limiter = AsyncRateLimiter(per_second=100.0)
        async with limiter as l:
            assert l is limiter

    def test_invalid_per_second(self):
        with pytest.raises(ValueError):
            AsyncRateLimiter(per_second=0.0)


# -- TaskGroup -------------------------------------------------------------


class TestTaskGroup:
    @pytest.mark.asyncio
    async def test_spawn_and_join(self):
        tg = TaskGroup()
        results: list[int] = []

        async def worker(i: int) -> None:
            await asyncio.sleep(0)
            results.append(i)

        for i in range(5):
            tg.spawn(worker(i))
        await tg.join()
        assert sorted(results) == list(range(5))

    @pytest.mark.asyncio
    async def test_active_count(self):
        tg = TaskGroup()
        event = asyncio.Event()

        async def worker() -> None:
            await event.wait()

        tg.spawn(worker())
        tg.spawn(worker())
        assert tg.active_count == 2
        event.set()
        await tg.join()
        assert tg.active_count == 0

    @pytest.mark.asyncio
    async def test_cancel_all(self):
        tg = TaskGroup()

        async def worker() -> None:
            await asyncio.sleep(10.0)

        tg.spawn(worker())
        tg.spawn(worker())
        await tg.cancel_all()
        assert tg.active_count == 0


# -- aclocked decorator ----------------------------------------------------


class TestAclocked:
    @pytest.mark.asyncio
    async def test_propagates_return(self):
        @aclocked()
        async def add(a: int, b: int) -> int:
            return a + b

        assert await add(1, 2) == 3

    @pytest.mark.asyncio
    async def test_calls_sink(self):
        captured: list[tuple[str, float]] = []

        @aclocked(sink=lambda name, secs: captured.append((name, secs)))
        async def hello() -> str:
            return "hi"

        await hello()
        assert len(captured) == 1
        assert "hello" in captured[0][0]
        assert captured[0][1] >= 0.0
