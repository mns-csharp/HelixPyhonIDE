// pyrt.hpp - runtime support library for HelixStudio's Python -> C++ transpiler.
// Generated/embedded by HelixStudio. Do not edit generated output by hand; this
// file itself is hand-maintained and embedded verbatim into every build.
//
// Design goal: every operation here reproduces Python's *language-level*
// semantics exactly (not CPython's internal implementation details). The one
// deliberate, documented divergence is pyrt::Set iteration order, which the
// Python language itself does not guarantee either.
//
// Requires a C++17-or-later compiler with <charconv> floating-point support
// (MSVC 19.24+ / VS2019 16.4+; this project builds with VS 18).
#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <memory>
#include <stdexcept>
#include <sstream>
#include <iostream>
#include <functional>
#include <optional>
#include <tuple>
#include <algorithm>
#include <cmath>
#include <charconv>
#include <limits>
#include <type_traits>
#include <initializer_list>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace pyrt {

// ===================================================================
// Primitive aliases
// ===================================================================
using py_int = std::int64_t;   // fixed-width; see checked_add/sub/mul below
using py_float = double;       // CPython's float IS a C double, so this is exact
using py_bool = bool;

// ===================================================================
// Exception hierarchy - mirrors Python's builtin exception classes so a
// `catch (SomeException&)` in emitted C++ matches exactly what Python's
// `except SomeException:` would match, via ordinary C++ polymorphism.
// ===================================================================
class PyException : public std::exception {
public:
    explicit PyException(std::string msg) : msg_(std::move(msg)) {}
    const char* what() const noexcept override { return msg_.c_str(); }
    const std::string& message() const noexcept { return msg_; }
private:
    std::string msg_;
};

#define PYRT_DECLARE_EXCEPTION(Name) \
    class Name : public PyException { public: using PyException::PyException; };

PYRT_DECLARE_EXCEPTION(PyValueError)
PYRT_DECLARE_EXCEPTION(PyTypeError)
PYRT_DECLARE_EXCEPTION(PyKeyError)
PYRT_DECLARE_EXCEPTION(PyIndexError)
PYRT_DECLARE_EXCEPTION(PyZeroDivisionError)
PYRT_DECLARE_EXCEPTION(PyOverflowError)
PYRT_DECLARE_EXCEPTION(PyAttributeError)
PYRT_DECLARE_EXCEPTION(PyRuntimeError)
PYRT_DECLARE_EXCEPTION(PyNotImplementedError)
PYRT_DECLARE_EXCEPTION(PyAssertionError)
PYRT_DECLARE_EXCEPTION(PyStopIteration)
PYRT_DECLARE_EXCEPTION(PyNameError)
PYRT_DECLARE_EXCEPTION(PyUnicodeDecodeError)

#undef PYRT_DECLARE_EXCEPTION

// ===================================================================
// finally - a plain RAII guard runs on EVERY exit path (fallthrough,
// return, break/continue via scope exit, or exception unwinding), which
// is exactly Python's `finally:` semantics. Emitted as:
//   { pyrt::FinallyGuard __pyrt_finally([&]{ <finally body> }); <try body> }
// ===================================================================
class FinallyGuard {
public:
    explicit FinallyGuard(std::function<void()> fn) : fn_(std::move(fn)) {}
    ~FinallyGuard() { if (fn_) fn_(); }
    FinallyGuard(const FinallyGuard&) = delete;
    FinallyGuard& operator=(const FinallyGuard&) = delete;
private:
    std::function<void()> fn_;
};

// ===================================================================
// Checked 64-bit integer arithmetic. Python ints are arbitrary-precision;
// this runtime deliberately does NOT implement bignum (documented scope
// boundary), so instead of silently wrapping/giving a wrong answer on
// overflow (as raw C++ `int64_t` would), every operation that can
// overflow throws PyOverflowError. Failing loudly is the "zero silent
// semantic drift" contract for the one place this subset can't match
// Python exactly.
// ===================================================================
inline py_int checked_add(py_int a, py_int b) {
    if (b > 0 && a > std::numeric_limits<py_int>::max() - b) throw PyOverflowError("integer addition overflow");
    if (b < 0 && a < std::numeric_limits<py_int>::min() - b) throw PyOverflowError("integer addition overflow");
    return a + b;
}

