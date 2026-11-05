"""Middleware chain for the demo server.

A middleware is anything matching :class:`Middleware` — a callable that
receives ``(request, next_)`` where ``next_`` is the next link in the
chain. The base case (``next_`` returning the actual handler's
response) is the inner-most call.

Multiple middlewares compose left-to-right via :class:`MiddlewareChain`.
"""
from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from typing import Awaitable, Callable, Iterable, Protocol

from lib_utils.async_ops import AsyncRateLimiter
from lib_utils.logger import StructuredLogger

from .routes import Request, Response, RouterTable


Next = Callable[[Request], Awaitable[Response]]


# -- Protocol --------------------------------------------------------------


class Middleware(Protocol):
    """Anything that can sit between a Request and a handler.

    Implementations should:
      1. Optionally inspect/modify the request.
      2. Call ``await next_(request)`` to chain.
      3. Optionally inspect/modify the response.
      4. Return the (possibly modified) response.
    """

    async def __call__(self, request: Request, next_: Next) -> Response: ...


# -- Concrete middlewares --------------------------------------------------


class LoggingMiddleware:
    """Log every request + response status."""

    def __init__(self, logger: StructuredLogger) -> None:
        self.logger = logger

    async def __call__(self, request: Request, next_: Next) -> Response:
        start = time.perf_counter()
        self.logger.info(f"--> {request.method} {request.path}")
        response = await next_(request)
        elapsed_ms = (time.perf_counter() - start) * 1000.0
        self.logger.info(f"<-- {response.status} ({elapsed_ms:.1f} ms)")
        return response


class AuthMiddleware:
    """Reject requests without a valid bearer token in the ``authorization`` header.

    The ``valid_tokens`` set is checked verbatim; case-sensitive. In a
    real app this would consult a key-store / JWT verifier.
    """

    def __init__(self, valid_tokens: Iterable[str], *, exempt_paths: Iterable[str] = ()) -> None:
        self.valid_tokens = set(valid_tokens)
        self.exempt_paths = set(exempt_paths)

    async def __call__(self, request: Request, next_: Next) -> Response:
        if request.path in self.exempt_paths:
            return await next_(request)
        header = request.headers.get("authorization", "")
        prefix = "Bearer "
        if not header.startswith(prefix):
            return Response(401, body=b"missing or malformed authorization header")
        token = header[len(prefix):]
        if token not in self.valid_tokens:
            return Response(403, body=b"forbidden")
        return await next_(request)


class RateLimitMiddleware:
    """Reject requests once the global rate limit is exceeded."""

    def __init__(self, *, per_second: float) -> None:
        self.limiter = AsyncRateLimiter(per_second=per_second)

    async def __call__(self, request: Request, next_: Next) -> Response:
        await self.limiter.acquire()
        return await next_(request)


class CORSMiddleware:
    """Inject a CORS allow-origin header on every response."""

    def __init__(self, *, allow_origin: str = "*") -> None:
        self.allow_origin = allow_origin

    async def __call__(self, request: Request, next_: Next) -> Response:
        response = await next_(request)
        new_headers = dict(response.headers)
        new_headers["access-control-allow-origin"] = self.allow_origin
        return Response(
            status=response.status,
            headers=new_headers,
            body=response.body,
        )


class TimingMiddleware:
    """Stamp a ``server-timing`` header on every response."""

    async def __call__(self, request: Request, next_: Next) -> Response:
        start = time.perf_counter()
        response = await next_(request)
        elapsed_ms = (time.perf_counter() - start) * 1000.0
        new_headers = dict(response.headers)
        new_headers["server-timing"] = f"app;dur={elapsed_ms:.1f}"
        return Response(response.status, headers=new_headers, body=response.body)


# -- Chain -----------------------------------------------------------------


@dataclass
class MiddlewareChain:
    """Composes middlewares around a router. Outermost first."""

    router: RouterTable
    middlewares: list[Middleware] = field(default_factory=list)

    def use(self, middleware: Middleware) -> "MiddlewareChain":
        """Append ``middleware``. Returns self so calls chain."""
        self.middlewares.append(middleware)
        return self

    def build(self) -> Next:
        """Compose every middleware around ``router.dispatch``."""
        terminal: Next = self.router.dispatch
        chain: Next = terminal
        # Wrap from innermost to outermost so the first .use() ends up on
        # the outside.
        for mw in reversed(self.middlewares):
            chain = self._wrap(mw, chain)
        return chain

    @staticmethod
    def _wrap(mw: Middleware, downstream: Next) -> Next:
        async def wrapped(request: Request) -> Response:
            return await mw(request, downstream)

        return wrapped

    async def handle(self, request: Request) -> Response:
        """Build and invoke in one shot. Convenient for tests."""
        return await self.build()(request)


# -- Convenience factory ---------------------------------------------------


def default_chain(
    router: RouterTable,
    *,
    auth_tokens: Iterable[str] = ("dev-token",),
    rate_per_second: float = 100.0,
    logger: StructuredLogger | None = None,
) -> MiddlewareChain:
    """Build a chain with a sensible default stack."""
    chain = MiddlewareChain(router=router)
    log: StructuredLogger = logger if logger is not None else StructuredLogger(prefix="server")
    chain.use(LoggingMiddleware(log))
    chain.use(TimingMiddleware())
    chain.use(CORSMiddleware())
    chain.use(RateLimitMiddleware(per_second=rate_per_second))
    chain.use(AuthMiddleware(auth_tokens, exempt_paths={"/health", "/version"}))
    return chain


# -- Synchronous shim ------------------------------------------------------


def run_request_sync(chain: MiddlewareChain, request: Request) -> Response:
    """Convenience helper for sync tests/demos.

    Spins up an event loop just for this call. Don't use inside an
    already-running loop — call ``await chain.handle(...)`` instead.
    """
    return asyncio.run(chain.handle(request))
