"""Generic value types and domain models for lib_core.

This module is the IDE's stress-test for everything dataclass-,
NamedTuple-, and TypeVar-related: frozen dataclasses, slotted
dataclasses, ``__post_init__`` validation, generic boxes, sum types,
and tagged unions.

Convention: anything that's a pure value (no identity, comparable by
content) lives here as a ``frozen=True`` dataclass or NamedTuple.
Anything that's mutable application state lives in the package that
owns it.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Generic, NamedTuple, Protocol, TypeVar

from .errors import FieldValidationError, CrossFieldValidationError


T = TypeVar("T")
A = TypeVar("A")
B = TypeVar("B")
E = TypeVar("E")


# -- Protocol -----------------------------------------------------------


class Taggable(Protocol):
    """Anything that can be labeled with free-form string tags.

    ``User`` satisfies this structurally via its ``tags`` list and
    ``tag()`` method — no explicit inheritance needed.
    """

    tags: list[str]

    def tag(self, label: str) -> None:
        ...


# -- Generic value containers -----------------------------------------------


@dataclass(frozen=True, slots=True)
class Boxy(Generic[T]):
    """Single-value container — useful as a hashable wrapper around a T.

    The wrapping defeats Python's "no, you can't put a set into a dict
    key" complaint when the inner T happens to be unhashable: ``Box``
    itself hashes by identity of the wrapper, not the contents.

    :param value: The wrapped value. Read-only after construction.
    """

    value: T

    def map(self, fn: Callable[[T], A]) -> Boxy[A]:
        """Apply ``fn`` to the inner value, returning a new Box."""
        return Boxy(fn(self.value))

    def unwrap(self) -> T:
        """Return the inner value."""
        return self.value


@dataclass(frozen=True, slots=True)
class Pair(Generic[A, B]):
    """Heterogeneous two-tuple with named fields.

    Unlike ``tuple[A, B]``, this exposes attribute access (``p.first``)
    which makes call sites self-documenting without losing the type.
    """

    first: A
    second: B

    def swapped(self) -> Pair[B, A]:
        """Return a new pair with first and second swapped."""
        return Pair(self.second, self.first)

    def to_tuple(self) -> tuple[A, B]:
        """Return the same data as a plain tuple."""
        return (self.first, self.second)


# -- Coordinate / range NamedTuples -----------------------------------------


class Coordinate(NamedTuple):
    """2-D coordinate; uses NamedTuple so it integrates with numpy/etc.

    NamedTuples preserve tuple semantics (iteration, unpacking) while
    giving each slot a name. Useful when downstream APIs expect tuples.
    """

    x: float
    y: float

    def shifted(self, dx: float, dy: float) -> Coordinate:
        """Return a new coordinate offset by ``(dx, dy)``."""
        return Coordinate(self.x + dx, self.y + dy)

    @property
    def magnitude(self) -> float:
        """Distance from origin."""
        return (self.x * self.x + self.y * self.y) ** 0.5


class Coordinate3D(NamedTuple):
    """3-D coordinate."""

    x: float
    y: float
    z: float

    def shifted(self, dx: float, dy: float, dz: float) -> Coordinate3D:
        """Return a new coordinate offset by ``(dx, dy, dz)``."""
        return Coordinate3D(self.x + dx, self.y + dy, self.z + dz)

    @property
    def magnitude(self) -> float:
        """Distance from origin in 3-D."""
        return (self.x ** 2 + self.y ** 2 + self.z ** 2) ** 0.5


class Range(NamedTuple):
    """Closed numeric range ``[lo, hi]``."""

    lo: float
    hi: float

    @property
    def width(self) -> float:
        """``hi - lo``."""
        return self.hi - self.lo

    def contains(self, value: float) -> bool:
        """Inclusive containment check."""
        return self.lo <= value <= self.hi

    def overlaps(self, other: Range) -> bool:
        """Open-set overlap predicate."""
        return self.lo <= other.hi and other.lo <= self.hi


# -- Domain models with __post_init__ validation ----------------------------


@dataclass(frozen=True)
class UserId:
    """Strongly-typed user identifier — defeats accidental swap with bare int."""

    value: int

    def __post_init__(self) -> None:
        if self.value <= 0:
            raise FieldValidationError("value", f"must be positive, got {self.value}")


@dataclass(frozen=True)
class Email:
    """Validated email address. Intentionally minimal — real world would
    use a proper validator, but for stress-testing we just check the @."""

    address: str

    def __post_init__(self) -> None:
        if "@" not in self.address:
            raise FieldValidationError("address", "missing @")
        if self.address.startswith("@") or self.address.endswith("@"):
            raise FieldValidationError("address", "@ at boundary")

    @property
    def domain(self) -> str:
        """Everything after the @."""
        return self.address.split("@", 1)[1]

    @property
    def local_part(self) -> str:
        """Everything before the @."""
        return self.address.split("@", 1)[0]


@dataclass
class User:
    """Mutable user record — id is immutable, profile fields can change.

    Demonstrates ``__post_init__`` cross-field validation: ``display_name``
    defaults to the email's local part if not provided.
    """

    user_id: UserId
    email: Email
    display_name: str = ""
    is_active: bool = True
    tags: list[str] = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.display_name:
            object.__setattr__(self, "display_name", self.email.local_part)
        if self.is_active and not self.email.address:
            raise CrossFieldValidationError(
                ["is_active", "email"],
                "active users need a valid email",
            )

    def deactivate(self) -> None:
        """Mark the user inactive. Idempotent."""
        self.is_active = False

    def tag(self, label: str) -> None:
        """Add ``label`` to ``tags`` if not already present."""
        if label not in self.tags:
            self.tags.append(label)


# -- Result / Either sum type -----------------------------------------------


@dataclass(frozen=True, slots=True)
class Ok(Generic[T]):
    """Success branch of a ``Result``."""

    value: T

    def is_ok(self) -> bool:
        """Always ``True``."""
        return True

    def is_err(self) -> bool:
        """Always ``False``."""
        return False

    def unwrap(self) -> T:
        """Return the wrapped value."""
        return self.value

    def unwrap_or(self, default: T) -> T:
        """Return the wrapped value (default unused)."""
        return self.value

    def map(self, fn: Callable[[T], A]) -> Ok[A]:
        """Apply ``fn`` to the wrapped value."""
        return Ok(fn(self.value))


@dataclass(frozen=True, slots=True)
class Err(Generic[E]):
    """Error branch of a ``Result``."""

    error: E

    def is_ok(self) -> bool:
        """Always ``False``."""
        return False

    def is_err(self) -> bool:
        """Always ``True``."""
        return True

    def unwrap(self) -> Any:
        """Re-raise the wrapped error if it's an exception, else raise HelixError."""
        if isinstance(self.error, BaseException):
            raise self.error
        raise RuntimeError(f"unwrap on Err({self.error!r})")

    def unwrap_or(self, default: T) -> T:
        """Return ``default`` (the error is discarded)."""
        return default

    def map(self, fn: Callable[[Any], A]) -> Err[E]:
        """Identity — Err.map ignores the function (it can't run on an error)."""
        return self


# A two-armed sum type. A real Rust-style Result would lean on
# pattern-matching exhaustiveness; in Python 3.12 we get most of that
# via match/case at call sites.
Result = Ok[T] | Err[E]


# -- Convenience helpers -----------------------------------------------------


def ok(value: T) -> Ok[T]:
    """Construct an Ok result. Sugar over ``Ok(value)``."""
    return Ok(value)


def err(error: E) -> Err[E]:
    """Construct an Err result. Sugar over ``Err(error)``."""
    return Err(error)


def first_ok(results: list[Result[T, E]]) -> Result[T, list[E]]:
    """Return the first Ok in the list, or an Err with all collected errors.

    Useful for "try several alternatives, take whichever wins" workflows.
    """
    errors: list[E] = []
    for r in results:
        if r.is_ok():
            return r  # type: ignore[return-value]
        errors.append(r.error)  # type: ignore[union-attr]
    return Err(errors)


def all_ok(results: list[Result[T, E]]) -> Result[list[T], E]:
    """Return Ok([all values]) if every result is Ok, else the first Err."""
    values: list[T] = []
    for r in results:
        if r.is_err():
            return r  # type: ignore[return-value]
        values.append(r.value)  # type: ignore[union-attr]
    return Ok(values)