inline py_int checked_sub(py_int a, py_int b) {
    if (b < 0 && a > std::numeric_limits<py_int>::max() + b) throw PyOverflowError("integer subtraction overflow");
    if (b > 0 && a < std::numeric_limits<py_int>::min() + b) throw PyOverflowError("integer subtraction overflow");
    return a - b;
}

inline py_int checked_mul(py_int a, py_int b) {
    if (a == 0 || b == 0) return 0;
#if defined(_MSC_VER) && defined(_M_X64)
    long long high = 0;
    long long low = _mul128(a, b, &high);
    long long expectedHigh = (low < 0) ? -1LL : 0LL;
    if (high != expectedHigh) throw PyOverflowError("integer multiplication overflow");
    return low;
#elif defined(__SIZEOF_INT128__)
    __int128 full = static_cast<__int128>(a) * static_cast<__int128>(b);
    if (full > std::numeric_limits<py_int>::max() || full < std::numeric_limits<py_int>::min())
        throw PyOverflowError("integer multiplication overflow");
    return static_cast<py_int>(full);
#else
    // Portable fallback: pre-check via division (slower, still exact).
    py_int limit = std::numeric_limits<py_int>::max();
    py_int lo = std::numeric_limits<py_int>::min();
    if (a > 0) {
        if (b > 0) { if (a > limit / b) throw PyOverflowError("integer multiplication overflow"); }
        else       { if (b < lo / a)    throw PyOverflowError("integer multiplication overflow"); }
    } else {
        if (b > 0) { if (a < lo / b)    throw PyOverflowError("integer multiplication overflow"); }
        else       { if (a != 0 && b < limit / a) throw PyOverflowError("integer multiplication overflow"); }
    }
    return a * b;
#endif
}

// Python integer floor division: rounds toward negative infinity, not
// toward zero (C++'s `/` truncates toward zero for signed ints, so we
// correct when the signs of the operands differ and there's a remainder).
inline py_int floordiv(py_int a, py_int b) {
    if (b == 0) throw PyZeroDivisionError("integer division or modulo by zero");
    if (a == std::numeric_limits<py_int>::min() && b == -1) throw PyOverflowError("integer division overflow");
    py_int q = a / b;
    if ((a % b != 0) && ((a < 0) != (b < 0))) q -= 1;
    return q;
}

// Python modulo: result takes the sign of the divisor (unlike C++'s `%`,
// which takes the sign of the dividend).
inline py_int mod(py_int a, py_int b) {
    if (b == 0) throw PyZeroDivisionError("integer division or modulo by zero");
    if (a == std::numeric_limits<py_int>::min() && b == -1) return 0;
    py_int r = a % b;
    if (r != 0 && ((r < 0) != (b < 0))) r += b;
    return r;
}

inline py_float fmod_py(py_float a, py_float b) {
    if (b == 0.0) throw PyZeroDivisionError("float modulo");
    py_float r = std::fmod(a, b);
    if (r != 0.0 && ((r < 0) != (b < 0))) r += b;
    return r;
}

inline py_float floordiv_float(py_float a, py_float b) {
    if (b == 0.0) throw PyZeroDivisionError("float floor division by zero");
    return std::floor(a / b);
}

inline py_float true_div(py_int a, py_int b) {
    if (b == 0) throw PyZeroDivisionError("division by zero");
    return static_cast<py_float>(a) / static_cast<py_float>(b);
}

// `**` between two ints stays an int ONLY for a non-negative exponent
// (Python: `2 ** -1 == 0.5`, a float). Pass 1 requires the exponent to be
// provably non-negative for the result to type as int; this is the
// runtime backstop for that contract.
inline py_int pow_int(py_int base, py_int exp) {
    if (exp < 0) throw PyValueError("negative exponent for integer power (result would be float)");
    py_int result = 1, b = base, e = exp;
    while (e > 0) {
        if (e & 1) result = checked_mul(result, b);
        e >>= 1;
        if (e > 0) b = checked_mul(b, b);
    }
    return result;
}

inline py_float pow_float(py_float base, py_float exp) { return std::pow(base, exp); }

// ===================================================================
// Slicing - ports CPython's exact PySlice_Unpack / PySlice_AdjustIndices
// algorithm (Objects/sliceobject.c) so start/stop/step normalization,
// negative-index wraparound, and out-of-range clamping match bit-for-bit.
// ===================================================================
struct SliceIndices {
    py_int start, stop, step, length; // `length` = number of elements the slice yields
};

