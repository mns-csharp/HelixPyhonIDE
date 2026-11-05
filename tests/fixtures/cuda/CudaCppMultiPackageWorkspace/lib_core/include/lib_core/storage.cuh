#pragma once

// Storage Protocol with several backends.
//
// Mirrors lib_core/storage.py. Translation notes:
//   - `Storage`/`TypedStorage`/`AsyncStorage` Protocols -> C++20 concepts
//     (structural, matching Protocol's duck-typing exactly). `AsyncStorage`
//     collapses onto the same synchronous concept as the others, per this
//     workspace's "represent async as synchronous" policy.
//   - `InMemoryStorage` stores Python's untyped `object` values as
//     `std::any` -- the direct analogue of "any value at all".
//   - `FileStorage` is JSON-backed in Python. Pulling in a JSON library
//     is out of scope for this fixture (no test exercises the on-disk
//     format; only `InMemoryStorage`/`InMemoryTypedStorage` are covered by
//     the ported test suite), so the on-disk format here is a minimal
//     `key\tvalue` line format instead of JSON -- everything else
//     (RAII-scoped flush-on-exit, validate-then-write-through semantics)
//     is preserved faithfully.
//   - `@contextmanager transaction()` -> a method taking a callable;
//     the body runs under an undo log and any exception it throws
//     triggers rollback-then-rethrow, exactly like the Python
//     `try/except BaseException: ... raise`.

#include <any>
#include <concepts>
#include <fstream>
#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

#include "lib_core/errors.cuh"

namespace lib_core::storage {

// -- Legacy dict-backed store (kept so behaviour mirrors test_sample.py) ---

// Dict-backed Storage -- kept for backwards compatibility. Python's
// untyped `dict[str, object]` becomes `unordered_map<string, std::any>`,
// the direct analogue of "any value at all". For new code prefer
// `InMemoryTypedStorage<K, V>` which carries the element types through
// the IDE's type checker.
class InMemoryStorage {
public:
    std::optional<std::any> get(const std::string& key) const {
        auto it = data_.find(key);
        if (it == data_.end()) return std::nullopt;
        return it->second;
    }

    void set(const std::string& key, std::any value) { data_[key] = std::move(value); }

    void remove(const std::string& key) { data_.erase(key); }

private:
    std::unordered_map<std::string, std::any> data_;
};

// -- Protocols (structural, via C++20 concepts) -----------------------------

// Simple key-value store protocol (kept compatible with the original).
template <typename S>
concept Storage = requires(S s, const std::string& key) {
    { s.get(key) };
    { s.set(key, s.get(key)) };
    { s.remove(key) } -> std::same_as<void>;
};

// Strongly-typed version of Storage, parameterized by K and V.
template <typename S, typename K, typename V>
concept TypedStorage = requires(S s, K key, V value) {
    { s.get(key) } -> std::convertible_to<V>;
    { s.set(key, value) };
    { s.remove(key) } -> std::same_as<void>;
    { s.contains(key) } -> std::convertible_to<bool>;
    { s.size() } -> std::convertible_to<std::size_t>;
};

// -- Generic in-memory store -------------------------------------------------

// Generic in-memory store. Reports missing keys via KeyMissingError.
//
// `capacity`: optional soft cap; throws CapacityError on exceeding it.
// `nullopt` means no cap. Python's optional runtime `value_type` check is
// unnecessary here -- V is already enforced at compile time.
template <typename K, typename V>
class InMemoryTypedStorage {
public:
    explicit InMemoryTypedStorage(std::optional<std::size_t> capacity = std::nullopt) : capacity_(capacity) {}

    V get(const K& key) const {
        auto it = data_.find(key);
        if (it == data_.end()) {
            throw lib_core::errors::KeyMissingError(key_to_string(key));
        }
        return it->second;
    }

    void set(const K& key, V value) {
        if (capacity_.has_value() && !data_.contains(key) && data_.size() >= *capacity_) {
            throw lib_core::errors::CapacityError(static_cast<int>(*capacity_), static_cast<int>(data_.size()));
        }
        data_[key] = std::move(value);
    }

    void remove(const K& key) { data_.erase(key); }

    bool contains(const K& key) const { return data_.contains(key); }

    std::size_t size() const { return data_.size(); }

    // Snapshot of current keys.
    std::vector<K> keys() const {
        std::vector<K> out;
        out.reserve(data_.size());
        for (const auto& [k, v] : data_) out.push_back(k);
        return out;
    }

private:
    static std::string key_to_string(const K& key) {
        if constexpr (std::convertible_to<K, std::string>) {
            return key;
        } else {
            return "<key>";
        }
    }

    std::unordered_map<K, V> data_;
    std::optional<std::size_t> capacity_;
};

// -- File-backed store (simplified line format; see file header) -----------

// String-keyed, string-valued persistent store. Works as an RAII scope
// (`FileStorage::Scope`) so the file handle's lifetime is owned by a
// block, mirroring Python's `with FileStorage(path) as store:`. Outside a
// scope, `set` writes through immediately.
class FileStorage {
public:
    explicit FileStorage(std::string path) : path_(std::move(path)) { load_existing(); }

