"""Custom exception hierarchy for the lib_core package.

Every exception subclasses :class:`HelixError`. Subclasses carry an
``error_code`` string so callers can dispatch on stable identifiers
without parsing free-form messages, and each preserves the standard
``__cause__`` chain via ``raise ... from ...``.

The hierarchy is intentionally deep so the IDE has interesting class
relationships to render in the type-explorer / call-hierarchy panels.

Layout::

    HelixError                          # base of everything
        StorageError                    # any storage operation failed
            KeyMissingError             # lookup failed
            TypeMismatchError           # value type didn't match schema
            IntegrityError              # invariants violated
            CapacityError               # over quota
        HandlerError                    # event-bus handler raised
        ValidationError                 # model validation failed
            FieldValidationError        # one specific field
            CrossFieldValidationError   # cross-field constraint
        ConfigurationError              # bad configuration
        RetryableError                  # transient — caller should retry

The leaves are deliberate: each provides extra fields useful at the
catch-site so handlers can build helpful user messages without parsing.
"""
from __future__ import annotations

from typing import Any


class HelixError(Exception):
    """Base of every custom exception raised inside the lib_core stack.

    :param message: Human-readable description of the failure.
    :param error_code: Stable, machine-readable identifier; callers
        should switch on this rather than the message text.
    :param context: Optional mapping of extra diagnostic data. Stored on
        the instance for later inspection; never logged automatically.
    """

    error_code: str = "HELIX_GENERIC"

    def __init__(
        self,
        message: str,
        *,
        error_code: str | None = None,
        context: dict[str, Any] | None = None,
    ) -> None:
        super().__init__(message)
        self.message = message
        if error_code is not None:
            self.error_code = error_code
        self.context: dict[str, Any] = context or {}

    def __str__(self) -> str:
        if self.context:
            details = ", ".join(f"{k}={v!r}" for k, v in self.context.items())
            return f"[{self.error_code}] {self.message} ({details})"
        return f"[{self.error_code}] {self.message}"


class StorageError(HelixError):
    """Anything went wrong inside a Storage backend."""

    error_code = "STORAGE_ERROR"


class KeyMissingError(StorageError):
    """The key being looked up does not exist in the store."""

    error_code = "STORAGE_KEY_MISSING"

    def __init__(self, key: Any) -> None:
        super().__init__(f"key not found: {key!r}", context={"key": key})
        self.key = key


class TypeMismatchError(StorageError):
    """The value provided didn't match the type the store expects."""

    error_code = "STORAGE_TYPE_MISMATCH"

    def __init__(self, expected: type, actual: type) -> None:
        super().__init__(
            f"expected {expected.__name__}, got {actual.__name__}",
            context={"expected": expected.__name__, "actual": actual.__name__},
        )
        self.expected = expected
        self.actual = actual


class IntegrityError(StorageError):
    """A storage invariant has been violated (duplicate key, foreign-key, etc.)."""

    error_code = "STORAGE_INTEGRITY"


class CapacityError(StorageError):
    """The storage backend is at capacity and cannot accept more entries."""

    error_code = "STORAGE_CAPACITY"

    def __init__(self, capacity: int, used: int) -> None:
        super().__init__(
            f"capacity {used}/{capacity} reached",
            context={"capacity": capacity, "used": used},
        )
        self.capacity = capacity
        self.used = used


class HandlerError(HelixError):
    """An EventBus handler raised during dispatch.

    The original handler exception is chained via ``__cause__`` and
    preserved as :attr:`original` for callers that want it without
    walking the chain manually.
    """

    error_code = "HANDLER_ERROR"

    def __init__(self, handler_name: str, original: BaseException) -> None:
        super().__init__(
            f"handler {handler_name!r} raised {type(original).__name__}",
            context={"handler": handler_name},
        )
        self.handler_name = handler_name
        self.original = original


class ValidationError(HelixError):
    """A model failed validation."""

    error_code = "VALIDATION"


class FieldValidationError(ValidationError):
    """One specific field failed validation."""

    error_code = "VALIDATION_FIELD"

    def __init__(self, field: str, reason: str) -> None:
        super().__init__(
            f"{field}: {reason}",
            context={"field": field, "reason": reason},
        )
        self.field = field
        self.reason = reason


class CrossFieldValidationError(ValidationError):
    """A constraint spanning multiple fields was violated."""

    error_code = "VALIDATION_CROSS_FIELD"

    def __init__(self, fields: list[str], reason: str) -> None:
        super().__init__(
            f"fields {fields}: {reason}",
            context={"fields": fields, "reason": reason},
        )
        self.fields = fields
        self.reason = reason


class ConfigurationError(HelixError):
    """The runtime configuration is bad — usually surfaces at startup."""

    error_code = "CONFIG"


class RetryableError(HelixError):
    """A transient error: the caller may retry without changing inputs.

    :param retry_after: Suggested seconds to wait before retrying. Zero
        means "retry immediately"; negative is treated as "don't retry".
    """

    error_code = "RETRYABLE"

    def __init__(self, message: str, *, retry_after: float = 0.0) -> None:
        super().__init__(message, context={"retry_after": retry_after})
        self.retry_after = retry_after

    @property
    def is_retryable(self) -> bool:
        """``True`` iff the caller should retry this failure."""
        return self.retry_after >= 0.0