inline SliceIndices normalize_slice(py_int seq_len,
                                     std::optional<py_int> start_opt,
                                     std::optional<py_int> stop_opt,
                                     std::optional<py_int> step_opt) {
    py_int step = step_opt.value_or(1);
    if (step == 0) throw PyValueError("slice step cannot be zero");

    py_int start = start_opt.has_value() ? *start_opt
                 : (step < 0 ? std::numeric_limits<py_int>::max() : 0);
    py_int stop = stop_opt.has_value() ? *stop_opt
                : (step < 0 ? std::numeric_limits<py_int>::min() : std::numeric_limits<py_int>::max());

    if (start < 0) { start += seq_len; if (start < 0) start = (step < 0) ? -1 : 0; }
    else if (start >= seq_len) { start = (step < 0) ? seq_len - 1 : seq_len; }

    if (stop < 0) { stop += seq_len; if (stop < 0) stop = (step < 0) ? -1 : 0; }
    else if (stop >= seq_len) { stop = (step < 0) ? seq_len - 1 : seq_len; }

    py_int slicelen = 0;
    if (step < 0) { if (stop < start) slicelen = (start - stop - 1) / (-step) + 1; }
    else          { if (start < stop) slicelen = (stop - start - 1) / step + 1; }

    return { start, stop, step, slicelen };
}

// Python-style index normalization for single-element access (`seq[i]`):
// negative indices count from the end; out-of-range raises IndexError.
inline py_int normalize_index(py_int seq_len, py_int index, const char* what = "index") {
    py_int i = index;
    if (i < 0) i += seq_len;
    if (i < 0 || i >= seq_len) {
        std::ostringstream os; os << what << " out of range";
        throw PyIndexError(os.str());
    }
    return i;
}

// ===================================================================
// Str - UTF-8 storage, codepoint-based len()/index/slice (Python indexes
// strings by Unicode codepoint, not by byte - len("héllo") == 5, not
// 6). Deliberately O(n) per index/slice/len (decodes on demand): correct
// over fast, matching this runtime's whole "verified semantics first"
// design; a string-heavy hot loop is not this translator's target.
// ===================================================================
namespace detail {

inline int utf8_seq_len(unsigned char lead) {
    if ((lead & 0x80) == 0x00) return 1;
    if ((lead & 0xE0) == 0xC0) return 2;
    if ((lead & 0xF0) == 0xE0) return 3;
    if ((lead & 0xF8) == 0xF0) return 4;
    throw PyUnicodeDecodeError("invalid UTF-8 lead byte");
}

// Byte offsets of every codepoint boundary in `s`, plus a trailing entry
// at s.size() so offsets[i]..offsets[i+1] bounds codepoint i.
inline std::vector<std::size_t> codepoint_offsets(const std::string& s) {
    std::vector<std::size_t> offs;
    offs.reserve(s.size() + 1);
    std::size_t i = 0;
    while (i < s.size()) {
        offs.push_back(i);
        i += utf8_seq_len(static_cast<unsigned char>(s[i]));
        if (i > s.size()) throw PyUnicodeDecodeError("truncated UTF-8 sequence");
    }
    offs.push_back(s.size());
    return offs;
}

} // namespace detail

class Str {
public:
    Str() = default;
    Str(const char* s) : data_(s) {}
    Str(std::string s) : data_(std::move(s)) {}

    const std::string& bytes() const noexcept { return data_; }
    // Implicit conversion so a Str can be passed anywhere a std::string is expected —
    // e.g. constructing a pyrt exception (PyException(std::string)) from a Str message.
    operator std::string() const { return data_; }

    py_int len() const { return static_cast<py_int>(detail::codepoint_offsets(data_).size() - 1); }

    // seq[i] - returns a new one-codepoint Str.
    Str at(py_int index) const {
        auto offs = detail::codepoint_offsets(data_);
        py_int n = static_cast<py_int>(offs.size() - 1);
        py_int i = normalize_index(n, index, "string index");
        return Str(data_.substr(offs[i], offs[i + 1] - offs[i]));
    }

