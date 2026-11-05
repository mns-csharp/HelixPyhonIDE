"""Reference functions spanning distinct asymptotic-complexity classes, used to
exercise HelixStudio.Complexity's heuristic classifier end-to-end."""


def get_first(items):
    """O(1): no loop, no recursion."""
    return items[0] if items else None


def binary_search(items, target):
    """O(log n): the search window halves every iteration."""
    lo = 0
    hi = len(items) - 1
    while lo <= hi:
        mid = (lo + hi) // 2
        if items[mid] == target:
            return mid
        elif items[mid] < target:
            lo = mid + 1
        else:
            hi = mid - 1
    return -1


def linear_search(items, target):
    """O(n) worst case, but a conditional early exit gives it an O(1) best case."""
    for i in range(len(items)):
        if items[i] == target:
            return i
    return -1


def sum_all(items):
    """O(n): a single non-nested loop over the input, no early exit."""
    total = 0
    for x in items:
        total += x
    return total


def has_duplicate_pair(items):
    """O(n^2): two nested loops, each ~n iterations."""
    n = len(items)
    for i in range(n):
        for j in range(n):
            if i != j and items[i] == items[j]:
                return True
    return False


def factorial(n):
    """O(n): linear recursion, input reduced by a constant each call."""
    if n <= 1:
        return 1
    return n * factorial(n - 1)


def binary_search_recursive(items, target, lo, hi):
    """O(log n): a single recursive call on a halved input range."""
    if lo > hi:
        return -1
    mid = (lo + hi) // 2
    if items[mid] == target:
        return mid
    if items[mid] < target:
        return binary_search_recursive(items, target, mid + 1, hi)
    return binary_search_recursive(items, target, lo, mid - 1)


def merge_sort(items):
    """O(n log n): two recursive calls on halved input plus linear merge work."""
    if len(items) <= 1:
        return items
    mid = len(items) // 2
    left = merge_sort(items[:mid])
    right = merge_sort(items[mid:])
    merged = []
    for x in left:
        merged.append(x)
    for x in right:
        merged.append(x)
    return merged


def fib_naive(n):
    """O(2^n): two recursive calls, no halving pattern (naive Fibonacci)."""
    if n <= 1:
        return n
    return fib_naive(n - 1) + fib_naive(n - 2)
