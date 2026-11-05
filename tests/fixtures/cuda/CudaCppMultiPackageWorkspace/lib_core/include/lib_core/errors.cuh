#pragma once

// Custom exception hierarchy for lib_core.
//
// Mirrors lib_core/errors.py. Every exception subclasses HelixError (which
// subclasses std::runtime_error so it works with plain `catch
// (std::exception&)` call sites too). Python's `context: dict[str, Any]`
// becomes `std::unordered_map<std::string, std::string>` -- every original
// use only ever stored primitives destined for display, so string values
// lose nothing observable. `raise ... from ...` (__cause__ chaining) has
// no direct std::exception equivalent; HandlerError instead stores the
// original std::exception_ptr directly (a strictly more useful analogue,
// since callers can std::rethrow_exception it themselves).
//
// The hierarchy is intentionally deep so the IDE has interesting class
// relationships to render in the type-explorer / call-hierarchy panels.
//
// Layout:
//
//   HelixError                          // base of everything
//       StorageError                    // any storage operation failed
//           KeyMissingError             // lookup failed
//           TypeMismatchError           // value type didn't match schema
//           IntegrityError              // invariants violated
//           CapacityError               // over quota
//       HandlerError                    // event-bus handler raised
//       ValidationError                 // model validation failed
//           FieldValidationError        // one specific field
//           CrossFieldValidationError   // cross-field constraint
//       ConfigurationError              // bad configuration
//       RetryableError                  // transient -- caller should retry

#include <exception>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

namespace lib_core::errors {

// Base of every custom exception raised inside the lib_core stack.
class HelixError : public std::runtime_error {
public:
    explicit HelixError(std::string message, std::string error_code = "HELIX_GENERIC",
                         std::unordered_map<std::string, std::string> context = {})
        : std::runtime_error(render(message, error_code, context)),
          message_(std::move(message)),
          error_code_(std::move(error_code)),
          context_(std::move(context)) {}

    const std::string& message() const { return message_; }
    const std::string& error_code() const { return error_code_; }
    const std::unordered_map<std::string, std::string>& context() const { return context_; }

private:
    static std::string render(const std::string& message, const std::string& error_code,
                               const std::unordered_map<std::string, std::string>& context) {
        std::string out = "[" + error_code + "] " + message;
        if (!context.empty()) {
            out += " (";
            bool first = true;
            for (const auto& [k, v] : context) {
                if (!first) out += ", ";
                out += k + "=" + v;
                first = false;
            }
            out += ")";
        }
        return out;
    }

    std::string message_;
    std::string error_code_;
    std::unordered_map<std::string, std::string> context_;
};

// Anything went wrong inside a Storage backend.
class StorageError : public HelixError {
public:
    explicit StorageError(std::string message, std::string error_code = "STORAGE_ERROR",
                           std::unordered_map<std::string, std::string> context = {})
        : HelixError(std::move(message), std::move(error_code), std::move(context)) {}
};

// The key being looked up does not exist in the store.
class KeyMissingError : public StorageError {
public:
    explicit KeyMissingError(std::string key)
        : StorageError("key not found: " + key, "STORAGE_KEY_MISSING", {{"key", key}}), key(std::move(key)) {}

    std::string key;
};

// The value provided didn't match the type the store expects.
class TypeMismatchError : public StorageError {
public:
    TypeMismatchError(std::string expected, std::string actual)
        : StorageError("expected " + expected + ", got " + actual, "STORAGE_TYPE_MISMATCH",
                        {{"expected", expected}, {"actual", actual}}),
          expected(std::move(expected)),
          actual(std::move(actual)) {}

    std::string expected;
    std::string actual;
};

// A storage invariant has been violated (duplicate key, foreign-key, etc.).
class IntegrityError : public StorageError {
public:
    explicit IntegrityError(std::string message, std::unordered_map<std::string, std::string> context = {})
        : StorageError(std::move(message), "STORAGE_INTEGRITY", std::move(context)) {}
};

// The storage backend is at capacity and cannot accept more entries.
class CapacityError : public StorageError {
public:
    CapacityError(int capacity, int used)
        : StorageError("capacity " + std::to_string(used) + "/" + std::to_string(capacity) + " reached",
                        "STORAGE_CAPACITY",
                        {{"capacity", std::to_string(capacity)}, {"used", std::to_string(used)}}),
          capacity(capacity),
          used(used) {}

    int capacity;
    int used;
};

// An EventBus handler raised during dispatch. The original exception is
// preserved as `original` (a std::exception_ptr) so callers can
// std::rethrow_exception it themselves -- a strictly more capable
// analogue of Python's __cause__ chaining.
class HandlerError : public HelixError {
public:
    HandlerError(std::string handler_name, std::string original_type_name, std::exception_ptr original)
        : HelixError("handler '" + handler_name + "' raised " + original_type_name, "HANDLER_ERROR",
                      {{"handler", handler_name}}),
          handler_name(std::move(handler_name)),
          original(original) {}

    std::string handler_name;
    std::exception_ptr original;
};

// A model failed validation.
class ValidationError : public HelixError {
public:
    explicit ValidationError(std::string message, std::string error_code = "VALIDATION",
                              std::unordered_map<std::string, std::string> context = {})
        : HelixError(std::move(message), std::move(error_code), std::move(context)) {}
};

// One specific field failed validation.
class FieldValidationError : public ValidationError {
public:
    FieldValidationError(std::string field, std::string reason)
        : ValidationError(field + ": " + reason, "VALIDATION_FIELD", {{"field", field}, {"reason", reason}}),
          field(std::move(field)),
          reason(std::move(reason)) {}

    std::string field;
    std::string reason;
};

// A constraint spanning multiple fields was violated.
class CrossFieldValidationError : public ValidationError {
public:
    CrossFieldValidationError(std::vector<std::string> fields, std::string reason)
        : ValidationError(join_fields(fields) + ": " + reason, "VALIDATION_CROSS_FIELD",
                           {{"fields", join_fields(fields)}, {"reason", reason}}),
          fields(std::move(fields)),
          reason(std::move(reason)) {}

    std::vector<std::string> fields;
    std::string reason;

private:
    static std::string join_fields(const std::vector<std::string>& fields) {
        std::string out = "[";
        for (std::size_t i = 0; i < fields.size(); ++i) {
            if (i > 0) out += ", ";
            out += fields[i];
        }
        out += "]";
        return out;
    }
};

// The runtime configuration is bad -- usually surfaces at startup.
class ConfigurationError : public HelixError {
public:
    explicit ConfigurationError(std::string message) : HelixError(std::move(message), "CONFIG") {}
};

// A transient error: the caller may retry without changing inputs.
// `retry_after`: suggested seconds to wait before retrying. Zero means
// "retry immediately"; negative is treated as "don't retry".
class RetryableError : public HelixError {
public:
    explicit RetryableError(std::string message, double retry_after = 0.0)
        : HelixError(std::move(message), "RETRYABLE", {{"retry_after", std::to_string(retry_after)}}),
          retry_after(retry_after) {}

    double retry_after;

    // True iff the caller should retry this failure.
    bool is_retryable() const { return retry_after >= 0.0; }
};

}  // namespace lib_core::errors