    Str slice(std::optional<py_int> start, std::optional<py_int> stop, std::optional<py_int> step) const {
        auto offs = detail::codepoint_offsets(data_);
        py_int n = static_cast<py_int>(offs.size() - 1);
        SliceIndices s = normalize_slice(n, start, stop, step);
        std::string out;
        if (s.step == 1) {
            if (s.length > 0) out = data_.substr(offs[s.start], offs[s.start + s.length] - offs[s.start]);
        } else {
            out.reserve(static_cast<std::size_t>(s.length));
            py_int idx = s.start;
            for (py_int k = 0; k < s.length; ++k, idx += s.step)
                out.append(data_, offs[idx], offs[idx + 1] - offs[idx]);
        }
        return Str(std::move(out));
    }

    Str operator+(const Str& other) const { return Str(data_ + other.data_); }
    Str& operator+=(const Str& other) { data_ += other.data_; return *this; }

    Str repeat(py_int n) const {
        if (n <= 0) return Str();
        std::string out;
        out.reserve(data_.size() * static_cast<std::size_t>(n));
        for (py_int i = 0; i < n; ++i) out += data_;
        return Str(std::move(out));
    }

    bool contains(const Str& needle) const { return data_.find(needle.data_) != std::string::npos; }

    bool operator==(const Str& other) const { return data_ == other.data_; }
    bool operator!=(const Str& other) const { return data_ != other.data_; }
    bool operator<(const Str& other) const { return data_ < other.data_; }
    bool operator<=(const Str& other) const { return data_ <= other.data_; }
    bool operator>(const Str& other) const { return data_ > other.data_; }
    bool operator>=(const Str& other) const { return data_ >= other.data_; }

    bool empty() const noexcept { return data_.empty(); }

private:
    std::string data_;
};

// ===================================================================
// str()/repr()/print() - to_str() matches Python's str() conversion,
// including exact float formatting (shortest round-trip decimal via
// <charconv>, then Python-style touch-ups: an integral float always
// keeps a trailing ".0", never "1e+30"-style unless Python itself would
// use exponent notation).
// ===================================================================
inline std::string to_str(py_bool v) { return v ? "True" : "False"; }
inline std::string to_str(py_int v) { return std::to_string(v); }

inline std::string to_str(py_float v) {
    if (std::isnan(v)) return "nan";
    if (std::isinf(v)) return v < 0 ? "-inf" : "inf";
    char buf[64];
    auto res = std::to_chars(buf, buf + sizeof(buf), v);
    std::string s(buf, res.ptr);
    // std::to_chars never emits a trailing ".0" for integral values, and Python always does.
    if (s.find('.') == std::string::npos && s.find('e') == std::string::npos &&
        s.find("inf") == std::string::npos && s.find("nan") == std::string::npos) {
        s += ".0";
    }
    return s;
}

inline std::string to_str(const Str& v) { return v.bytes(); }
inline std::string to_str(const std::string& v) { return v; }
inline const char* to_str(const char* v) { return v; }

// str()/print() of an Optional[T] value — only ever reached after Python code has already
// narrowed it (an `if x is not None:` guard), so the value is expected to be present;
// dereferencing an empty one is a genuine translate-time-unreachable case, not a silent
// wrong answer, so it throws rather than printing something misleading like "None".
template <typename T>
inline std::string to_str(const std::optional<T>& v) {
    if (!v.has_value()) throw PyValueError("no string representation for an empty Optional value");
    return to_str(*v);
}

inline std::string repr(const Str& v) {
    std::string out = "'";
    for (char c : v.bytes()) {
        switch (c) {
            case '\'': out += "\\'"; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default: out += c;
        }
    }
    out += "'";
    return out;
}
inline std::string repr(py_int v) { return to_str(v); }
inline std::string repr(py_float v) { return to_str(v); }
inline std::string repr(py_bool v) { return to_str(v); }

inline void print() { std::cout << "\n"; }

template <typename T>
inline void print(const T& v) { std::cout << to_str(v) << "\n"; }

// ===================================================================
// List<T> - reference-semantics wrapper (Python `a = b` aliases the same
// list; mutating through one reference is visible through the other).
// Copying the handle copies the shared_ptr, not the underlying vector.
// ===================================================================
template <typename T>
class List {
public:
    List() : impl_(std::make_shared<std::vector<T>>()) {}
    explicit List(std::vector<T> init) : impl_(std::make_shared<std::vector<T>>(std::move(init))) {}
    List(std::initializer_list<T> init) : impl_(std::make_shared<std::vector<T>>(init)) {}

