#pragma once

// Reference functions spanning distinct asymptotic-complexity classes,
// used to exercise HelixStudio.Complexity's heuristic classifier end to
// end. Mirrors lib_utils/complexity_demo.py 1:1 -- every function keeps
// its Python name and complexity class exactly.

#include <cstddef>
#include <vector>

namespace lib_utils::complexity_demo {

// O(1): no loop, no recursion.
inline int get_first(const std::vector<int>& items) { return items.empty() ? -1 : items.front(); }

// O(log n): the search window halves every iteration.
inline int binary_search(const std::vector<int>& items, int target) {
    int lo = 0;
    int hi = static_cast<int>(items.size()) - 1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (items[mid] == target) return mid;
        if (items[mid] < target) {
            lo = mid + 1;
        } else {
            hi = mid - 1;
        }
    }
    return -1;
}

// O(n) worst case, but a conditional early exit gives it an O(1) best case.
inline int linear_search(const std::vector<int>& items, int target) {
    for (std::size_t i = 0; i < items.size(); ++i) {
        if (items[i] == target) return static_cast<int>(i);
    }
    return -1;
}

// O(n): a single non-nested loop over the input, no early exit.
inline int sum_all(const std::vector<int>& items) {
    int total = 0;
    for (int x : items) total += x;
    return total;
}

// O(n^2): two nested loops, each ~n iterations.
inline bool has_duplicate_pair(const std::vector<int>& items) {
    std::size_t n = items.size();
    for (std::size_t i = 0; i < n; ++i) {
        for (std::size_t j = 0; j < n; ++j) {
            if (i != j && items[i] == items[j]) return true;
        }
    }
    return false;
}

// O(n): linear recursion, input reduced by a constant each call.
inline long long factorial(long long n) {
    if (n <= 1) return 1;
    return n * factorial(n - 1);
}

// O(log n): a single recursive call on a halved input range.
inline int binary_search_recursive(const std::vector<int>& items, int target, int lo, int hi) {
    if (lo > hi) return -1;
    int mid = (lo + hi) / 2;
    if (items[mid] == target) return mid;
    if (items[mid] < target) return binary_search_recursive(items, target, mid + 1, hi);
    return binary_search_recursive(items, target, lo, mid - 1);
}

// O(n log n): two recursive calls on halved input plus linear merge work.
inline std::vector<int> merge_sort(const std::vector<int>& items) {
    if (items.size() <= 1) return items;
    std::size_t mid = items.size() / 2;
    std::vector<int> left(items.begin(), items.begin() + mid);
    std::vector<int> right(items.begin() + mid, items.end());
    left = merge_sort(left);
    right = merge_sort(right);
    std::vector<int> merged;
    merged.reserve(items.size());
    for (int x : left) merged.push_back(x);
    for (int x : right) merged.push_back(x);
    return merged;
}

// O(2^n): two recursive calls, no halving pattern (naive Fibonacci).
inline long long fib_naive(long long n) {
    if (n <= 1) return n;
    return fib_naive(n - 1) + fib_naive(n - 2);
}

}  // namespace lib_utils::complexity_demo
