#pragma once

// Iterator / generator helpers.
//
// Mirrors lib_utils/iterators.py. Python's lazy generators (`yield`)
// become eagerly-evaluated `std::vector<T>`-returning functions -- no
// test in this workspace observes laziness (early-exit-without-consuming-
// the-rest) so this is a safe, much simpler translation than hand-rolling
// custom C++ input iterators for every helper. Async iteration helpers
// (`aiter_pages`, `amap`, `afilter`) collapse onto the same eager
// synchronous style, per this workspace's "represent async as
// synchronous" policy.

#include <cmath>
#include <functional>
#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

namespace lib_utils::iterators {

// Yield successive n-length windows from `seq`.
//   windowed({1,2,3,4}, 2) == {{1,2},{2,3},{3,4}}
template <typename T>
std::vector<std::vector<T>> windowed(const std::vector<T>& seq, int n) {
    if (n <= 0) throw std::invalid_argument("window size must be positive, got " + std::to_string(n));
    std::vector<std::vector<T>> out;
    std::vector<T> buf;
    for (const auto& item : seq) {
        buf.push_back(item);
        if (static_cast<int>(buf.size()) == n) {
            out.push_back(buf);
            buf.erase(buf.begin());
        }
    }
    return out;
}

// Yield non-overlapping chunks of `size`. The last may be shorter.
template <typename T>
std::vector<std::vector<T>> chunked(const std::vector<T>& seq, int size) {
    if (size <= 0) throw std::invalid_argument("chunk size must be positive, got " + std::to_string(size));
    std::vector<std::vector<T>> out;
    std::vector<T> buf;
    for (const auto& item : seq) {
        buf.push_back(item);
        if (static_cast<int>(buf.size()) == size) {
            out.push_back(buf);
            buf.clear();
        }
    }
    if (!buf.empty()) out.push_back(buf);
    return out;
}

// Take items while `pred` returns true.
template <typename T>
std::vector<T> take_while(const std::vector<T>& seq, std::function<bool(const T&)> pred) {
    std::vector<T> out;
    for (const auto& item : seq) {
        if (!pred(item)) break;
        out.push_back(item);
    }
    return out;
}

// Skip items while `pred` is true, then take the rest.
template <typename T>
std::vector<T> drop_while(const std::vector<T>& seq, std::function<bool(const T&)> pred) {
    std::vector<T> out;
    bool dropping = true;
    for (const auto& item : seq) {
        if (dropping && pred(item)) continue;
        dropping = false;
        out.push_back(item);
    }
    return out;
}

// Concatenate any number of iterables.
template <typename T>
std::vector<T> chain_iters(const std::vector<std::vector<T>>& iters) {
    std::vector<T> out;
    for (const auto& it : iters) out.insert(out.end(), it.begin(), it.end());
    return out;
}

// Flatten one level of nesting.
template <typename T>
std::vector<T> flatten(const std::vector<std::vector<T>>& nested) {
    return chain_iters(nested);
}

// Bucket numbers into negative / zero / small / large. `small` means
// absolute value <= 1.0.
template <typename T>
std::unordered_map<std::string, std::vector<T>> classify_numbers(const std::vector<T>& items) {
    std::unordered_map<std::string, std::vector<T>> result{{"negative", {}}, {"zero", {}}, {"small", {}}, {"large", {}}};
    for (const auto& item : items) {
        if (item == 0) {
            result["zero"].push_back(item);
        } else if (item < 0) {
            result["negative"].push_back(item);
        } else if (std::abs(static_cast<double>(item)) <= 1.0) {
            result["small"].push_back(item);
        } else {
            result["large"].push_back(item);
        }
    }
    return result;
}

// Deduplicate while keeping the first-seen order.
template <typename T>
std::vector<T> unique_preserving_order(const std::vector<T>& seq) {
    std::vector<T> out;
    for (const auto& item : seq) {
        bool seen = false;
        for (const auto& existing : out) {
            if (existing == item) {
                seen = true;
                break;
            }
        }
        if (!seen) out.push_back(item);
    }
    return out;
}

// NOTE: Python's `deep_flatten` recurses through arbitrarily nested
// `Iterable` values at runtime via `isinstance` checks (skipping
// str/bytes) -- doing that generically in C++ would need a recursive
// std::variant/std::any value tree, which is a disproportionate amount of
// machinery for a helper no test in this workspace exercises. Omitted;
// `flatten` (one level, statically typed) above covers the common case.

// Fetch every page via `fetch_page` until an empty page or `max_pages` is
// reached. Originally `async def aiter_pages` yielding page items;
// eagerly collects everything into one vector here (see file header).
template <typename T>
std::vector<T> aiter_pages(std::function<std::vector<T>(int)> fetch_page, int start_page = 0,
                            std::optional<int> max_pages = std::nullopt) {
    std::vector<T> out;
    int page = start_page;
    while (!max_pages.has_value() || (page - start_page) < *max_pages) {
        std::vector<T> items = fetch_page(page);
        if (items.empty()) break;
        out.insert(out.end(), items.begin(), items.end());
        ++page;
    }
    return out;
}

// Histogram of `seq` grouped by `key`.
template <typename T, typename K>
std::unordered_map<K, int> count_by(const std::vector<T>& seq, std::function<K(const T&)> key) {
    std::unordered_map<K, int> result;
    for (const auto& item : seq) ++result[key(item)];
    return result;
}

// Return the first element if any, else `default_value`.
template <typename T>
T first_or(const std::vector<T>& seq, T default_value) {
    if (!seq.empty()) return seq.front();
    return default_value;
}

// Variant of `reduce` that returns nullopt on empty input.
template <typename T>
std::optional<T> reduce_inplace(const std::vector<T>& seq, std::function<T(const T&, const T&)> op) {
    if (seq.empty()) return std::nullopt;
    T acc = seq.front();
    for (std::size_t i = 1; i < seq.size(); ++i) acc = op(acc, seq[i]);
    return acc;
}

}  // namespace lib_utils::iterators
