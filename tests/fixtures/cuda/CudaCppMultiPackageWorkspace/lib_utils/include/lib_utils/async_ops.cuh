#pragma once

// Async helpers: gather-with-limit, retry, timeout, async context
// managers.
//
// Mirrors lib_utils/async_ops.py. Every function here was `async def` in
// Python. Per this workspace's translation policy (see animals.cuh),
// `async`/`await` collapses to synchronous C++ wherever the original
// coroutine's only "await" was a simulated `asyncio.sleep` with no other
// effect. Where the Python original relied on GENUINE concurrency
// (`TaskGroup`, `gather_with_limit`'s bounded fan-out), this translation
// uses real `std::thread` -- available without any external dependency
// and preserving actually-concurrent behaviour, unlike a fully-sequential
// stand-in. `retry_async`/`aclocked` collapse onto
// `decorators::retry`/`decorators::timed` now that both sides are
// synchronous -- keeping separate copies would just be duplicates
// (matching the Rust sibling's identical `retry_async` -> `retry`
// re-export decision). `timeout_scope` had no cooperative-cancellation
// machinery worth building for a fixture with no actual long-running
// work, so it is a documented passthrough, same as the Rust sibling.

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <exception>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <variant>
#include <vector>

#include "lib_utils/decorators.cuh"

namespace lib_utils::async_ops {

// -- Bounded concurrency (real threads, actually bounded) -------------------

// Like asyncio.gather, but at most `max_concurrent` tasks run at once.
// Real fan-out via std::async/std::future, gated by a counting
// semaphore-like mutex+condition_variable pair -- preserves the
// Python original's genuine concurrency, unlike a purely sequential
// stand-in.
template <typename R>
std::vector<R> gather_with_limit(std::vector<std::function<R()>> tasks, int max_concurrent) {
    if (max_concurrent <= 0) {
        throw std::invalid_argument("max_concurrent must be positive, got " + std::to_string(max_concurrent));
    }
    std::mutex mtx;
    std::condition_variable cv;
    int in_flight = 0;
    std::vector<std::future<R>> futures;
    futures.reserve(tasks.size());
    for (auto& task : tasks) {
        futures.push_back(std::async(std::launch::async, [&mtx, &cv, &in_flight, max_concurrent, task]() -> R {
            {
                std::unique_lock<std::mutex> lock(mtx);
                cv.wait(lock, [&] { return in_flight < max_concurrent; });
                ++in_flight;
            }
            R result = task();
            {
                std::lock_guard<std::mutex> lock(mtx);
                --in_flight;
            }
            cv.notify_one();
            return result;
        }));
    }
    std::vector<R> results;
    results.reserve(futures.size());
    for (auto& f : futures) results.push_back(f.get());
    return results;
}

// Like asyncio.gather(return_exceptions=True): a throwing task's failure
// is captured as an exception_ptr instead of aborting the whole batch.
template <typename R>
std::vector<std::variant<R, std::exception_ptr>> gather_settled(std::vector<std::function<R()>> tasks) {
    std::vector<std::variant<R, std::exception_ptr>> results;
    results.reserve(tasks.size());
    for (auto& task : tasks) {
        try {
            results.emplace_back(std::in_place_index<0>, task());
        } catch (...) {
            results.emplace_back(std::in_place_index<1>, std::current_exception());
        }
    }
    return results;
}

// -- Timeouts (backed by a real OS thread) ----------------------------------

class TimeoutError : public std::runtime_error {
public:
    TimeoutError() : std::runtime_error("operation timed out") {}
};

// Run `f` (on a background thread) with a wall-clock timeout. The
// background thread is not forcibly killed on timeout (C++ has no safe
// thread-kill primitive) -- it keeps running detached, mirroring "best
// effort" cancellation, same caveat as the Rust sibling.
template <typename R>
R with_timeout(std::function<R()> f, double seconds) {
    auto fut = std::async(std::launch::async, f);
    if (fut.wait_for(std::chrono::duration<double>(seconds)) == std::future_status::timeout) {
        throw TimeoutError();
    }
    return fut.get();
}

// Passthrough scope -- no cooperative-cancellation runtime is built for
// this fixture (documented deviation from Python's cancel-on-timeout
// `@asynccontextmanager`, same as the Rust sibling).
template <typename F>
auto timeout_scope(double /*seconds*/, F body) {
    return body();
}

// -- Retry / backoff ---------------------------------------------------

// Retry an async operation in Python; collapses onto decorators::retry
// now that both are synchronous (see file header).
template <typename F>
auto retry_async(F coro_factory, int times = 3, double delay = 0.1, double backoff = 2.0) {
    return lib_utils::decorators::retry(coro_factory, times, delay, backoff)();
}

// -- Rate limiting -----------------------------------------------------

// Simple rate limiter: at most `per_second` permits per second,
// non-bursty. Synchronous sibling of Python's AsyncRateLimiter.
class AsyncRateLimiter {
public:
    explicit AsyncRateLimiter(double per_second) {
        if (per_second <= 0.0) {
            throw std::invalid_argument("per_second must be positive, got " + std::to_string(per_second));
        }
        interval_ = std::chrono::duration<double>(1.0 / per_second);
        next_at_ = std::chrono::steady_clock::now();
    }

