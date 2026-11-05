"""Toy HTTP-shaped router. No real network I/O — just dispatch.

Every route is an ``async def`` function decorated with ``@route(...)``.
The :class:`RouterTable` walks a prefix tree to find the best match.
Lots of similar names (``get_user``, ``get_users``, ``get_user_by_id``)
exercise the IDE's completion-ranker.
"""
from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Iterable, Protocol

from lib_core.errors import ConfigurationError, RetryableError
from lib_utils.async_ops import retry_async


HandlerFn = Callable[["Request"], Awaitable["Response"]]


# -- Protocol ---------------------------------------------------------------


class RequestHandler(Protocol):
    """Anything callable as ``async def handler(request) -> Response``.

    ``HandlerFn`` above is the same shape expressed as a Callable alias;
    this Protocol form lets call sites accept an object with a matching
    ``__call__`` too, not just a bare function.
    """

    async def __call__(self, request: "Request") -> "Response":
        ...


# -- Request / Response models ---------------------------------------------


@dataclass(frozen=True, slots=True)
class Request:
    """Inbound request — frozen so handlers can't accidentally mutate it."""

    method: str
    path: str
    query: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    body: bytes = b""

    @property
    def path_segments(self) -> list[str]:
        """``/users/42/orders`` → ``['users', '42', 'orders']``."""
        return [seg for seg in self.path.split("/") if seg]


@dataclass(frozen=True, slots=True)
class Response:
    """Outbound response."""

    status: int
    headers: dict[str, str] = field(default_factory=dict)
    body: bytes = b""

    @classmethod
    def ok(cls, body: str = "") -> "Response":
        return cls(200, headers={"content-type": "text/plain"}, body=body.encode("utf-8"))

    @classmethod
    def not_found(cls, message: str = "not found") -> "Response":
        return cls(404, headers={"content-type": "text/plain"}, body=message.encode("utf-8"))

    @classmethod
    def server_error(cls, message: str = "internal error") -> "Response":
        return cls(500, headers={"content-type": "text/plain"}, body=message.encode("utf-8"))


# -- Route declaration -----------------------------------------------------


@dataclass(frozen=True, slots=True)
class RouteSpec:
    """How a handler is registered: pattern + allowed methods + priority."""

    path: str
    methods: frozenset[str]
    priority: int = 0


def route(
    path: str,
    *,
    methods: Iterable[str] = ("GET",),
    priority: int = 0,
) -> Callable[[HandlerFn], HandlerFn]:
    """Decorator: tag ``handler`` with route metadata.

    The metadata is read by :meth:`RouterTable.attach_handlers`.
    """
    spec = RouteSpec(
        path=path,
        methods=frozenset(m.upper() for m in methods),
        priority=priority,
    )

    def decorator(fn: HandlerFn) -> HandlerFn:
        fn._helix_route = spec  # type: ignore[attr-defined]
        return fn

    return decorator


# -- Router ----------------------------------------------------------------


class RouterTable:
    """Maps (method, path) to a handler. Last-registered wins on ties."""

    def __init__(self) -> None:
        self._routes: list[tuple[RouteSpec, HandlerFn]] = []

    def add(self, spec: RouteSpec, handler: HandlerFn) -> None:
        """Register ``handler`` under ``spec``. Sorted by priority on insertion."""
        self._routes.append((spec, handler))
        self._routes.sort(key=lambda item: -item[0].priority)

    def attach_handlers(self, container: Any) -> int:
        """Walk ``container`` and add every ``@route``-tagged callable."""
        count = 0
        for name in dir(container):
            attr = getattr(container, name, None)
            spec = getattr(attr, "_helix_route", None)
            if spec is None:
                continue
            self.add(spec, attr)
            count += 1
        return count

    def find(self, request: Request) -> HandlerFn | None:
        """Return the highest-priority handler whose pattern matches."""
        method = request.method.upper()
        for spec, handler in self._routes:
            if method not in spec.methods:
                continue
            if _matches(spec.path, request.path):
                return handler
        return None

    async def dispatch(self, request: Request) -> Response:
        """Look up and invoke the handler. 404 on no match, 500 on raise."""
        handler = self.find(request)
        if handler is None:
            return Response.not_found(f"no route for {request.method} {request.path}")
        try:
            return await handler(request)
        except RetryableError as exc:
            return Response(
                status=503,
                headers={"retry-after": str(int(max(0.0, exc.retry_after)))},
                body=str(exc).encode("utf-8"),
            )
        except Exception as exc:  # noqa: BLE001 — top-level dispatcher catches everything
            return Response.server_error(f"{type(exc).__name__}: {exc}")

    @property
    def route_count(self) -> int:
        return len(self._routes)


