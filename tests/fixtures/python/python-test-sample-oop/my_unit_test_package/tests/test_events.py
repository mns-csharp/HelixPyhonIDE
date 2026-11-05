"""Tests for :class:`lib_core.events.EventBus` — sync + async dispatch."""
from __future__ import annotations

import sys
from pathlib import Path

_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

import asyncio  # noqa: E402

import pytest  # noqa: E402

from lib_core.errors import HandlerError  # noqa: E402
from lib_core.events import (  # noqa: E402
    Event,
    EventBus,
    IntPayload,
    KeyValueChange,
    StringPayload,
    on,
)


# -- Fixtures --------------------------------------------------------------


@pytest.fixture
def bus() -> EventBus:
    """Fresh, empty bus for each test."""
    return EventBus()


@pytest.fixture
def listener() -> "Listener":
    """Class-based listener decorated with @on(...)."""
    return Listener()


class Listener:
    """Sample listener for fixture-based tests.

    Methods are tagged with @on(EventCls). The bus's
    ``attach_handlers`` walks the listener and subscribes each one.
    """

    def __init__(self) -> None:
        self.strings: list[str] = []
        self.ints: list[int] = []
        self.changes: list[tuple[str, object]] = []
        self.broad: list[Event] = []

    @on(StringPayload)
    def on_string(self, ev: StringPayload) -> None:
        self.strings.append(ev.text)

    @on(IntPayload)
    def on_int(self, ev: IntPayload) -> None:
        self.ints.append(ev.value)

    @on(KeyValueChange)
    def on_change(self, ev: KeyValueChange) -> None:
        self.changes.append((ev.key, ev.new_value))

    @on(Event)
    def on_any(self, ev: Event) -> None:
        self.broad.append(ev)


# -- Subscribe / dispatch --------------------------------------------------


class TestSubscribe:
    def test_subscribe_and_dispatch_string(self, bus, listener):
        bus.subscribe(StringPayload, listener.on_string)
        bus.dispatch(StringPayload(text="hello"))
        assert listener.strings == ["hello"]

    def test_dispatch_returns_handler_count(self, bus, listener):
        bus.subscribe(StringPayload, listener.on_string)
        n = bus.dispatch(StringPayload(text="x"))
        assert n == 1

    def test_no_handler_dispatch_returns_zero(self, bus):
        # No subscriptions → dispatch is a no-op returning 0.
        assert bus.dispatch(StringPayload(text="x")) == 0

    def test_unsubscribe(self, bus, listener):
        bus.subscribe(StringPayload, listener.on_string)
        bus.unsubscribe(StringPayload, listener.on_string)
        bus.dispatch(StringPayload(text="x"))
        assert listener.strings == []

    def test_unsubscribe_nonexistent_is_noop(self, bus, listener):
        # Should not raise.
        bus.unsubscribe(StringPayload, listener.on_string)


# -- attach_handlers via @on ------------------------------------------------


class TestAttachHandlers:
    def test_attach_subscribes_decorated_methods(self, bus, listener):
        n = bus.attach_handlers(listener)
        # Listener has 4 @on-tagged methods.
        assert n == 4

    def test_attached_handlers_run(self, bus, listener):
        bus.attach_handlers(listener)
        bus.dispatch(StringPayload(text="hi"))
        bus.dispatch(IntPayload(value=42))
        assert listener.strings == ["hi"]
        assert listener.ints == [42]

    def test_subclass_event_walks_mro(self, bus, listener):
        bus.attach_handlers(listener)
        # KeyValueChange is a subclass of Event — both handlers fire.
        bus.dispatch(KeyValueChange(key="alpha", new_value=1))
        assert listener.changes == [("alpha", 1)]
        # The broad Event handler also saw it.
        assert any(isinstance(e, KeyValueChange) for e in listener.broad)


# -- Weakref subscription lifetime -----------------------------------------


class TestWeakSubscriptions:
    def test_dead_subscriber_pruned(self, bus):
        listener = Listener()
        bus.subscribe(StringPayload, listener.on_string)
        # Drop the only reference to listener.
        del listener
        # The handler is a WeakMethod; after GC it's gone.
        import gc
        gc.collect()
        # Dispatch returns 0 because the weak ref is dead.
        assert bus.dispatch(StringPayload(text="x")) == 0

    def test_subscription_count_purges_dead(self, bus):
        listener = Listener()
        bus.subscribe(StringPayload, listener.on_string)
        assert bus.subscription_count == 1
        del listener
        import gc
        gc.collect()
        assert bus.subscription_count == 0


# -- Error handling --------------------------------------------------------


class TestErrorHandling:
    def test_handler_exception_wrapped(self, bus):
        def raises(ev: StringPayload) -> None:
            raise ValueError("boom")

        bus.subscribe(StringPayload, raises)
        with pytest.raises(HandlerError) as exc_info:
            bus.dispatch(StringPayload(text="x"))
        assert isinstance(exc_info.value.original, ValueError)


# -- Async dispatch --------------------------------------------------------


class TestAsyncPublish:
    @pytest.mark.asyncio
    async def test_publish_runs_async_handler(self, bus):
        seen: list[str] = []

        async def handler(ev: StringPayload) -> None:
            await asyncio.sleep(0)
            seen.append(ev.text)

        bus.subscribe(StringPayload, handler)
        n = await bus.publish(StringPayload(text="async"))
        assert n == 1
        assert seen == ["async"]

    @pytest.mark.asyncio
    async def test_publish_multiple_handlers_concurrent(self, bus):
        seen: list[str] = []

        async def slow(ev: StringPayload) -> None:
            await asyncio.sleep(0.01)
            seen.append("slow")

        async def fast(ev: StringPayload) -> None:
            seen.append("fast")

        bus.subscribe(StringPayload, slow)
        bus.subscribe(StringPayload, fast)
        await bus.publish(StringPayload(text="x"))
        # Both handlers ran (order is gather-dependent but both fired).
        assert set(seen) == {"slow", "fast"}

    @pytest.mark.asyncio
    async def test_publish_mixed_sync_async(self, bus):
        async_seen: list[int] = []

        async def async_h(ev: IntPayload) -> None:
            async_seen.append(ev.value)

        sync_seen: list[int] = []

        def sync_h(ev: IntPayload) -> None:
            sync_seen.append(ev.value)

        bus.subscribe(IntPayload, async_h)
        bus.subscribe(IntPayload, sync_h)
        await bus.publish(IntPayload(value=7))
        assert async_seen == [7]
        assert sync_seen == [7]


# -- Bus housekeeping ------------------------------------------------------


class TestBusLifecycle:
    def test_clear_drops_subscriptions(self, bus, listener):
        bus.attach_handlers(listener)
        bus.clear()
        assert bus.subscription_count == 0
        assert bus.dispatch(StringPayload(text="x")) == 0

    def test_subscribe_rejects_non_callable(self, bus):
        with pytest.raises(TypeError):
            bus.subscribe(StringPayload, "not a callable")  # type: ignore[arg-type]
