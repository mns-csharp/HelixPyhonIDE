"""Decorator factory zoo: @retry, @timed, @memoize, @traced, @require_role.

Every decorator preserves the wrapped callable's type signature via
``ParamSpec`` + ``TypeVar`` so the IDE's signature-help and completion
keep working through the wrapper.

Convention: each decorator factory returns a decorator (the inner
function), which in turn returns the wrapper (the actual replacement).
This three-level structure exercises the IDE's nested-function fold
and scope analysis.
"""
from __future__ import annotations

import asyncio
import functools
import time
from typing import Any, Awaitable, Callable, Iterable, ParamSpec, TypeVar


P = ParamSpec("P")
R = TypeVar("R")


# -- @retry -----------------------------------------------------------------


def retry(
    *,
    times: int = 3,
    on: tuple[type[BaseException], ...] = (Exception,),
    delay: float = 0.0,
    backoff: float = 1.0,
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Retry the wrapped callable up to ``times`` total attempts.

    :param times: Maximum number of attempts. ``times=1`` means "no retry".
    :param on: Tuple of exception types that should trigger a retry.
        Anything else propagates immediately.
    :param delay: Seconds to wait before the second attempt.
    :param backoff: Multiplier applied to ``delay`` between successive retries.
        ``1.0`` means constant delay; ``2.0`` doubles each time.
    :raises ValueError: if ``times < 1``.
    """
    if times < 1:
        raise ValueError(f"times must be ≥1, got {times}")

    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        @functools.wraps(fn)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            last_exc: BaseException | None = None
            current_delay = delay
            for attempt in range(times):
                try:
                    return fn(*args, **kwargs)
                except on as exc:
                    last_exc = exc
                    if attempt + 1 == times:
                        break
                    if current_delay > 0:
                        time.sleep(current_delay)
                        current_delay *= backoff
            assert last_exc is not None
            raise last_exc

        return wrapper

    return decorator


def async_retry(
    *,
    times: int = 3,
    on: tuple[type[BaseException], ...] = (Exception,),
    delay: float = 0.0,
    backoff: float = 1.0,
) -> Callable[[Callable[P, Awaitable[R]]], Callable[P, Awaitable[R]]]:
    """Async sibling of :func:`retry`."""
    if times < 1:
        raise ValueError(f"times must be ≥1, got {times}")

    def decorator(fn: Callable[P, Awaitable[R]]) -> Callable[P, Awaitable[R]]:
        @functools.wraps(fn)
        async def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            last_exc: BaseException | None = None
            current_delay = delay
            for attempt in range(times):
                try:
                    return await fn(*args, **kwargs)
                except on as exc:
                    last_exc = exc
                    if attempt + 1 == times:
                        break
                    if current_delay > 0:
                        await asyncio.sleep(current_delay)
                        current_delay *= backoff
            assert last_exc is not None
            raise last_exc

        return wrapper

    return decorator


# -- @timed -----------------------------------------------------------------


def timed(
    *,
    label: str | None = None,
    sink: Callable[[str, float], None] | None = None,
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Measure the wrapped callable's wall-clock duration.

    The duration is reported via ``sink(label, seconds)`` if provided,
    else ``print``ed.
    """
    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        name = label or getattr(fn, "__qualname__", repr(fn))

        @functools.wraps(fn)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            start = time.perf_counter()
            try:
                return fn(*args, **kwargs)
            finally:
                elapsed = time.perf_counter() - start
                if sink is not None:
                    sink(name, elapsed)
                else:
                    print(f"[timed] {name} took {elapsed * 1000:.3f} ms")

        return wrapper

    return decorator


# -- @memoize ---------------------------------------------------------------


def memoize(
    *,
    max_size: int | None = None,
    key: Callable[..., Any] | None = None,
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Cache the wrapped callable's results in an LRU-style dict.

    When ``max_size`` is set, the oldest entry is evicted on overflow.
    A custom ``key`` function may project args+kwargs into a hashable
    cache key (default: ``(args, frozenset(kwargs.items()))``).
    """
    def default_key(args: tuple[Any, ...], kwargs: dict[str, Any]) -> Any:
        return (args, frozenset(kwargs.items()))

    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        cache: dict[Any, R] = {}
        order: list[Any] = []  # FIFO for eviction

        @functools.wraps(fn)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            k = key(*args, **kwargs) if key is not None else default_key(args, kwargs)
            if k in cache:
                return cache[k]
            value = fn(*args, **kwargs)
            cache[k] = value
            order.append(k)
            if max_size is not None and len(order) > max_size:
                evict = order.pop(0)
                cache.pop(evict, None)
            return value

        # Stash for inspection / testing.
        wrapper.cache = cache  # type: ignore[attr-defined]
        wrapper.cache_clear = lambda: (cache.clear(), order.clear())  # type: ignore[attr-defined]
        return wrapper

    return decorator


# -- @traced ----------------------------------------------------------------


def traced(
    logger: Callable[[str], None] | None = None,
    *,
    include_args: bool = True,
    include_result: bool = False,
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Log entry/exit of the wrapped callable.

    :param logger: Function that takes a single string and emits it
        (e.g. ``logging.getLogger(...).info``). Defaults to ``print``.
    :param include_args: If True, render arguments in the entry log.
    :param include_result: If True, render the return value in the exit log.
    """
    sink = logger if logger is not None else print

    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        @functools.wraps(fn)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            if include_args:
                sink(f"-> {fn.__qualname__}({_render_args(args, kwargs)})")
            else:
                sink(f"-> {fn.__qualname__}")
            try:
                result = fn(*args, **kwargs)
            except BaseException as exc:
                sink(f"!! {fn.__qualname__} raised {type(exc).__name__}")
                raise
            if include_result:
                sink(f"<- {fn.__qualname__} = {result!r}")
            else:
                sink(f"<- {fn.__qualname__}")
            return result

        return wrapper

    return decorator


def _render_args(args: tuple[Any, ...], kwargs: dict[str, Any]) -> str:
    """Render a comma-separated arglist for trace output."""
    parts: list[str] = [repr(a) for a in args]
    parts.extend(f"{k}={v!r}" for k, v in kwargs.items())
    return ", ".join(parts)


# -- @require_role ----------------------------------------------------------


class RoleDeniedError(PermissionError):
    """Raised by :func:`require_role` when the caller lacks the role."""


def require_role(
    role: str,
    *,
    role_resolver: Callable[[], Iterable[str]] | None = None,
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Decorator: raise ``RoleDeniedError`` if the caller doesn't hold ``role``.

    The default ``role_resolver`` reads from a thread-local-style global
    (``_active_roles``) — call sites configure it via ``set_active_roles``
    before invoking guarded functions. In production you'd plug in your
    web framework's auth context here.
    """
    resolver = role_resolver if role_resolver is not None else _default_role_resolver

    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        @functools.wraps(fn)
        def wrapper(*args: P.args, **kwargs: P.kwargs) -> R:
            roles = list(resolver())
            if role not in roles:
                raise RoleDeniedError(
                    f"{fn.__qualname__} requires role {role!r}; have {roles}"
                )
            return fn(*args, **kwargs)

        return wrapper

    return decorator


# Module-level "active roles" registry — replace with thread-local in
# real code. Kept simple here for stress-test purposes.
_active_roles: list[str] = []


def set_active_roles(roles: Iterable[str]) -> None:
    """Install the active role set for the current process."""
    _active_roles[:] = list(roles)


def _default_role_resolver() -> Iterable[str]:
    return list(_active_roles)


# -- Composition utilities --------------------------------------------------


def chain_decorators(
    *decorators: Callable[[Callable[P, R]], Callable[P, R]],
) -> Callable[[Callable[P, R]], Callable[P, R]]:
    """Apply decorators left-to-right.

    ``chain_decorators(a, b)(fn)`` is equivalent to ``a(b(fn))`` —
    so the first argument is the outermost wrapper.
    """
    def decorator(fn: Callable[P, R]) -> Callable[P, R]:
        result = fn
        for d in reversed(decorators):
            result = d(result)
        return result

    return decorator