    class Scope {
    public:
        explicit Scope(FileStorage& owner) : owner_(owner) { owner_.in_context_ = true; }
        ~Scope() {
            owner_.in_context_ = false;
            owner_.flush();
        }
        FileStorage& operator*() { return owner_; }

    private:
        FileStorage& owner_;
    };

    Scope scope() { return Scope(*this); }

    std::string get(const std::string& key) const {
        auto it = data_.find(key);
        if (it == data_.end()) {
            throw lib_core::errors::KeyMissingError(key);
        }
        return it->second;
    }

    void set(const std::string& key, std::string value) {
        data_[key] = std::move(value);
        dirty_ = true;
        if (!in_context_) flush();
    }

    void remove(const std::string& key) {
        if (data_.erase(key) > 0) {
            dirty_ = true;
            if (!in_context_) flush();
        }
    }

    // Write pending changes to disk.
    void flush() {
        if (!dirty_) return;
        std::ofstream out(path_, std::ios::trunc);
        if (!out) {
            throw lib_core::errors::StorageError("flush failed for " + path_);
        }
        for (const auto& [k, v] : data_) {
            out << k << '\t' << v << '\n';
        }
        dirty_ = false;
    }

    std::vector<std::string> keys() const {
        std::vector<std::string> out;
        out.reserve(data_.size());
        for (const auto& [k, v] : data_) out.push_back(k);
        return out;
    }

private:
    void load_existing() {
        std::ifstream in(path_);
        if (!in) return;
        std::string line;
        while (std::getline(in, line)) {
            auto tab = line.find('\t');
            if (tab == std::string::npos) continue;
            data_[line.substr(0, tab)] = line.substr(tab + 1);
        }
    }

    std::string path_;
    std::unordered_map<std::string, std::string> data_;
    bool dirty_ = false;
    bool in_context_ = false;
};

// -- Layered / transactional wrappers ---------------------------------------

// Layered store -- reads cascade through `layers` until one hits. Writes
// go to the first layer only.
template <typename K, typename V>
class ChainedStorage {
public:
    explicit ChainedStorage(std::vector<InMemoryTypedStorage<K, V>*> layers) : layers_(std::move(layers)) {
        if (layers_.empty()) {
            throw std::invalid_argument("ChainedStorage needs at least one layer");
        }
    }

    V get(const K& key) const {
        for (auto* layer : layers_) {
            if (layer->contains(key)) return layer->get(key);
        }
        throw lib_core::errors::KeyMissingError("<chained>");
    }

    void set(const K& key, V value) { layers_.front()->set(key, std::move(value)); }

    void remove(const K& key) {
        for (auto* layer : layers_) layer->remove(key);
    }

    bool contains(const K& key) const {
        for (auto* layer : layers_) {
            if (layer->contains(key)) return true;
        }
        return false;
    }

private:
    std::vector<InMemoryTypedStorage<K, V>*> layers_;
};

// Wraps an InMemoryTypedStorage with a transactional undo log. Inside
// `transaction(body)`, mutations are tracked; if `body` throws, every
// mutation made within the block is reverted before the exception
// propagates.
template <typename K, typename V>
class TransactionalStorage {
public:
    explicit TransactionalStorage(InMemoryTypedStorage<K, V> inner) : inner_(std::move(inner)) {}

    V get(const K& key) const { return inner_.get(key); }

    void set(const K& key, V value) {
        if (undo_.has_value()) {
            std::optional<V> prev;
            if (inner_.contains(key)) prev = inner_.get(key);
            undo_->push_back({UndoAction::Set, key, prev});
        }
        inner_.set(key, std::move(value));
    }

    void remove(const K& key) {
        if (undo_.has_value()) {
            if (!inner_.contains(key)) return;
            undo_->push_back({UndoAction::Remove, key, inner_.get(key)});
        }
        inner_.remove(key);
    }

    // Run `body(*this)` under an undo log; rolls back on exception.
    template <typename F>
    void transaction(F body) {
        if (undo_.has_value()) {
            throw lib_core::errors::IntegrityError("nested transactions are not supported");
        }
        undo_ = std::vector<UndoEntry>{};
        try {
            body(*this);
        } catch (...) {
            for (auto it = undo_->rbegin(); it != undo_->rend(); ++it) {
                if (it->action == UndoAction::Set) {
                    if (it->previous.has_value()) {
                        inner_.set(it->key, *it->previous);
                    } else {
                        inner_.remove(it->key);
                    }
                } else if (it->action == UndoAction::Remove) {
                    if (it->previous.has_value()) {
                        inner_.set(it->key, *it->previous);
                    }
                }
            }
            undo_.reset();
            throw;
        }
        undo_.reset();
    }

private:
    enum class UndoAction { Set, Remove };
    struct UndoEntry {
        UndoAction action;
        K key;
        std::optional<V> previous;
    };

    InMemoryTypedStorage<K, V> inner_;
    std::optional<std::vector<UndoEntry>> undo_;
};

}  // namespace lib_core::storage
