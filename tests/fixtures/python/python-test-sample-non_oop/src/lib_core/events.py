"""Event-bus subsystem: typed events, subscriber registry, sync + async dispatch.

The bus keeps weak references to bound-method subscribers so listener
lifetime tracks the owning object — common pattern in long-lived apps
where forgetting to unsubscribe leaks state.

Design:
  - :class:`Event` and :class:`Payload` are frozen dataclasses; the
    base ``Event`` carries metadata (timestamp, source) that all
    subclasses inherit.
  - :class:`EventBus` registers handlers under event-class keys and
    walks the MRO so a handler for ``Event`` sees every subclass too.
  - :class:`@on(EventCls)` decorator marks a function for declarative
    registration via ``EventBus.attach_handlers(module_or_class)``.
"""
from __future__ import annotations

import asyncio
import time
import weakref
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Generic, Protocol, TypeVar

from .errors import HandlerError


# -- Event types -----------------------------------------------------------


@dataclass(frozen=True)
class Event:
    """Base event class. All real events inherit from this.

    :param source: Free-form identifier for the publisher; useful in
        logs and for filtering.
    :param timestamp: Monotonic time the event was created. Defaults
        to ``time.monotonic()`` at construction.
    """

    source: str = ""
    timestamp: float = field(default_factory=time.monotonic)


@dataclass(frozen=True)
class StringPayload(Event):
    """An event that carries a plain string payload."""

    text: str = ""


@dataclass(frozen=True)
class IntPayload(Event):
    """An event that carries a single integer."""

    value: int = 0


@dataclass(frozen=True)
class KeyValueChange(Event):
    """A storage backend changed: a key was set or deleted."""

    key: str = ""
    new_value: Any = None
    old_value: Any = None


@dataclass(frozen=True)
class ErrorRaised(Event):
    """Something raised — published by HandlerError-aware code."""

    error_code: str = ""
    message: str = ""


# -- Handler interface ------------------------------------------------------


E_co = TypeVar("E_co", bound=Event, covariant=True)


class EventHandler(Protocol, Generic[E_co]):
    """A callable that consumes events of type ``E_co``.

    Sync and async handlers both satisfy this — the bus checks for the
    awaitable return at dispatch time.
    """

    def __call__(self, event: E_co) -> Awaitable[None] | None: ...


# -- Bus implementation -----------------------------------------------------


class EventBus:
    """In-process event bus. Sync ``dispatch`` + async ``publish``.

    Handlers are kept in a list per event class. When the bus dispatches
    an event of type ``E``, it walks ``E.__mro__`` and invokes every
    handler registered against any superclass — so a single handler
    against ``Event`` sees every subclass.

    Bound-method handlers are stored as weak references via
    ``weakref.WeakMethod`` so that subscriber lifetime tracks the
    owning object. Free functions are stored as strong references
    (they're typically module-level and live forever).
    """

    def __init__(self) -> None:
        self._handlers: dict[type[Event], list[Any]] = {}

    def subscribe(
        self,
        event_type: type[E_co],
        handler: EventHandler[E_co],
    ) -> None:
        """Register ``handler`` for events of type ``event_type``.

        :raises TypeError: if ``handler`` is not callable.
        """
        if not callable(handler):
            raise TypeError(f"handler must be callable, got {type(handler).__name__}")
        ref: Any
        # Use weakref for bound methods so subscriber lifetime matches
        # the object that owns the method.
        if hasattr(handler, "__self__"):
            ref = weakref.WeakMethod(handler)  # type: ignore[arg-type]
        else:
            ref = handler  # strong ref
        self._handlers.setdefault(event_type, []).append(ref)

    def unsubscribe(
        self,
        event_type: type[E_co],
        handler: EventHandler[E_co],
    ) -> None:
        """Remove a previously-subscribed handler. No-op if not registered."""
        bucket = self._handlers.get(event_type)
        if not bucket:
            return
        new_bucket = []
        for ref in bucket:
            current = ref() if isinstance(ref, weakref.WeakMethod) else ref
            if current is None or current == handler:
                continue
            new_bucket.append(ref)
        if new_bucket:
            self._handlers[event_type] = new_bucket
        else:
            self._handlers.pop(event_type, None)

    def _resolve_handlers(self, event: Event) -> list[Callable[[Event], Any]]:
        """Walk the event's MRO and collect every live handler."""
        out: list[Callable[[Event], Any]] = []
        for cls in type(event).__mro__:
            if not isinstance(cls, type) or not issubclass(cls, Event):
                continue
            bucket = self._handlers.get(cls)
            if not bucket:
                continue
            for ref in bucket:
                if isinstance(ref, weakref.WeakMethod):
                    fn = ref()
                    if fn is None:
                        continue  # subscriber died
                    out.append(fn)
                else:
                    out.append(ref)
        return out

    def dispatch(self, event: Event) -> int:
        """Invoke every matching handler synchronously.

        Async handlers are scheduled on the running loop if present, or
        run-to-completion via ``asyncio.run`` if no loop exists. Returns
        the number of handlers invoked.
        """
        handlers = self._resolve_handlers(event)
        for fn in handlers:
            try:
                result = fn(event)
                if asyncio.iscoroutine(result):
                    # Best-effort: run-to-completion if no loop, else fire-and-forget.
                    try:
                        loop = asyncio.get_running_loop()
                        loop.create_task(result)
                    except RuntimeError:
                        asyncio.run(result)
            except Exception as exc:
                raise HandlerError(getattr(fn, "__name__", repr(fn)), exc) from exc
        return len(handlers)

    async def publish(self, event: Event) -> int:
        """Async dispatch — awaits async handlers concurrently."""
        handlers = self._resolve_handlers(event)
        coros = []
        for fn in handlers:
            try:
                result = fn(event)
            except Exception as exc:
                raise HandlerError(getattr(fn, "__name__", repr(fn)), exc) from exc
            if asyncio.iscoroutine(result):
                coros.append(result)
        if coros:
            await asyncio.gather(*coros)
        return len(handlers)

    def attach_handlers(self, container: Any) -> int:
        """Walk ``container``'s attributes and subscribe anything tagged with ``@on``.

        Returns the number of handlers registered.
        """
        count = 0
        for name in dir(container):
            attr = getattr(container, name, None)
            event_type = getattr(attr, "_helix_event_type", None)
            if event_type is not None:
                self.subscribe(event_type, attr)
                count += 1
        return count

    def clear(self) -> None:
        """Drop every subscription."""
        self._handlers.clear()

    @property
    def subscription_count(self) -> int:
        """Total number of live subscriptions (purges dead weakrefs)."""
        n = 0
        for bucket in self._handlers.values():
            for ref in bucket:
                if isinstance(ref, weakref.WeakMethod):
                    if ref() is not None:
                        n += 1
                else:
                    n += 1
        return n


# -- Decorator for declarative subscription --------------------------------


def on(event_type: type[E_co]) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
    """Mark a function/method as a handler for ``event_type``.

    Usage::

        class Listener:
            @on(StringPayload)
            def handle(self, ev: StringPayload) -> None:
                print(ev.text)

        bus = EventBus()
        listener = Listener()
        bus.attach_handlers(listener)

    The decorator only attaches metadata; subscription happens when
    :meth:`EventBus.attach_handlers` walks the container.
    """
    def decorator(fn: Callable[..., Any]) -> Callable[..., Any]:
        fn._helix_event_type = event_type  # type: ignore[attr-defined]
        return fn
    return decorator