    py_int len() const { return static_cast<py_int>(impl_->size()); }
    bool empty() const { return impl_->empty(); }

    T& at(py_int index) { return (*impl_)[static_cast<std::size_t>(normalize_index(len(), index, "list index"))]; }
    const T& at(py_int index) const { return (*impl_)[static_cast<std::size_t>(normalize_index(len(), index, "list index"))]; }

    void set_at(py_int index, T value) {
        (*impl_)[static_cast<std::size_t>(normalize_index(len(), index, "list assignment index"))] = std::move(value);
    }

    List<T> slice(std::optional<py_int> start, std::optional<py_int> stop, std::optional<py_int> step) const {
        SliceIndices s = normalize_slice(len(), start, stop, step);
        std::vector<T> out;
        out.reserve(static_cast<std::size_t>(std::max<py_int>(0, s.length)));
        py_int idx = s.start;
        for (py_int k = 0; k < s.length; ++k, idx += s.step) out.push_back((*impl_)[static_cast<std::size_t>(idx)]);
        return List<T>(std::move(out));
    }

    void append(T value) { impl_->push_back(std::move(value)); }
    void extend(const List<T>& other) { impl_->insert(impl_->end(), other.impl_->begin(), other.impl_->end()); }
    void insert(py_int index, T value) {
        py_int i = std::clamp<py_int>(index < 0 ? index + len() : index, 0, len());
        impl_->insert(impl_->begin() + static_cast<std::ptrdiff_t>(i), std::move(value));
    }
    T pop() {
        if (impl_->empty()) throw PyIndexError("pop from empty list");
        T v = std::move(impl_->back());
        impl_->pop_back();
        return v;
    }
    T pop_at(py_int index) {
        py_int i = normalize_index(len(), index, "pop index");
        auto it = impl_->begin() + static_cast<std::ptrdiff_t>(i);
        T v = std::move(*it);
        impl_->erase(it);
        return v;
    }
    void clear() { impl_->clear(); }
    List<T> copy() const { return List<T>(std::vector<T>(*impl_)); }
    void reverse() { std::reverse(impl_->begin(), impl_->end()); }
    void sort() { std::sort(impl_->begin(), impl_->end()); }

    bool contains(const T& value) const {
        return std::find(impl_->begin(), impl_->end(), value) != impl_->end();
    }

    bool operator==(const List<T>& other) const { return *impl_ == *other.impl_; }
    bool operator!=(const List<T>& other) const { return !(*this == other); }

    auto begin() { return impl_->begin(); }
    auto end() { return impl_->end(); }
    auto begin() const { return impl_->cbegin(); }
    auto end() const { return impl_->cend(); }

private:
    std::shared_ptr<std::vector<T>> impl_;
};

template <typename T>
inline std::string to_str(const List<T>& v) {
    std::ostringstream os;
    os << '[';
    bool first = true;
    for (const auto& e : v) { if (!first) os << ", "; first = false; os << repr(e); }
    os << ']';
    return os.str();
}

// repr() for a dict/set key inside a KeyError message needs to work for
// any key type. Declared here (before Dict/Set) rather than via ADL:
// a fundamental-type key like py_int (== int64_t) has no associated
// namespace for ADL to search, so an unqualified call from inside the
// Dict/Set templates only resolves if these overloads are already
// visible via ordinary lookup at the templates' definition point.
inline std::string repr_key(py_int k) { return repr(k); }
inline std::string repr_key(const Str& k) { return repr(k); }
inline std::string repr_key(const std::string& k) { return "'" + k + "'"; }

// ===================================================================
// Dict<K,V> - insertion-ordered (Python dicts have guaranteed insertion
// order since 3.7 and real programs rely on it). Backed by a vector for
// order + an unordered_map index for O(1) lookup.
// ===================================================================
template <typename K, typename V>
class Dict {
    struct Impl {
        std::vector<std::pair<K, V>> order;
        std::unordered_map<K, std::size_t> index;
    };
public:
    Dict() : impl_(std::make_shared<Impl>()) {}

    py_int len() const { return static_cast<py_int>(impl_->order.size()); }
    bool empty() const { return impl_->order.empty(); }

    bool contains(const K& key) const { return impl_->index.find(key) != impl_->index.end(); }

