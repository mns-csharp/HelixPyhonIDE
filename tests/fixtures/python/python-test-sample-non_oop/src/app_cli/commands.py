"""Command pattern for the CLI: typed command payloads + dispatcher.

Every concrete command subclasses :class:`Command`. Payloads live in
``frozen=True`` dataclasses so the IDE has lots of distinct named
fields to highlight, and the dispatcher walks the registry by command
name (string) so the user can see import-resolution + dict-key
diagnostics in action.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, Callable, Generic, Protocol, TypeVar

from lib_core.errors import ConfigurationError, HelixError
from lib_core.shapes import Point3D, Polygon, Shape
from lib_utils.geometry import Matrix2x3, transform_polygon


T = TypeVar("T")


# -- Protocol ---------------------------------------------------------------


class CommandFactory(Protocol):
    """Anything callable with a payload that produces a Command.

    ``CommandBus._factories`` stores plain lambdas today, but any object
    with a matching ``__call__`` (e.g. a class with extra state) would
    satisfy this Protocol too.
    """

    def __call__(self, payload: Any) -> "Command":
        ...


# -- Payload types ---------------------------------------------------------


@dataclass(frozen=True, slots=True)
class CommandResult(Generic[T]):
    """Normalised result envelope. Keeps success/error info adjacent."""

    ok: bool
    value: T | None = None
    error: HelixError | None = None
    duration_ms: float = 0.0


@dataclass(frozen=True, slots=True)
class AddShapePayload:
    """Add a single shape to the canvas."""

    shape: Shape
    layer: str = "default"


@dataclass(frozen=True, slots=True)
class RemoveShapePayload:
    """Remove a shape by its index in the active layer."""

    index: int
    layer: str = "default"


@dataclass(frozen=True, slots=True)
class TransformPayload:
    """Apply a 2-D matrix transform to a polygon at ``index``."""

    index: int
    matrix: Matrix2x3
    layer: str = "default"


@dataclass(frozen=True, slots=True)
class PersistPayload:
    """Persist the current canvas to a file path."""

    path: str
    overwrite: bool = False


@dataclass(frozen=True, slots=True)
class ReplayPayload:
    """Replay a sequence of recorded commands."""

    commands: list["Command"]


# -- Command ABC -----------------------------------------------------------


class Command(ABC):
    """Base class for every command.

    Subclasses provide a ``name`` class-level identifier (the key the
    dispatcher uses to look them up) and an ``execute`` method that
    receives a context object and returns a :class:`CommandResult`.
    """

    name: str = ""
    priority: int = 0

    @abstractmethod
    def execute(self, ctx: "Context") -> CommandResult[Any]:
        """Run the command against ``ctx``. Implementations should not raise —
        they should wrap failures in ``CommandResult(ok=False, error=...)``."""
        ...


@dataclass
class Context:
    """Mutable state passed through the command chain.

    The ``canvas`` maps layer-name → list-of-shapes; commands operate
    against named layers so undo/replay can target a specific layer
    without fighting the others.
    """

    canvas: dict[str, list[Shape]] = field(default_factory=dict)
    history: list[Command] = field(default_factory=list)
    metadata: dict[str, Any] = field(default_factory=dict)

    def get_layer(self, name: str) -> list[Shape]:
        return self.canvas.setdefault(name, [])


# -- Concrete commands -----------------------------------------------------


class AddShape(Command):
    """Add a shape to a layer."""

    name = "add_shape"
    priority = 10

    def __init__(self, payload: AddShapePayload) -> None:
        self.payload = payload

    def execute(self, ctx: Context) -> CommandResult[int]:
        layer = ctx.get_layer(self.payload.layer)
        layer.append(self.payload.shape)
        ctx.history.append(self)
        return CommandResult(ok=True, value=len(layer) - 1)


class RemoveShape(Command):
    """Remove a shape by index."""

    name = "remove_shape"
    priority = 20

    def __init__(self, payload: RemoveShapePayload) -> None:
        self.payload = payload

    def execute(self, ctx: Context) -> CommandResult[Shape]:
        layer = ctx.get_layer(self.payload.layer)
        if not (0 <= self.payload.index < len(layer)):
            return CommandResult(
                ok=False,
                error=HelixError(
                    f"index {self.payload.index} out of range for layer "
                    f"{self.payload.layer!r} (size {len(layer)})",
                    error_code="INDEX_OUT_OF_RANGE",
                ),
            )
        removed = layer.pop(self.payload.index)
        ctx.history.append(self)
        return CommandResult(ok=True, value=removed)


class Transform(Command):
    """Apply a 2-D matrix transform to a polygon at ``index``."""

    name = "transform"
    priority = 30

    def __init__(self, payload: TransformPayload) -> None:
        self.payload = payload

    def execute(self, ctx: Context) -> CommandResult[Polygon]:
        layer = ctx.get_layer(self.payload.layer)
        if not (0 <= self.payload.index < len(layer)):
            return CommandResult(
                ok=False,
                error=HelixError("index out of range", error_code="INDEX_OUT_OF_RANGE"),
            )
        shape = layer[self.payload.index]
        if not isinstance(shape, Polygon):
            return CommandResult(
                ok=False,
                error=HelixError(
                    f"transform requires a Polygon, got {type(shape).__name__}",
                    error_code="TRANSFORM_TYPE",
                ),
            )
        new_shape = transform_polygon(shape, self.payload.matrix)
        layer[self.payload.index] = new_shape
        ctx.history.append(self)
        return CommandResult(ok=True, value=new_shape)


class Persist(Command):
    """Persist the canvas — stub implementation that just records intent."""

    name = "persist"
    priority = 90

    def __init__(self, payload: PersistPayload) -> None:
        self.payload = payload

    def execute(self, ctx: Context) -> CommandResult[str]:
        ctx.metadata["persist_path"] = self.payload.path
        ctx.metadata["persist_overwrite"] = self.payload.overwrite
        ctx.history.append(self)
        return CommandResult(ok=True, value=self.payload.path)


class Replay(Command):
    """Replay a recorded sequence of commands."""

    name = "replay"
    priority = 100

    def __init__(self, payload: ReplayPayload) -> None:
        self.payload = payload

    def execute(self, ctx: Context) -> CommandResult[int]:
        executed = 0
        for cmd in self.payload.commands:
            result = cmd.execute(ctx)
            if not result.ok:
                return CommandResult(ok=False, error=result.error, value=executed)
            executed += 1
        return CommandResult(ok=True, value=executed)


# -- Command bus / dispatcher ----------------------------------------------


class CommandBus:
    """Dispatcher that routes commands by ``Command.name``.

    A bus knows how to construct a command from a name + payload via
    a registered factory, and can execute the resulting command against
    a shared context.
    """

    def __init__(self) -> None:
        self._factories: dict[str, Callable[[Any], Command]] = {}

    def register(self, name: str, factory: Callable[[Any], Command]) -> None:
        """Register ``factory`` under ``name``."""
        if name in self._factories:
            raise ConfigurationError(f"command name {name!r} already registered")
        self._factories[name] = factory

    def register_default(self) -> None:
        """Register the built-in commands shipped in this module."""
        self.register("add_shape", lambda p: AddShape(p))
        self.register("remove_shape", lambda p: RemoveShape(p))
        self.register("transform", lambda p: Transform(p))
        self.register("persist", lambda p: Persist(p))
        self.register("replay", lambda p: Replay(p))

    def execute(self, name: str, payload: Any, ctx: Context) -> CommandResult[Any]:
        """Look up the factory for ``name``, construct, execute."""
        factory = self._factories.get(name)
        if factory is None:
            return CommandResult(
                ok=False,
                error=ConfigurationError(f"unknown command {name!r}"),
            )
        cmd = factory(payload)
        return cmd.execute(ctx)

    @property
    def known_names(self) -> list[str]:
        """Sorted list of registered command names."""
        return sorted(self._factories)


def make_default_bus() -> CommandBus:
    """Convenience: a bus with every built-in command registered."""
    bus = CommandBus()
    bus.register_default()
    return bus


# -- Demo helper -----------------------------------------------------------


def demo_pipeline() -> Context:
    """Run a small command sequence and return the resulting context.

    Used by the CLI demo to show end-to-end wiring; also a useful smoke
    test that everything imports cleanly.
    """
    bus = make_default_bus()
    ctx = Context()
    triangle = Polygon([Point3D(0.0, 0.0), Point3D(4.0, 0.0), Point3D(2.0, 3.0)])
    bus.execute("add_shape", AddShapePayload(triangle), ctx)
    bus.execute(
        "transform",
        TransformPayload(0, Matrix2x3.translation(10.0, 0.0)),
        ctx,
    )
    bus.execute("persist", PersistPayload("/tmp/canvas.json", overwrite=True), ctx)
    return ctx