def _matches(pattern: str, path: str) -> bool:
    """Very simple pattern match: literal segments + ``:placeholder`` segments."""
    pat_segs = [s for s in pattern.split("/") if s]
    req_segs = [s for s in path.split("/") if s]
    if len(pat_segs) != len(req_segs):
        return False
    for p, r in zip(pat_segs, req_segs):
        if p.startswith(":"):
            continue  # placeholder matches any value
        if p != r:
            return False
    return True


# -- Sample handlers (lots of similar names for completion testing) --------


class UserRoutes:
    """A bag of user-related routes. Attach via ``router.attach_handlers``."""

    def __init__(self) -> None:
        self._users: dict[str, dict[str, Any]] = {
            "1": {"id": "1", "name": "Alice"},
            "2": {"id": "2", "name": "Bob"},
        }

    @route("/users", methods=("GET",))
    async def get_users(self, request: Request) -> Response:
        """List all users."""
        await asyncio.sleep(0)
        names = ", ".join(u["name"] for u in self._users.values())
        return Response.ok(f"users: {names}")

    @route("/users/:id", methods=("GET",))
    async def get_user(self, request: Request) -> Response:
        """Fetch a single user by id."""
        user_id = request.path_segments[-1]
        if user_id not in self._users:
            return Response.not_found(f"no user {user_id}")
        return Response.ok(f"user: {self._users[user_id]}")

    @route("/users/:id", methods=("DELETE",))
    async def delete_user(self, request: Request) -> Response:
        """Remove a user."""
        user_id = request.path_segments[-1]
        if user_id not in self._users:
            return Response.not_found(f"no user {user_id}")
        del self._users[user_id]
        return Response.ok(f"deleted {user_id}")

    @route("/users", methods=("POST",))
    async def create_user(self, request: Request) -> Response:
        """Create a user from the request body (just stores the bytes)."""
        new_id = str(len(self._users) + 1)
        self._users[new_id] = {"id": new_id, "name": request.body.decode("utf-8")}
        return Response.ok(f"created {new_id}")

    @route("/users/by-name/:name", methods=("GET",))
    async def get_user_by_name(self, request: Request) -> Response:
        """Look up a user by name."""
        target = request.path_segments[-1]
        for user in self._users.values():
            if user["name"].lower() == target.lower():
                return Response.ok(f"user: {user}")
        return Response.not_found(f"no user named {target}")

    @route("/users/by-tag/:tag", methods=("GET",))
    async def get_users_by_tag(self, request: Request) -> Response:
        """Look up users by tag (placeholder — doesn't track tags)."""
        return Response.ok("no tag index in this demo")


class HealthRoutes:
    """Health and version probes."""

    @route("/health", methods=("GET",), priority=10)
    async def get_health(self, request: Request) -> Response:
        """Liveness probe. Always 200 unless the server is on fire."""
        return Response.ok("OK")

    @route("/version", methods=("GET",))
    async def get_version(self, request: Request) -> Response:
        """Version string."""
        return Response.ok("HelixStudioDemo/0.1.0")


class FlakeyRoutes:
    """Demonstrates RetryableError handling."""

    def __init__(self) -> None:
        self._tries = 0

    @route("/flakey", methods=("GET",))
    async def get_flakey(self, request: Request) -> Response:
        """Fails twice then succeeds — exercise the retry path."""

        async def attempt() -> Response:
            self._tries += 1
            if self._tries < 3:
                raise RetryableError("warming up", retry_after=0.05)
            return Response.ok(f"ready after {self._tries} tries")

        return await retry_async(attempt, times=4, on=(RetryableError,), delay=0.01)


def make_default_router() -> RouterTable:
    """Build a router with every demo handler registered."""
    router = RouterTable()
    if (n := router.attach_handlers(UserRoutes())) <= 0:
        raise ConfigurationError("UserRoutes: no handlers attached")
    router.attach_handlers(HealthRoutes())
    router.attach_handlers(FlakeyRoutes())
    return router
