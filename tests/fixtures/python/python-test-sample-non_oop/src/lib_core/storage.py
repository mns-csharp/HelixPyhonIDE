"""Storage Protocol with synchronous + async variants and several backends.

The original ``Storage`` Protocol and ``InMemoryStorage`` stay so the
existing tests pass; we additionally wire up:

  - Generic ``Storage[K, V]`` for type-safe stores.
  - ``AsyncStorage`` Protocol.
  - ``FileStorage`` — JSON-backed; works as a context manager so the
    file handle is owned by the ``with`` block.
  - ``ChainedStorage`` — read-through chain of fallback stores.
  - ``TransactionalStorage`` — wraps any Storage in an undo log via a
    ``@contextmanager`` ``transaction()``.

The expanded Protocol is intentionally backwards-compatible with the
old one (same method names, same contract).
"""
from __future__ import annotations

import json
from contextlib import contextmanager
from pathlib import Path
from typing import (
    Any,
    AsyncIterator,
    Generic,
    Iterator,
    Protocol,
    TypeVar,
    runtime_checkable,
)

from .errors import (
    CapacityError,
    IntegrityError,
    KeyMissingError,
    StorageError,
    TypeMismatchError,
)


K = TypeVar("K")
V = TypeVar("V")


# -- Protocols --------------------------------------------------------------


class Storage(Protocol):
    """Simple key-value store protocol (kept compatible with original)."""

    def get(self, key: str) -> object: ...
    def set(self, key: str, value: object) -> None: ...
    def delete(self, key: str) -> None: ...


@runtime_checkable
class TypedStorage(Protocol, Generic[K, V]):
    """Strongly-typed version of Storage parameterized by K and V.

    ``runtime_checkable`` is on so callers can ``isinstance(s, TypedStorage)``
    to gate strongly-typed code paths.
    """

    def get(self, key: K) -> V: ...
    def set(self, key: K, value: V) -> None: ...
    def delete(self, key: K) -> None: ...
    def keys(self) -> Iterator[K]: ...
    def __contains__(self, key: object) -> bool: ...
    def __len__(self) -> int: ...


class AsyncStorage(Protocol, Generic[K, V]):
    """Async variant — every method is a coroutine."""

    async def get(self, key: K) -> V: ...
    async def set(self, key: K, value: V) -> None: ...
    async def delete(self, key: K) -> None: ...
    def keys(self) -> AsyncIterator[K]: ...


# -- Concrete implementations ----------------------------------------------


class InMemoryStorage:
    """Dict-backed Storage — kept for backwards compatibility.

    For new code prefer ``InMemoryTypedStorage[K, V]`` which carries the
    element types through the IDE's type checker.
    """

    def __init__(self) -> None:
        self._data: dict[str, object] = {}

    def get(self, key: str) -> object:
        return self._data.get(key)

    def set(self, key: str, value: object) -> None:
        self._data[key] = value

    def delete(self, key: str) -> None:
        if key in self._data:
            del self._data[key]


class InMemoryTypedStorage(Generic[K, V]):
    """Generic in-memory store. Reports missing keys via :class:`KeyMissingError`.

    :param capacity: Optional soft cap; raises :class:`CapacityError` on
        exceeding it. ``None`` means no cap.
    :param value_type: Optional runtime type check for values; raises
        :class:`TypeMismatchError` on mismatch.
    """

    def __init__(
        self,
        *,
        capacity: int | None = None,
        value_type: type | None = None,
    ) -> None:
        self._data: dict[K, V] = {}
        self._capacity = capacity
        self._value_type = value_type

    def get(self, key: K) -> V:
        try:
            return self._data[key]
        except KeyError as exc:
            raise KeyMissingError(key) from exc

    def set(self, key: K, value: V) -> None:
        if self._value_type is not None and not isinstance(value, self._value_type):
            raise TypeMismatchError(self._value_type, type(value))
        if (
            self._capacity is not None
            and key not in self._data
            and len(self._data) >= self._capacity
        ):
            raise CapacityError(self._capacity, len(self._data))
        self._data[key] = value

    def delete(self, key: K) -> None:
        if key in self._data:
            del self._data[key]

    def keys(self) -> Iterator[K]:
        """Iterator over current keys (snapshot)."""
        return iter(list(self._data))

    def items(self) -> Iterator[tuple[K, V]]:
        """Iterator over (key, value) pairs (snapshot)."""
        return iter(list(self._data.items()))

    def __contains__(self, key: object) -> bool:
        return key in self._data

    def __len__(self) -> int:
        return len(self._data)