    V get(const K& key) const {
        auto it = impl_->index.find(key);
        if (it == impl_->index.end()) throw PyKeyError(repr_key(key));
        return impl_->order[it->second].second;
    }
    V get(const K& key, V default_value) const {
        auto it = impl_->index.find(key);
        if (it == impl_->index.end()) return default_value;
        return impl_->order[it->second].second;
    }

    void set(const K& key, V value) {
        auto it = impl_->index.find(key);
        if (it != impl_->index.end()) { impl_->order[it->second].second = std::move(value); return; }
        impl_->index.emplace(key, impl_->order.size());
        impl_->order.emplace_back(key, std::move(value));
    }

    void erase(const K& key) {
        auto it = impl_->index.find(key);
        if (it == impl_->index.end()) throw PyKeyError(repr_key(key));
        std::size_t pos = it->second;
        impl_->order.erase(impl_->order.begin() + static_cast<std::ptrdiff_t>(pos));
        impl_->index.erase(it);
        for (auto& kv : impl_->index) if (kv.second > pos) --kv.second;
    }

    void clear() { impl_->order.clear(); impl_->index.clear(); }
    Dict<K, V> copy() const { Dict<K, V> d; d.impl_ = std::make_shared<Impl>(*impl_); return d; }

    // Iteration follows Python's `for k in d` (keys, insertion order).
    struct KeyIterator {
        const std::vector<std::pair<K, V>>* order; std::size_t i;
        bool operator!=(const KeyIterator& o) const { return i != o.i; }
        void operator++() { ++i; }
        const K& operator*() const { return (*order)[i].first; }
    };
    KeyIterator begin() const { return { &impl_->order, 0 }; }
    KeyIterator end() const { return { &impl_->order, impl_->order.size() }; }

    const std::vector<std::pair<K, V>>& items() const { return impl_->order; }

    bool operator==(const Dict<K, V>& other) const {
        if (len() != other.len()) return false;
        for (const auto& kv : impl_->order) {
            if (!other.contains(kv.first) || !(other.get(kv.first) == kv.second)) return false;
        }
        return true;
    }

private:
    std::shared_ptr<Impl> impl_;
};

template <typename K, typename V>
inline std::string to_str(const Dict<K, V>& v) {
    std::ostringstream os;
    os << '{';
    bool first = true;
    for (const auto& kv : v.items()) {
        if (!first) os << ", ";
        first = false;
        os << repr(kv.first) << ": " << repr(kv.second);
    }
    os << '}';
    return os.str();
}

// ===================================================================
// Set<T> - reference semantics like List/Dict. Iteration order is NOT
// guaranteed (matches the Python *language* spec, not any particular
// CPython version's incidental hash-table order) - the one deliberate,
// documented divergence in this runtime, and it is not language-level
// behavior any correct Python program may depend on.
// ===================================================================
template <typename T>
class Set {
public:
    Set() : impl_(std::make_shared<std::unordered_set<T>>()) {}
    explicit Set(std::unordered_set<T> init) : impl_(std::make_shared<std::unordered_set<T>>(std::move(init))) {}

    py_int len() const { return static_cast<py_int>(impl_->size()); }
    bool empty() const { return impl_->empty(); }
    bool contains(const T& v) const { return impl_->find(v) != impl_->end(); }

    void add(T v) { impl_->insert(std::move(v)); }
    void discard(const T& v) { impl_->erase(v); }
    void remove(const T& v) {
        if (impl_->erase(v) == 0) throw PyKeyError(repr_key(v));
    }
    void clear() { impl_->clear(); }
    Set<T> copy() const { return Set<T>(std::unordered_set<T>(*impl_)); }

    Set<T> union_with(const Set<T>& other) const {
        std::unordered_set<T> out(*impl_);
        out.insert(other.impl_->begin(), other.impl_->end());
        return Set<T>(std::move(out));
    }
    Set<T> intersection(const Set<T>& other) const {
        std::unordered_set<T> out;
        for (const auto& v : *impl_) if (other.contains(v)) out.insert(v);
        return Set<T>(std::move(out));
    }
    Set<T> difference(const Set<T>& other) const {
        std::unordered_set<T> out;
        for (const auto& v : *impl_) if (!other.contains(v)) out.insert(v);
        return Set<T>(std::move(out));
    }

