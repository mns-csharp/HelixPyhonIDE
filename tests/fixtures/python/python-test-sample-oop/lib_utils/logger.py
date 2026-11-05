"""Logging utilities: original Logger, plus Formatter Protocol, factory, async.

Original public API (``Logger`` with ``info``/``warn``/``error``/
``from_config``) is unchanged so existing tests keep passing.
Additions stress the IDE on Protocols, context managers, and async.
"""
from __future__ import annotations

import asyncio
import json
import sys
import time
from contextlib import asynccontextmanager, contextmanager
from dataclasses import dataclass
from enum import Enum
from typing import Any, AsyncIterator, Iterator, Protocol, TextIO


# -- Original Logger (kept verbatim, modulo formatting) --------------------


class Logger:
    """A tiny prefix-based console logger."""

    def __init__(self, prefix: str) -> None:
        self._prefix = prefix

    def info(self, msg: str) -> None:
        print(self._prefix + " INFO: " + msg)

    def warn(self, msg: str) -> None:
        print(self._prefix + " WARN: " + msg)

    def error(self, msg: str) -> None:
        print(self._prefix + " ERROR: " + msg)

    @classmethod
    def from_config(cls, prefix: str) -> Logger:
        """Alternative constructor — exercises @classmethod classification."""
        return cls(prefix)

    def __repr__(self) -> str:
        return "Logger(prefix=" + repr(self._prefix) + ")"


# -- Levels -----------------------------------------------------------------


class Level(Enum):
    """Severity levels in ascending order."""

    DEBUG = 10
    INFO = 20
    WARN = 30
    ERROR = 40
    CRITICAL = 50


# -- Records ----------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class LogRecord:
    """A single log entry. Frozen so it can be passed across threads safely."""

    level: Level
    message: str
    timestamp: float
    prefix: str = ""
    extra: dict[str, Any] | None = None


# -- Formatter Protocol ----------------------------------------------------


class Formatter(Protocol):
    """Anything that turns a LogRecord into a serialized line."""

    def format(self, record: LogRecord) -> str: ...


class TextFormatter:
    """Plaintext ``[prefix] LEVEL message`` formatter."""

    def format(self, record: LogRecord) -> str:
        ts = time.strftime("%H:%M:%S", time.localtime(record.timestamp))
        prefix = f"[{record.prefix}] " if record.prefix else ""
        return f"{prefix}{ts} {record.level.name}: {record.message}"


class JSONFormatter:
    """JSON line formatter — one record per line, machine-parseable."""

    def __init__(self, *, ensure_ascii: bool = False) -> None:
        self.ensure_ascii = ensure_ascii

    def format(self, record: LogRecord) -> str:
        payload: dict[str, Any] = {
            "ts": record.timestamp,
            "level": record.level.name,
            "msg": record.message,
        }
        if record.prefix:
            payload["prefix"] = record.prefix
        if record.extra:
            payload["extra"] = record.extra
        return json.dumps(payload, ensure_ascii=self.ensure_ascii)


# -- StructuredLogger (new) ------------------------------------------------


class StructuredLogger:
    """Logger that pushes LogRecords through a Formatter to a stream.

    Intended for code that wants levels and a swappable output format.
    The original :class:`Logger` stays for the simple "print with prefix"
    use case.
    """

    def __init__(
        self,
        prefix: str = "",
        *,
        level: Level = Level.INFO,
        formatter: Formatter | None = None,
        stream: TextIO | None = None,
    ) -> None:
        self.prefix = prefix
        self.level = level
        self.formatter: Formatter = formatter or TextFormatter()
        self.stream: TextIO = stream or sys.stdout

    def log(self, level: Level, message: str, *, extra: dict[str, Any] | None = None) -> None:
        """Emit ``message`` at ``level`` if it meets the configured threshold."""
        if level.value < self.level.value:
            return
        record = LogRecord(
            level=level,
            message=message,
            timestamp=time.time(),
            prefix=self.prefix,
            extra=extra,
        )
        self.stream.write(self.formatter.format(record) + "\n")
        self.stream.flush()

    def debug(self, message: str, **extra: Any) -> None:
        self.log(Level.DEBUG, message, extra=extra or None)

    def info(self, message: str, **extra: Any) -> None:
        self.log(Level.INFO, message, extra=extra or None)

    def warn(self, message: str, **extra: Any) -> None:
        self.log(Level.WARN, message, extra=extra or None)

    def error(self, message: str, **extra: Any) -> None:
        self.log(Level.ERROR, message, extra=extra or None)

    def critical(self, message: str, **extra: Any) -> None:
        self.log(Level.CRITICAL, message, extra=extra or None)