class FileStorage:
    """JSON-backed string-keyed store. Works as a context manager.

    Usage::

        with FileStorage(Path("data.json")) as store:
            store.set("key", "value")
        # on exit the file is flushed.

    Outside a ``with`` block, ``set`` writes-through immediately so the
    store is also usable as a long-lived object.
    """

    def __init__(self, path: Path) -> None:
        self.path = path
        self._data: dict[str, Any] = {}
        self._dirty = False
        self._in_context = False
        self._load_existing()

    def _load_existing(self) -> None:
        if self.path.exists():
            try:
                with self.path.open("r", encoding="utf-8") as f:
                    self._data = json.load(f)
            except (OSError, json.JSONDecodeError) as exc:
                raise IntegrityError(
                    f"could not read {self.path}",
                    context={"path": str(self.path)},
                ) from exc

    def __enter__(self) -> "FileStorage":
        self._in_context = True
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self._in_context = False
        # Always flush on exit, even if an exception happened — the
        # caller can choose to delete the file if they want a rollback.
        self.flush()

    def get(self, key: str) -> Any:
        if key not in self._data:
            raise KeyMissingError(key)
        return self._data[key]

    def set(self, key: str, value: Any) -> None:
        # Validate JSON-serializable.
        try:
            json.dumps(value)
        except (TypeError, ValueError) as exc:
            raise TypeMismatchError(str, type(value)) from exc
        self._data[key] = value
        self._dirty = True
        if not self._in_context:
            self.flush()

    def delete(self, key: str) -> None:
        if key in self._data:
            del self._data[key]
            self._dirty = True
            if not self._in_context:
                self.flush()

    def flush(self) -> None:
        """Write pending changes to disk."""
        if not self._dirty:
            return
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.open("w", encoding="utf-8") as f:
                json.dump(self._data, f, indent=2)
            self._dirty = False
        except OSError as exc:
            raise StorageError(
                f"flush failed for {self.path}",
                context={"path": str(self.path)},
            ) from exc

    def keys(self) -> Iterator[str]:
        """Snapshot iterator over keys."""
        return iter(list(self._data))


class ChainedStorage(Generic[K, V]):
    """Layered store — reads cascade through ``layers`` until one hits.

    Writes go to the first layer only. Useful for "fast cache in front
    of slow durable store" patterns.
    """

    def __init__(self, layers: list[InMemoryTypedStorage[K, V]]) -> None:
        if not layers:
            raise ValueError("ChainedStorage needs at least one layer")
        self.layers = list(layers)

    def get(self, key: K) -> V:
        last_exc: KeyMissingError | None = None
        for layer in self.layers:
            try:
                return layer.get(key)
            except KeyMissingError as exc:
                last_exc = exc
                continue
        # All layers missed.
        assert last_exc is not None
        raise last_exc

    def set(self, key: K, value: V) -> None:
        self.layers[0].set(key, value)

    def delete(self, key: K) -> None:
        # Delete from every layer so a stale lower layer can't shadow.
        for layer in self.layers:
            layer.delete(key)

    def __contains__(self, key: object) -> bool:
        return any(key in layer for layer in self.layers)


class TransactionalStorage(Generic[K, V]):
    """Wraps any ``InMemoryTypedStorage`` with a transactional undo log.

    Inside a ``transaction()`` block, mutations are tracked. If the
    block raises, every mutation made within the block is reverted.
    """

    def __init__(self, inner: InMemoryTypedStorage[K, V]) -> None:
        self._inner = inner
        # When _undo is None we're outside a transaction — mutations
        # just write through.
        self._undo: list[tuple[str, K, V | None]] | None = None

    def get(self, key: K) -> V:
        return self._inner.get(key)

    def set(self, key: K, value: V) -> None:
        if self._undo is not None:
            prev: V | None
            try:
                prev = self._inner.get(key)
            except KeyMissingError:
                prev = None
            self._undo.append(("set", key, prev))
        self._inner.set(key, value)

    def delete(self, key: K) -> None:
        if self._undo is not None:
            try:
                prev = self._inner.get(key)
            except KeyMissingError:
                return  # nothing to undo if it wasn't there
            self._undo.append(("delete", key, prev))
        self._inner.delete(key)

    @contextmanager
    def transaction(self) -> Iterator[None]:
        """Open a transaction. On exception, rolls back; on clean exit, commits."""
        if self._undo is not None:
            raise IntegrityError("nested transactions are not supported")
        self._undo = []
        try:
            yield
        except BaseException:
            # Roll back in reverse order.
            for action, key, prev in reversed(self._undo):
                if action == "set":
                    if prev is None:
                        self._inner.delete(key)
                    else:
                        self._inner.set(key, prev)
                elif action == "delete":
                    if prev is not None:
                        self._inner.set(key, prev)
            raise
        finally:
            self._undo = None
