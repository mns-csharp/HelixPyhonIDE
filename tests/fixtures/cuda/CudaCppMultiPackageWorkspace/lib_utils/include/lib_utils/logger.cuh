#pragma once

// Logging utilities: original Logger, plus a Formatter Protocol, factory.
//
// Mirrors lib_utils/logger.py. Translation notes:
//   - `Formatter` Protocol -> C++20 concept (structural).
//   - `AsyncLogger` / `running_async_logger` (queue-drained-by-a-
//     background-task) collapse onto the same synchronous `write`
//     StructuredLogger already has, per this workspace's "represent
//     async as synchronous" policy -- a queue that's drained
//     synchronously on every push has no externally observable
//     difference from writing immediately.

#include <concepts>
#include <cstdio>
#include <ctime>
#include <iostream>
#include <map>
#include <memory>
#include <ostream>
#include <sstream>
#include <string>
#include <unordered_map>

namespace lib_utils::logger {

// -- Original Logger (kept verbatim, modulo formatting) --------------------

// A tiny prefix-based console logger.
class Logger {
public:
    explicit Logger(std::string prefix) : prefix_(std::move(prefix)) {}

    void info(const std::string& msg) const { std::cout << prefix_ << " INFO: " << msg << "\n"; }
    void warn(const std::string& msg) const { std::cout << prefix_ << " WARN: " << msg << "\n"; }
    void error(const std::string& msg) const { std::cout << prefix_ << " ERROR: " << msg << "\n"; }

    // Alternative constructor -- exercises the "named constructor"
    // idiom (Python's @classmethod).
    static Logger from_config(const std::string& prefix) { return Logger(prefix); }

    std::string repr() const { return "Logger(prefix='" + prefix_ + "')"; }

    const std::string& prefix() const { return prefix_; }

private:
    std::string prefix_;
};

// -- Levels -----------------------------------------------------------------

// Severity levels in ascending order.
enum class Level { Debug = 10, Info = 20, Warn = 30, Error = 40, Critical = 50 };

inline const char* level_name(Level level) {
    switch (level) {
        case Level::Debug: return "DEBUG";
        case Level::Info: return "INFO";
        case Level::Warn: return "WARN";
        case Level::Error: return "ERROR";
        case Level::Critical: return "CRITICAL";
    }
    return "UNKNOWN";
}

// -- Records ----------------------------------------------------------------

// A single log entry.
struct LogRecord {
    Level level;
    std::string message;
    double timestamp;
    std::string prefix;
    std::map<std::string, std::string> extra;
};

// -- Formatter Protocol ----------------------------------------------------

// Anything that turns a LogRecord into a serialized line.
template <typename F>
concept Formatter = requires(const F& f, const LogRecord& record) {
    { f.format(record) } -> std::convertible_to<std::string>;
};

// Plaintext "[prefix] LEVEL message" formatter.
class TextFormatter {
public:
    std::string format(const LogRecord& record) const {
        std::time_t t = static_cast<std::time_t>(record.timestamp);
        std::tm tm_buf{};
#if defined(_WIN32)
        localtime_s(&tm_buf, &t);
#else
        localtime_r(&t, &tm_buf);
#endif
        char time_buf[16];
        std::strftime(time_buf, sizeof(time_buf), "%H:%M:%S", &tm_buf);
        std::ostringstream out;
        if (!record.prefix.empty()) out << "[" << record.prefix << "] ";
        out << time_buf << " " << level_name(record.level) << ": " << record.message;
        return out.str();
    }
};

// JSON-line formatter -- one record per line, machine-parseable. (A
// minimal hand-rolled encoder; no JSON library dependency is introduced
// for this fixture.)
class JSONFormatter {
public:
    std::string format(const LogRecord& record) const {
        std::ostringstream out;
        out << "{\"ts\":" << record.timestamp << ",\"level\":\"" << level_name(record.level) << "\",\"msg\":\""
            << record.message << "\"";
        if (!record.prefix.empty()) out << ",\"prefix\":\"" << record.prefix << "\"";
        out << "}";
        return out.str();
    }
};

// -- StructuredLogger --------------------------------------------------

// Logger that pushes LogRecords through a Formatter to a stream.
class StructuredLogger {
public:
    explicit StructuredLogger(std::string prefix = "", Level level = Level::Info, std::ostream* stream = &std::cout)
        : prefix_(std::move(prefix)), level(level), stream_(stream) {}

    // Emit `message` at `level` if it meets the configured threshold.
    void log(Level msg_level, const std::string& message, const std::map<std::string, std::string>& extra = {}) {
        if (static_cast<int>(msg_level) < static_cast<int>(level)) return;
        LogRecord record{msg_level, message, 0.0, prefix_, extra};
        (*stream_) << format_.format(record) << "\n";
        stream_->flush();
    }

    void debug(const std::string& message) { log(Level::Debug, message); }
    void info(const std::string& message) { log(Level::Info, message); }
    void warn(const std::string& message) { log(Level::Warn, message); }
    void error(const std::string& message) { log(Level::Error, message); }
    void critical(const std::string& message) { log(Level::Critical, message); }

    Level level;

private:
    std::string prefix_;
    std::ostream* stream_;
    TextFormatter format_;
};

// -- Factory + scope helper -------------------------------------------------

// Registry of named loggers -- one per prefix. Subsequent get() calls for
// the same prefix return the same instance, so configuration tweaks
// propagate to existing call sites without threading the logger through
// arguments.
class LoggerFactory {
public:
    explicit LoggerFactory(Level default_level = Level::Info) : default_level_(default_level) {}

    // Return the logger for `prefix`; create one if missing.
    StructuredLogger& get(const std::string& prefix) {
        auto it = registry_.find(prefix);
        if (it != registry_.end()) return it->second;
        auto [inserted, _] = registry_.emplace(prefix, StructuredLogger(prefix, default_level_));
        return inserted->second;
    }

private:
    std::unordered_map<std::string, StructuredLogger> registry_;
    Level default_level_;
};

// Run `body(logger)` with `prefix`'s logger temporarily reconfigured to
// `level`, restoring the prior level afterwards -- mirrors Python's
// `with logger_scope(factory, prefix, level=...) as log:`.
template <typename F>
void logger_scope(LoggerFactory& factory, const std::string& prefix, Level level, F body) {
    StructuredLogger& log = factory.get(prefix);
    Level saved = log.level;
    log.level = level;
    try {
        body(log);
    } catch (...) {
        log.level = saved;
        throw;
    }
    log.level = saved;
}

}  // namespace lib_utils::logger