    bool operator==(const Set<T>& other) const { return *impl_ == *other.impl_; }

    auto begin() const { return impl_->cbegin(); }
    auto end() const { return impl_->cend(); }

private:
    std::shared_ptr<std::unordered_set<T>> impl_;
};

template <typename T>
inline std::string to_str(const Set<T>& v) {
    if (v.empty()) return "set()";
    std::ostringstream os;
    os << '{';
    bool first = true;
    for (const auto& e : v) { if (!first) os << ", "; first = false; os << repr(e); }
    os << '}';
    return os.str();
}

// ===================================================================
// Tuple - Python tuples are immutable, so plain value semantics are
// already exact; std::tuple already does elementwise equality matching
// Python's tuple `==`. No wrapper needed beyond a convenience alias.
// ===================================================================
template <typename... Ts>
using Tuple = std::tuple<Ts...>;

// ===================================================================
// truthy() - Python's implicit boolean conversion (`if x:`, `while x:`, `not x`),
// used wherever a non-bool value appears in a boolean context. 0 / 0.0 / "" / empty
// container are falsy, matching Python exactly; everything else is truthy.
// ===================================================================
inline bool truthy(py_bool v) { return v; }
inline bool truthy(py_int v) { return v != 0; }
inline bool truthy(py_float v) { return v != 0.0; }
inline bool truthy(const Str& v) { return !v.empty(); }
template <typename T> inline bool truthy(const List<T>& v) { return !v.empty(); }
template <typename K, typename V> inline bool truthy(const Dict<K, V>& v) { return !v.empty(); }
template <typename T> inline bool truthy(const Set<T>& v) { return !v.empty(); }
// A shared_ptr-held class instance (or std::optional) is truthy iff it isn't None.
template <typename T> inline bool truthy(const std::shared_ptr<T>& v) { return v != nullptr; }
template <typename T> inline bool truthy(const std::optional<T>& v) { return v.has_value(); }

// ===================================================================
// `and` / `or` - Python's short-circuit operators return one of the OPERANDS
// (whichever truthiness decided), not a coerced bool the way C++'s && / || do.
// Only meaningful (and only emitted by Pass 2) when both operands share one
// static type, per Pass 1's same-type requirement for and/or in this subset.
// ===================================================================
template <typename T> inline T py_and(const T& a, const T& b) { return truthy(a) ? b : a; }
template <typename T> inline T py_or(const T& a, const T& b) { return truthy(a) ? a : b; }

// ===================================================================
// f-string / format() mini-language - covers fill+align, sign, width,
// precision, and the 'd'/'f'/'s'/'x'/'X'/'o'/'b'/'e'/'%' type codes.
// Documented boundary: does not implement the full grammar (grouping
// options ',' / '_', nested replacement fields, or 'n'/locale-aware
// formatting) - unsupported spec syntax throws rather than silently
// misformatting.
// ===================================================================
struct FormatSpec {
    char fill = ' ';
    char align = '\0';   // '<' '>' '^' '=' or '\0' for "use type default"
    char sign = '-';      // '+' '-' ' '
    bool alt = false;     // '#'
    bool zero_pad = false;
    int width = 0;
    bool has_precision = false;
    int precision = 0;
    char type = '\0';
};

inline FormatSpec parse_format_spec(const std::string& spec) {
    FormatSpec f;
    std::size_t i = 0;
    if (spec.size() >= 2 && (spec[1] == '<' || spec[1] == '>' || spec[1] == '^' || spec[1] == '=')) {
        f.fill = spec[0]; f.align = spec[1]; i = 2;
    } else if (!spec.empty() && (spec[0] == '<' || spec[0] == '>' || spec[0] == '^' || spec[0] == '=')) {
        f.align = spec[0]; i = 1;
    }
    if (i < spec.size() && (spec[i] == '+' || spec[i] == '-' || spec[i] == ' ')) { f.sign = spec[i]; ++i; }
    if (i < spec.size() && spec[i] == '#') { f.alt = true; ++i; }
    if (i < spec.size() && spec[i] == '0') { f.zero_pad = true; if (f.align == '\0') { f.align = '='; f.fill = '0'; } ++i; }
    std::size_t widthStart = i;
    while (i < spec.size() && std::isdigit(static_cast<unsigned char>(spec[i]))) ++i;
    if (i > widthStart) f.width = std::stoi(spec.substr(widthStart, i - widthStart));
    if (i < spec.size() && spec[i] == '.') {
        ++i;
        std::size_t precStart = i;
        while (i < spec.size() && std::isdigit(static_cast<unsigned char>(spec[i]))) ++i;
        f.has_precision = true;
        f.precision = (i > precStart) ? std::stoi(spec.substr(precStart, i - precStart)) : 0;
    }
    if (i < spec.size()) { f.type = spec[i]; ++i; }
    if (i != spec.size()) throw PyValueError("unsupported format spec syntax: " + spec);
    return f;
}