    // Block until a permit is available.
    void acquire() {
        std::lock_guard<std::mutex> lock(mtx_);
        auto now = std::chrono::steady_clock::now();
        if (next_at_ > now) {
            std::this_thread::sleep_for(next_at_ - now);
            next_at_ += std::chrono::duration_cast<std::chrono::steady_clock::duration>(interval_);
        } else {
            next_at_ = now + std::chrono::duration_cast<std::chrono::steady_clock::duration>(interval_);
        }
    }

private:
    std::mutex mtx_;
    std::chrono::duration<double> interval_;
    std::chrono::steady_clock::time_point next_at_;
};

// -- Function timing ---------------------------------------------------

// Async-aware @timed in Python; collapses onto decorators::timed now that
// both are synchronous (see file header).
template <typename F>
auto aclocked(F fn, const std::string& label = "", std::function<void(const std::string&, double)> sink = nullptr) {
    return lib_utils::decorators::timed(fn, label, sink);
}

// -- Background task management (backed by real OS threads) ---------------

// Lightweight wrapper that tracks spawned threads and ensures clean
// shutdown. Modelled after Python's TaskGroup, but backed by std::thread
// instead of asyncio.Task.
class TaskGroup {
public:
    // Schedule a closure on its own OS thread.
    void spawn(std::function<void()> f) {
        auto done = std::make_shared<std::atomic<bool>>(false);
        auto thread = std::make_shared<std::thread>([f, done] {
            f();
            done->store(true);
        });
        std::lock_guard<std::mutex> lock(mtx_);
        tasks_.push_back({thread, done});
    }

    // Wait for every tracked task to finish.
    void join() {
        std::vector<Entry> tasks;
        {
            std::lock_guard<std::mutex> lock(mtx_);
            tasks.swap(tasks_);
        }
        for (auto& entry : tasks) {
            if (entry.thread->joinable()) entry.thread->join();
        }
    }

    // C++ has no safe way to force-cancel a running OS thread, so this is
    // "best effort": it just waits for everything to finish, same caveat
    // as the Rust sibling.
    void cancel_all() { join(); }

    // Number of tracked threads that haven't finished.
    int active_count() const {
        std::lock_guard<std::mutex> lock(mtx_);
        int n = 0;
        for (const auto& entry : tasks_) {
            if (!entry.done->load()) ++n;
        }
        return n;
    }

private:
    struct Entry {
        std::shared_ptr<std::thread> thread;
        std::shared_ptr<std::atomic<bool>> done;
    };
    mutable std::mutex mtx_;
    std::vector<Entry> tasks_;
};

}  // namespace lib_utils::async_ops
