"""CLI demo: drives the rest of the workspace end-to-end.

Builds a small "zoo" of animals + a canvas of shapes, runs them through
the command bus, dispatches a few events, and prints a summary. The
demo is meant to *run* cleanly under HelixStudio's Run command — every
import resolves and every API used here is real.

The legacy intentional bug at line 22 (passing a list of ints to a
typed-Shape function) stays so the Problems tab has something to
display.
"""
from __future__ import annotations

import os
import os.path

from lib_core.animals import (
    Cat,
    Dog,
    Eagle,
    GuideDog,
    Habitat,
    Parrot,
    PoliceDog,
    Snake,
    Wolf,
)
from lib_core.events import EventBus, KeyValueChange, StringPayload, on
from lib_core.models import Email, User, UserId, ok
from lib_core.shapes import (
    Circle,
    Point3D,
    Polygon,
    Rectangle,
    RegularPolygon,
    Square,
    Star,
    Triangle,
)
from lib_core.storage import InMemoryTypedStorage, TransactionalStorage
from lib_utils.geometry import (
    Matrix2x3,
    bounding_box,
    convex_hull,
    largest,
    total_area,
    total_extent,
)
from lib_utils.logger import Level, LoggerFactory, logger_scope

from app_cli.commands import (
    AddShapePayload,
    Context,
    PersistPayload,
    Replay,
    ReplayPayload,
    TransformPayload,
    make_default_bus,
)
from app_cli.pipeline import make_demo_pipeline


