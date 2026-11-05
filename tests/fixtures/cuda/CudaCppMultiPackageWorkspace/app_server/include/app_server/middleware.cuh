#pragma once

// Middleware chain for the demo server.
//
// Mirrors app_server/middleware.py. Every middleware's `__call__` was
// `async def` in Python (chaining via `await next_(request)`); per this
// workspace's "represent async as synchronous" policy, `Next` here is a
// plain `std::function<Response(const Request&)>` instead of
// `Callable[[Request], Awaitable[Response]]`. The `Middleware` Protocol
// becomes a C++20 concept (structural, matching Protocol's duck typing).

#include <chrono>
#include <concepts>
#include <functional>
#include <memory>
#include <set>
#include <string>
#include <unordered_set>
#include <vector>

#include "app_server/routes.cuh"
#include "lib_utils/async_ops.cuh"
#include "lib_utils/logger.cuh"

namespace app_server::middleware {

using app_server::routes::Request;
using app_server::routes::Response;
using app_server::routes::RouterTable;

using Next = std::function<Response(const Request&)>;

// Anything that can sit between a Request and a handler: inspect the
// request, call `next_`, optionally inspect/modify the response.
template <typename M>
concept Middleware = requires(M m, const Request& request, const Next& next) {
    { m(request, next) } -> std::convertible_to<Response>;
};

// Log every request + response status.
class LoggingMiddleware {
public:
    explicit LoggingMiddleware(std::shared_ptr<lib_utils::logger::StructuredLogger> logger)
        : logger_(std::move(logger)) {}

    Response operator()(const Request& request, const Next& next) const {
        auto start = std::chrono::steady_clock::now();
        logger_->info("--> " + request.method + " " + request.path);
        Response response = next(request);
        double elapsed_ms = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count() * 1000.0;
        logger_->info("<-- " + std::to_string(response.status) + " (" + std::to_string(elapsed_ms) + " ms)");
        return response;
    }

private:
    std::shared_ptr<lib_utils::logger::StructuredLogger> logger_;
};

// Reject requests without a valid bearer token in the `authorization`
// header. `exempt_paths` bypass the check entirely.
class AuthMiddleware {
public:
    AuthMiddleware(std::unordered_set<std::string> valid_tokens, std::unordered_set<std::string> exempt_paths = {})
        : valid_tokens_(std::move(valid_tokens)), exempt_paths_(std::move(exempt_paths)) {}

    Response operator()(const Request& request, const Next& next) const {
        if (exempt_paths_.contains(request.path)) return next(request);
        auto it = request.headers.find("authorization");
        std::string header = it != request.headers.end() ? it->second : "";
        const std::string prefix = "Bearer ";
        if (header.rfind(prefix, 0) != 0) {
            return Response{401, {}, "missing or malformed authorization header"};
        }
        std::string token = header.substr(prefix.size());
        if (!valid_tokens_.contains(token)) {
            return Response{403, {}, "forbidden"};
        }
        return next(request);
    }

private:
    std::unordered_set<std::string> valid_tokens_;
    std::unordered_set<std::string> exempt_paths_;
};

// Reject requests once the global rate limit is exceeded.
//
// Holds the limiter behind a shared_ptr rather than by value: AsyncRateLimiter
// owns a std::mutex (needed for its real "block until a permit is free"
// behaviour), which makes it non-copyable -- but MiddlewareChain::use()
// stores middlewares in a std::function, and std::function requires its
// wrapped callable to be copy-constructible. A shared_ptr indirection
// keeps the middleware itself cheaply copyable while every copy still
// shares (and correctly serializes access to) the same limiter state.
class RateLimitMiddleware {
public:
    explicit RateLimitMiddleware(double per_second)
        : limiter_(std::make_shared<lib_utils::async_ops::AsyncRateLimiter>(per_second)) {}

    Response operator()(const Request& request, const Next& next) const {
        limiter_->acquire();
        return next(request);
    }

private:
    std::shared_ptr<lib_utils::async_ops::AsyncRateLimiter> limiter_;
};

// Inject a CORS allow-origin header on every response.
class CORSMiddleware {
public:
    explicit CORSMiddleware(std::string allow_origin = "*") : allow_origin_(std::move(allow_origin)) {}

    Response operator()(const Request& request, const Next& next) const {
        Response response = next(request);
        response.headers["access-control-allow-origin"] = allow_origin_;
        return response;
    }

private:
    std::string allow_origin_;
};

// Stamp a `server-timing` header on every response.
class TimingMiddleware {
public:
    Response operator()(const Request& request, const Next& next) const {
        auto start = std::chrono::steady_clock::now();
        Response response = next(request);
        double elapsed_ms = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count() * 1000.0;
        response.headers["server-timing"] = "app;dur=" + std::to_string(elapsed_ms);
        return response;
    }
};

// -- Chain -----------------------------------------------------------------

// Composes middlewares around a router. Outermost first.
class MiddlewareChain {
public:
    explicit MiddlewareChain(std::shared_ptr<RouterTable> router) : router_(std::move(router)) {}

    // Append `middleware`. Returns self so calls chain.
    MiddlewareChain& use(std::function<Response(const Request&, const Next&)> middleware) {
        middlewares_.push_back(std::move(middleware));
        return *this;
    }

    // Build and invoke in one shot. Convenient for tests.
    Response handle(const Request& request) const {
        Next terminal = [router = router_](const Request& req) { return router->dispatch(req); };
        // Wrap from innermost to outermost so the first use() ends up on
        // the outside.
        Next chain = terminal;
        for (auto it = middlewares_.rbegin(); it != middlewares_.rend(); ++it) {
            auto mw = *it;
            Next downstream = chain;
            chain = [mw, downstream](const Request& req) { return mw(req, downstream); };
        }
        return chain(request);
    }

private:
    std::shared_ptr<RouterTable> router_;
    std::vector<std::function<Response(const Request&, const Next&)>> middlewares_;
};

// Build a chain with a sensible default stack.
inline MiddlewareChain default_chain(std::shared_ptr<RouterTable> router,
                                      std::unordered_set<std::string> auth_tokens = {"dev-token"},
                                      double rate_per_second = 100.0,
                                      std::shared_ptr<lib_utils::logger::StructuredLogger> logger = nullptr) {
    MiddlewareChain chain(std::move(router));
    auto log = logger ? logger : std::make_shared<lib_utils::logger::StructuredLogger>("server");
    chain.use(LoggingMiddleware(log));
    chain.use(TimingMiddleware());
    chain.use(CORSMiddleware());
    chain.use(RateLimitMiddleware(rate_per_second));
    chain.use(AuthMiddleware(std::move(auth_tokens), {"/health", "/version"}));
    return chain;
}

}  // namespace app_server::middleware