# -- Factory + scope context manager ---------------------------------------


class LoggerFactory:
    """Registry of named loggers — one per prefix.

    Subsequent ``get`` calls for the same prefix return the same instance,
    so configuration tweaks propagate to existing call sites without
    threading the logger through arguments.
    """

    def __init__(self, *, default_level: Level = Level.INFO) -> None:
        self._registry: dict[str, StructuredLogger] = {}
        self._default_level = default_level

    def get(self, prefix: str) -> StructuredLogger:
        """Return the logger for ``prefix``; create one if missing."""
        existing = self._registry.get(prefix)
        if existing is not None:
            return existing
        logger = StructuredLogger(prefix=prefix, level=self._default_level)
        self._registry[prefix] = logger
        return logger

    def configure(self, *, level: Level | None = None, formatter: Formatter | None = None) -> None:
        """Reconfigure every registered logger atomically."""
        for logger in self._registry.values():
            if level is not None:
                logger.level = level
            if formatter is not None:
                logger.formatter = formatter

    @property
    def names(self) -> list[str]:
        """Sorted list of registered prefixes."""
        return sorted(self._registry)


@contextmanager
def logger_scope(factory: LoggerFactory, prefix: str, *, level: Level = Level.INFO) -> Iterator[StructuredLogger]:
    """Yield a logger temporarily reconfigured to ``level``.

    Usage::

        with logger_scope(factory, "boot", level=Level.DEBUG) as log:
            log.debug("starting up")
        # outside the scope the logger reverts to its prior level.
    """
    log = factory.get(prefix)
    saved = log.level
    log.level = level
    try:
        yield log
    finally:
        log.level = saved


# -- Async logger -----------------------------------------------------------


class AsyncLogger:
    """Append-only async logger backed by an ``asyncio.Queue``.

    Writes don't block the caller — they just enqueue. A long-running
    ``flush_loop`` task drains the queue and writes to the underlying
    stream. Useful in async services where logging on the request path
    can't block.
    """

    def __init__(
        self,
        prefix: str = "",
        *,
        formatter: Formatter | None = None,
        stream: TextIO | None = None,
        max_pending: int = 1000,
    ) -> None:
        self.prefix = prefix
        self.formatter: Formatter = formatter or TextFormatter()
        self.stream: TextIO = stream or sys.stdout
        self._queue: asyncio.Queue[LogRecord | None] = asyncio.Queue(maxsize=max_pending)
        self._task: asyncio.Task[None] | None = None

    async def start(self) -> None:
        """Begin draining the queue. Call once per logger lifetime."""
        if self._task is not None:
            return
        self._task = asyncio.create_task(self._drain())

    async def stop(self) -> None:
        """Stop the drain task. Pending records are flushed first."""
        if self._task is None:
            return
        await self._queue.put(None)  # sentinel
        await self._task
        self._task = None

    async def log(self, level: Level, message: str) -> None:
        """Enqueue a record — fast, non-blocking unless the queue is full."""
        record = LogRecord(level, message, time.time(), self.prefix)
        await self._queue.put(record)

    async def _drain(self) -> None:
        while True:
            record = await self._queue.get()
            if record is None:
                return
            self.stream.write(self.formatter.format(record) + "\n")
            self.stream.flush()


@asynccontextmanager
async def running_async_logger(
    prefix: str = "",
    *,
    formatter: Formatter | None = None,
) -> AsyncIterator[AsyncLogger]:
    """Context manager that starts/stops an :class:`AsyncLogger` cleanly."""
    log = AsyncLogger(prefix=prefix, formatter=formatter)
    await log.start()
    try:
        yield log
    finally:
        await log.stop()