inline std::string pad(std::string body, const FormatSpec& f, bool numeric) {
    if (static_cast<int>(body.size()) >= f.width) return body;
    int padLen = f.width - static_cast<int>(body.size());
    char align = f.align != '\0' ? f.align : (numeric ? '>' : '<');
    std::string padding(static_cast<std::size_t>(padLen), f.fill);
    if (align == '<') return body + padding;
    if (align == '>') return padding + body;
    if (align == '^') {
        std::string left(static_cast<std::size_t>(padLen / 2), f.fill);
        std::string right(static_cast<std::size_t>(padLen - padLen / 2), f.fill);
        return left + body + right;
    }
    // '=' : padding goes after a leading sign character.
    if (!body.empty() && (body[0] == '-' || body[0] == '+' || body[0] == ' '))
        return body.substr(0, 1) + padding + body.substr(1);
    return padding + body;
}

inline std::string format_value(py_int v, const FormatSpec& f) {
    std::string body;
    py_int mag = v < 0 ? -v : v;
    int base = 10;
    if (f.type == 'x' || f.type == 'X') base = 16;
    else if (f.type == 'o') base = 8;
    else if (f.type == 'b') base = 2;
    if (base == 10) body = std::to_string(mag);
    else {
        char buf[70]; auto res = std::to_chars(buf, buf + sizeof(buf), mag, base);
        body.assign(buf, res.ptr);
        if (f.type == 'X') for (auto& c : body) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
        if (f.alt) body = (f.type == 'x' ? "0x" : f.type == 'X' ? "0X" : f.type == 'o' ? "0o" : "0b") + body;
    }
    std::string signStr = v < 0 ? "-" : (f.sign == '+' ? "+" : f.sign == ' ' ? " " : "");
    return pad(signStr + body, f, /*numeric=*/true);
}

inline std::string format_value(py_float v, const FormatSpec& f) {
    int prec = f.has_precision ? f.precision : 6;
    char type = f.type != '\0' ? f.type : 'g';
    std::string body;
    if (type == 'f' || type == 'F') {
        std::ostringstream os; os.setf(std::ios::fixed); os.precision(prec); os << std::abs(v); body = os.str();
    } else if (type == 'e' || type == 'E') {
        std::ostringstream os; os.setf(std::ios::scientific); os.precision(prec); os << std::abs(v); body = os.str();
        if (type == 'E') for (auto& c : body) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    } else if (type == '%') {
        std::ostringstream os; os.setf(std::ios::fixed); os.precision(prec); os << std::abs(v) * 100.0; body = os.str() + "%";
    } else {
        body = to_str(std::abs(v));
    }
    std::string signStr = v < 0 ? "-" : (f.sign == '+' ? "+" : f.sign == ' ' ? " " : "");
    return pad(signStr + body, f, /*numeric=*/true);
}

inline std::string format_value(const Str& v, const FormatSpec& f) {
    std::string body = v.bytes();
    if (f.has_precision) body = v.slice(0, f.precision, std::nullopt).bytes();
    return pad(body, f, /*numeric=*/false);
}

inline std::string format_value(py_bool v, const FormatSpec& f) { return pad(to_str(v), f, false); }

template <typename T>
inline std::string format(const T& v, const std::string& spec) {
    if (spec.empty()) return to_str(v);
    return format_value(v, parse_format_spec(spec));
}

} // namespace pyrt

// std::hash specializations so pyrt::Str / pyrt::Dict / pyrt::Set can be
// used as unordered_map/unordered_set keys (dict keys, set elements).
namespace std {
template <> struct hash<pyrt::Str> {
    std::size_t operator()(const pyrt::Str& s) const noexcept { return std::hash<std::string>{}(s.bytes()); }
};
}
