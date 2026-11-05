// Mirrors my_unit_test_package/tests/test_async_ops.py, using Catch2
// instead of pytest + pytest-asyncio.
//
// Every `async def test_...` + `@pytest.mark.asyncio` becomes a plain
// TEST_CASE with no `co_await`/`async`, since lib_utils::async_ops
// collapses async to synchronous C++ (real std::thread where genuine
// concurrency mattered) -- see async_ops.cuh's file header for the full
// rationale.

#include <algorithm>
#include <atomic>
#include <chrono>
#include <functional>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "lib_utils/async_ops.cuh"

using namespace lib_utils::async_ops;

// -- gather_with_limit -------------------------------------------------

TEST_CASE("gather_with_limit returns results in order", "[async_ops]") {
    std::vector<std::function<int()>> tasks;
    for (int i = 0; i < 5; ++i) tasks.push_back([i] { return i * 2; });
    auto result = gather_with_limit<int>(tasks, 3);
    REQUIRE(result == std::vector<int>{0, 2, 4, 6, 8});
}

TEST_CASE("gather_with_limit respects concurrency cap", "[async_ops]") {
    std::atomic<int> active{0};
    std::atomic<int> peak{0};
    std::vector<std::function<int()>> tasks;
    for (int i = 0; i < 8; ++i) {
        tasks.push_back([&active, &peak] {
            int now = ++active;
            int prev_peak = peak.load();
            while (now > prev_peak && !peak.compare_exchange_weak(prev_peak, now)) {
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            --active;
            return 1;
        });
    }
    gather_with_limit<int>(tasks, 2);
    REQUIRE(peak.load() <= 2);
}

TEST_CASE("gather_with_limit zero max_concurrent throws", "[async_ops]") {
    std::vector<std::function<int()>> tasks;
    REQUIRE_THROWS_AS(gather_with_limit<int>(tasks, 0), std::invalid_argument);
}

// -- gather_settled ----------------------------------------------------

TEST_CASE("gather_settled all succeed", "[async_ops]") {
    std::vector<std::function<int()>> tasks;
    for (int i = 0; i < 3; ++i) tasks.push_back([i] { return i; });
    auto result = gather_settled<int>(tasks);
    REQUIRE(result.size() == 3);
    for (int i = 0; i < 3; ++i) REQUIRE(std::get<int>(result[static_cast<std::size_t>(i)]) == i);
}

TEST_CASE("gather_settled mixed results", "[async_ops]") {
    std::vector<std::function<int()>> tasks;
    tasks.push_back([] { return 1; });
    tasks.push_back([]() -> int { throw std::runtime_error("boom"); });
    tasks.push_back([] { return 1; });
    auto result = gather_settled<int>(tasks);
    REQUIRE(std::get<int>(result[0]) == 1);
    REQUIRE(result[1].index() == 1);  // holds an exception_ptr
    REQUIRE(std::get<int>(result[2]) == 1);
}

// -- with_timeout / timeout_scope ---------------------------------------

TEST_CASE("with_timeout completes when fast", "[async_ops]") {
    REQUIRE(with_timeout<std::string>([] { return std::string("done"); }, 1.0) == "done");
}

TEST_CASE("with_timeout expires when slow", "[async_ops]") {
    REQUIRE_THROWS_AS(with_timeout<int>(
                           [] {
                               std::this_thread::sleep_for(std::chrono::milliseconds(500));
                               return 1;
                           },
                           0.05),
                       TimeoutError);
}

TEST_CASE("timeout_scope runs body cleanly", "[async_ops]") {
    int result = timeout_scope(1.0, [] { return 42; });
    REQUIRE(result == 42);
}

// -- retry_async ---------------------------------------------------------

TEST_CASE("retry_async succeeds first try", "[async_ops]") {
    REQUIRE(retry_async(std::function<int()>([] { return 42; }), 3) == 42);
}

TEST_CASE("retry_async retries until success", "[async_ops]") {
    int attempts = 0;
    auto factory = [&attempts]() -> std::string {
        ++attempts;
        if (attempts < 3) throw std::runtime_error("transient");
        return "ok";
    };
    REQUIRE(retry_async(std::function<std::string()>(factory), 5, 0.0) == "ok");
    REQUIRE(attempts == 3);
}

TEST_CASE("retry_async exhausts attempts", "[async_ops]") {
    auto factory = []() -> int { throw std::runtime_error("permanent"); };
    bool threw_with_expected_message = false;
    try {
        retry_async(std::function<int()>(factory), 2, 0.0);
    } catch (const std::runtime_error& exc) {
        threw_with_expected_message = (std::string(exc.what()) == "permanent");
    }
    REQUIRE(threw_with_expected_message);
}

TEST_CASE("retry_async zero times throws", "[async_ops]") {
    REQUIRE_THROWS_AS(retry_async(std::function<int()>([] { return 1; }), 0), std::invalid_argument);
}

// -- AsyncRateLimiter ------------------------------------------------------

TEST_CASE("AsyncRateLimiter first acquire is immediate", "[async_ops]") {
    AsyncRateLimiter limiter(10.0);
    auto start = std::chrono::steady_clock::now();
    limiter.acquire();
    double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    REQUIRE(elapsed < 0.05);
}

TEST_CASE("AsyncRateLimiter second acquire waits", "[async_ops]") {
    AsyncRateLimiter limiter(20.0);  // 50 ms interval
    limiter.acquire();
    auto start = std::chrono::steady_clock::now();
    limiter.acquire();
    double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    REQUIRE(elapsed >= 0.03);
}

TEST_CASE("AsyncRateLimiter rejects non-positive rate", "[async_ops]") {
    REQUIRE_THROWS_AS(AsyncRateLimiter(0.0), std::invalid_argument);
}

// -- TaskGroup -------------------------------------------------------------

TEST_CASE("TaskGroup spawn and join", "[async_ops]") {
    TaskGroup tg;
    std::mutex mtx;
    std::vector<int> results;
    for (int i = 0; i < 5; ++i) {
        tg.spawn([i, &mtx, &results] {
            std::lock_guard<std::mutex> lock(mtx);
            results.push_back(i);
        });
    }
    tg.join();
    std::sort(results.begin(), results.end());
    REQUIRE(results == std::vector<int>{0, 1, 2, 3, 4});
}

TEST_CASE("TaskGroup active_count reflects pending tasks", "[async_ops]") {
    TaskGroup tg;
    std::atomic<bool> release{false};
    tg.spawn([&release] {
        while (!release.load()) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    });
    tg.spawn([&release] {
        while (!release.load()) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    REQUIRE(tg.active_count() == 2);
    release.store(true);
    tg.join();
    REQUIRE(tg.active_count() == 0);
}

TEST_CASE("TaskGroup cancel_all joins everything", "[async_ops]") {
    TaskGroup tg;
    tg.spawn([] { std::this_thread::sleep_for(std::chrono::milliseconds(5)); });
    tg.spawn([] { std::this_thread::sleep_for(std::chrono::milliseconds(5)); });
    tg.cancel_all();
    REQUIRE(tg.active_count() == 0);
}

// -- aclocked decorator -----------------------------------------------------

TEST_CASE("aclocked propagates return", "[async_ops]") {
    auto add = aclocked(std::function<int(int, int)>([](int a, int b) { return a + b; }));
    REQUIRE(add(1, 2) == 3);
}

TEST_CASE("aclocked calls sink", "[async_ops]") {
    std::vector<std::pair<std::string, double>> captured;
    auto hello = aclocked(std::function<std::string()>([] { return std::string("hi"); }), "hello",
                           [&captured](const std::string& name, double secs) { captured.emplace_back(name, secs); });
    hello();
    REQUIRE(captured.size() == 1);
    REQUIRE(captured[0].first == "hello");
    REQUIRE(captured[0].second >= 0.0);
}