def main() -> None:
    """Entry point. Builds shapes, animals, runs commands, prints output."""
    factory = LoggerFactory(default_level=Level.INFO)
    log = factory.get("cli")

    log.info("starting CLI demo")
    shapes = [
        Circle(1.0),
        Rectangle(2.0, 3.0),
        Square(4.0),
        Triangle(5.0, 6.0),
        RegularPolygon(6, 2.0),
        Star(5, 3.0, 1.5),
    ]
    log.info(f"shape count = {len(shapes)}")
    log.info(f"total area  = {total_area(shapes):.4f}")
    log.info(f"largest     = {type(largest(shapes)).__name__}")

    # --- Intentional bug ---------------------------------------------------
    # `total_area` expects a list; passing a str triggers PY6003 statically AND
    # raises AttributeError at runtime (str has no .area()) — the demo catches it
    # so the rest of the script still runs. `largest` gets the same wrong-element
    # list to exercise the runtime path too.
    try:
        total_area("not a list")  # noqa: PY6003 — intentional (str where list required)
        largest([1, 2, 3, 4, 5])  # runtime AttributeError on int.area()
    except AttributeError as exc:
        log.warn(f"intentional bug surfaced: {exc}")

    # --- os/os.path imports — exercise the catalog linker -----------------
    print("path join =", os.path.join("a", "b"))

    # --- Bounding box / convex hull demo ----------------------------------
    bb = total_extent(shapes)
    log.info(f"total bounding box: {bb.lo_x:.2f},{bb.lo_y:.2f} -> {bb.hi_x:.2f},{bb.hi_y:.2f}")
    triangle = Polygon([Point3D(0.0, 0.0), Point3D(4.0, 0.0), Point3D(2.0, 3.0)])
    log.info(f"triangle bbox: {bounding_box(triangle)}")
    hull = convex_hull(triangle.vertices)
    log.info(f"convex hull has {len(hull)} vertices")

    # --- Animals: build a habitat and run a small chorus ------------------
    log.info("populating habitats")
    pack = Habitat[Wolf]("North Ridge", capacity=4)
    pack.admit(Wolf("Akela"))
    pack.admit(Wolf("Raksha", pack="North Ridge"))
    aviary = Habitat[Eagle]("Cliff Hide", capacity=2)
    aviary.admit(Eagle("Tarkus"))
    log.info(f"pack size = {len(pack)}, aviary size = {len(aviary)}")
    for animal in (Cat("Whiskers"), Dog("Rex"), PoliceDog("K9", badge=42), Snake("Kaa")):
        log.info(f"  {animal.name} says {animal.speak()}")
    parrot = Parrot("Polly", owner="Alice")
    parrot.teach_word("hello")
    parrot.teach_word("goodbye")
    log.info(f"  Parrot {parrot.name} now says {parrot.speak()}")
    guide = GuideDog("Buddy", owner="Sam", handler="Sam")
    guide.learn("forward")
    guide.learn("stop")
    log.info(f"  GuideDog {guide.name} knows {guide.commands}")

    # --- Storage: typed + transactional -----------------------------------
    log.info("running typed-storage demo")
    storage = InMemoryTypedStorage[str, int]()
    storage.set("alpha", 1)
    storage.set("beta", 2)
    txn = TransactionalStorage(storage)
    try:
        with txn.transaction():
            txn.set("gamma", 3)
            txn.set("alpha", 100)
            raise RuntimeError("simulated failure to test rollback")
    except RuntimeError:
        log.info("  rollback executed; alpha should be 1 again")
    log.info(f"  alpha = {storage.get('alpha')}")
    log.info(f"  has gamma = {'gamma' in storage}")

    # --- Models: validated User -------------------------------------------
    log.info("constructing user")
    user = User(UserId(42), Email("alice@example.com"), tags=["admin"])
    user.tag("editor")
    log.info(f"  user = {user.display_name} ({user.email.address}), tags={user.tags}")
    result = ok(user)
    log.info(f"  result.is_ok() = {result.is_ok()}")

    # --- Commands ---------------------------------------------------------
    log.info("running command bus")
    bus = make_default_bus()
    ctx = Context()
    bus.execute("add_shape", AddShapePayload(triangle), ctx)
    bus.execute("add_shape", AddShapePayload(Square(5.0), layer="background"), ctx)
    bus.execute(
        "transform",
        TransformPayload(0, Matrix2x3.translation(10.0, 0.0)),
        ctx,
    )
    bus.execute("persist", PersistPayload("/tmp/canvas.json", overwrite=True), ctx)
    log.info(f"  history depth = {len(ctx.history)}")
    log.info(f"  layers = {sorted(ctx.canvas)}")

    # --- Replay -----------------------------------------------------------
    log.info("running replay")
    replay = Replay(ReplayPayload(commands=list(ctx.history)))
    replay_ctx = Context()
    replay_result = replay.execute(replay_ctx)
    log.info(f"  replay ok = {replay_result.ok}, executed = {replay_result.value}")

    # --- Pipeline ---------------------------------------------------------
    log.info("running data pipeline")
    pipeline = make_demo_pipeline()
    pipeline_result = pipeline.run([1, -2, 3, 4, -5, 6, 7, 8, 9, 10, 11, 12])
    log.info(f"  pipeline result = {pipeline_result} ({pipeline.describe()})")

    # --- Event bus --------------------------------------------------------
    log.info("running event-bus demo")
    bus2 = EventBus()

    class Listener:
        """Sample listener subscribed via the @on decorator."""

        def __init__(self) -> None:
            self.seen: list[str] = []

        @on(StringPayload)
        def on_string(self, ev: StringPayload) -> None:
            self.seen.append(ev.text)

        @on(KeyValueChange)
        def on_change(self, ev: KeyValueChange) -> None:
            self.seen.append(f"{ev.key} -> {ev.new_value}")

    listener = Listener()
    attached = bus2.attach_handlers(listener)
    log.info(f"  attached {attached} handlers")
    bus2.dispatch(StringPayload(text="hello", source="cli"))
    bus2.dispatch(KeyValueChange(key="alpha", new_value=100, source="cli"))
    log.info(f"  listener saw: {listener.seen}")

    # --- Final summary with a brief debug-level scope ---------------------
    with logger_scope(factory, "cli", level=Level.DEBUG) as scoped:
        scoped.debug(f"finished — {len(ctx.history)} commands, {bus2.subscription_count} subs")

    # --- Backwards-compatible interactive bits (kept short) ---------------
    # name = input("What is your name? ")  # disabled so Run completes non-interactively
    # print("Hello,", name)
    for i in range(3):
        log.info(f"countdown {3 - i}")
    numbers = [2, 4, 6, 8]
    total = sum(numbers)
    log.info(f"total = {total}")


if __name__ == "__main__":
    main()
    
    
    
    
    
