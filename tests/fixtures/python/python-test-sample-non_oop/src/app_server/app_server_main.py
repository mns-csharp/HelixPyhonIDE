"""Server-style demo: async router + middleware chain + storage + animals.

Runnable end-to-end via ``python -m app_server`` once the workspace is
installed. The "server" doesn't actually open a socket — it just routes
synthetic ``Request`` objects through the middleware chain to its
handlers, then prints the responses.
"""
from __future__ import annotations

import asyncio
import time

from lib_core.animals import (
    Dog,
    Eagle,
    Habitat,
    PoliceDog,
    Snake,
    Wolf,
    chorus,
    feed,
    sleep_animal,
)
from lib_core.events import EventBus, KeyValueChange
from lib_core.storage import InMemoryStorage, InMemoryTypedStorage
from lib_utils.async_ops import TaskGroup, gather_with_limit
from lib_utils.logger import Level, Logger, StructuredLogger

from app_server.middleware import default_chain
from app_server.routes import Request, make_default_router


# -- Legacy sync demo -------------------------------------------------------


def legacy_demo() -> None:
    """The original tiny demo, kept so existing call sites keep working."""
    log = Logger.from_config("server")
    store = InMemoryStorage()

    rex = Dog("rex")
    k9 = PoliceDog("k9", 42)

    store.set("house_pet", rex)
    store.set("on_duty", k9)

    log.info("registered pet: " + str(store.get("house_pet")))
    log.info("on-duty says: " + k9.speak())
    log.warn("end of demo")


# -- Async server demo ------------------------------------------------------


async def serve_demo_requests(*, request_count: int = 6) -> list[tuple[int, str]]:
    """Issue a few requests through the middleware chain. Returns (status, body) pairs.

    Demonstrates: async router dispatch, middleware composition,
    rate-limited concurrency via ``gather_with_limit``.
    """
    router = make_default_router()
    chain = default_chain(
        router,
        auth_tokens={"dev-token"},
        rate_per_second=20.0,
        logger=StructuredLogger(prefix="server", level=Level.INFO),
    )

    async def issue(req: Request) -> tuple[int, str]:
        response = await chain.handle(req)
        return response.status, response.body.decode("utf-8")

    requests: list[Request] = [
        Request("GET", "/health"),
        Request("GET", "/version"),
        Request("GET", "/users", headers={"authorization": "Bearer dev-token"}),
        Request("GET", "/users/1", headers={"authorization": "Bearer dev-token"}),
        Request("POST", "/users", headers={"authorization": "Bearer dev-token"}, body=b"Charlie"),
        Request("GET", "/flakey", headers={"authorization": "Bearer dev-token"}),
    ][:request_count]

    return await gather_with_limit([issue(r) for r in requests], max_concurrent=2)


# -- Animal demo (async) ---------------------------------------------------


async def animal_demo() -> None:
    """Build a habitat, feed everyone concurrently, run a chorus."""
    pack = Habitat[Wolf]("North Ridge", capacity=3)
    pack.admit(Wolf("Akela"))
    pack.admit(Wolf("Raksha", pack="North Ridge"))
    pack.admit(Wolf("Grey"))

    aviary = Habitat[Eagle]("Cliff Hide", capacity=2)
    aviary.admit(Eagle("Tarkus"))

    snakes = Habitat[Snake]("Reptile House", capacity=4)
    snakes.admit(Snake("Kaa"))
    snakes.admit(Snake("Nag", venomous=True))

    log = StructuredLogger(prefix="zoo")
    log.info(f"pack={len(pack)}, aviary={len(aviary)}, snakes={len(snakes)}")

    # Concurrent feeding.
    async with TaskGroupCtx() as tg:
        for animal in pack:
            tg.spawn(feed(animal, ration_grams=200))
        for animal in aviary:
            tg.spawn(feed(animal, ration_grams=120))
        for animal in snakes:
            tg.spawn(feed(animal, ration_grams=80))

    # Chorus.
    sounds = await chorus(list(pack) + list(aviary) + list(snakes))
    log.info("chorus: " + ", ".join(sounds))

    # Sleep them all.
    sleep_results = await asyncio.gather(
        *(sleep_animal(a, hours=8.0) for a in (*pack, *aviary, *snakes)),
    )
    for r in sleep_results:
        log.info(r)


class TaskGroupCtx:
    """Tiny async context manager that wraps :class:`TaskGroup`.

    Spawns inside ``__aenter__`` would be premature; this helper exists
    only so the async-with syntax in :func:`animal_demo` stays clean.
    """

    def __init__(self) -> None:
        self.group = TaskGroup()

    async def __aenter__(self) -> TaskGroup:
        return self.group

    async def __aexit__(self, exc_type, exc, tb) -> None:
        await self.group.join()


# -- Storage + events demo --------------------------------------------------


async def storage_demo() -> None:
    """Wire a typed storage to an EventBus and watch the dispatches happen."""
    storage = InMemoryTypedStorage[str, str]()
    bus = EventBus()
    log = StructuredLogger(prefix="store")
    seen: list[str] = []

    def on_change(ev: KeyValueChange) -> None:
        seen.append(f"{ev.key}={ev.new_value}")

    bus.subscribe(KeyValueChange, on_change)

    storage.set("alpha", "1")
    bus.dispatch(KeyValueChange(key="alpha", new_value="1", source="store"))
    storage.set("beta", "2")
    bus.dispatch(KeyValueChange(key="beta", new_value="2", source="store"))

    log.info(f"recorded changes: {seen}")
    bus.unsubscribe(KeyValueChange, on_change)


# -- Entry points ----------------------------------------------------------


def main() -> None:
    """Synchronous entry point: legacy demo + async demos via ``asyncio.run``."""
    print("== legacy demo ==")
    legacy_demo()

    print("\n== async server demo ==")
    started = time.perf_counter()
    responses = asyncio.run(serve_demo_requests())
    elapsed = (time.perf_counter() - started) * 1000.0
    for i, (status, body) in enumerate(responses):
        print(f"  [{i}] {status} {body}")
    print(f"  total {elapsed:.1f} ms")

    print("\n== async animal demo ==")
    asyncio.run(animal_demo())

    print("\n== storage + events demo ==")
    asyncio.run(storage_demo())


if __name__ == "__main__":
    main()
