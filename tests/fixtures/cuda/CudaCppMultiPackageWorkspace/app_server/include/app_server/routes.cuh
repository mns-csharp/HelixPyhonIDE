#pragma once

// Toy HTTP-shaped router. No real network I/O -- just dispatch.
//
// Mirrors app_server/routes.py. Translation notes:
//   - Route handlers were `async def` in Python; per this workspace's
//     "represent async as synchronous" policy (see lib_core/animals.cuh)
//     they are plain functions here.
//   - The `@route(...)` decorator + `RouterTable.attach_handlers`
//     reflection scan (which walks an arbitrary object's methods looking
//     for a stashed route-spec attribute) has no C++ equivalent -- C++
//     has no runtime introspection over a class's methods. `make_default_
//     router` instead registers each handler explicitly via
//     `RouterTable::add`, the same adaptation the Rust sibling made for
//     its identical reflection-based scan.

#include <algorithm>
#include <functional>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include "lib_core/errors.cuh"
#include "lib_utils/async_ops.cuh"

namespace app_server::routes {

// -- Request / Response models -----------------------------------------

// Inbound request -- immutable so handlers can't accidentally mutate it.
struct Request {
    std::string method;
    std::string path;
    std::unordered_map<std::string, std::string> headers;
    std::string body;

    // "/users/42/orders" -> {"users", "42", "orders"}.
    std::vector<std::string> path_segments() const {
        std::vector<std::string> out;
        std::string current;
        for (char c : path) {
            if (c == '/') {
                if (!current.empty()) out.push_back(current);
                current.clear();
            } else {
                current += c;
            }
        }
        if (!current.empty()) out.push_back(current);
        return out;
    }
};

// Outbound response.
struct Response {
    int status;
    std::unordered_map<std::string, std::string> headers;
    std::string body;

    static Response ok(const std::string& text = "") {
        return Response{200, {{"content-type", "text/plain"}}, text};
    }
    static Response not_found(const std::string& message = "not found") {
        return Response{404, {{"content-type", "text/plain"}}, message};
    }
    static Response server_error(const std::string& message = "internal error") {
        return Response{500, {{"content-type", "text/plain"}}, message};
    }
};

// -- Route declaration -----------------------------------------------------

using HandlerFn = std::function<Response(const Request&)>;

// How a handler is registered: pattern + allowed methods + priority.
struct RouteSpec {
    std::string path;
    std::vector<std::string> methods;
    int priority = 0;
};

// -- Router ----------------------------------------------------------------

// Maps (method, path) to a handler. Highest-priority match wins.
class RouterTable {
public:
    // Register `handler` under `spec`. Kept sorted by descending priority.
    void add(RouteSpec spec, HandlerFn handler) {
        routes_.emplace_back(std::move(spec), std::move(handler));
        std::stable_sort(routes_.begin(), routes_.end(),
                          [](const auto& a, const auto& b) { return a.first.priority > b.first.priority; });
    }

    // Return the highest-priority handler whose pattern matches.
    const HandlerFn* find(const Request& request) const {
        for (const auto& [spec, handler] : routes_) {
            bool method_ok = false;
            for (const auto& m : spec.methods) {
                if (m == request.method) {
                    method_ok = true;
                    break;
                }
            }
            if (!method_ok) continue;
            if (matches(spec.path, request.path)) return &handler;
        }
        return nullptr;
    }

    // Look up and invoke the handler. 404 on no match, 500 on throw.
    Response dispatch(const Request& request) const {
        const HandlerFn* handler = find(request);
        if (handler == nullptr) {
            return Response::not_found("no route for " + request.method + " " + request.path);
        }
        try {
            return (*handler)(request);
        } catch (const lib_core::errors::RetryableError& exc) {
            return Response{503, {{"retry-after", std::to_string(static_cast<int>(std::max(0.0, exc.retry_after)))}},
                             exc.what()};
        } catch (const std::exception& exc) {
            return Response::server_error(std::string(typeid(exc).name()) + ": " + exc.what());
        }
    }

    std::size_t route_count() const { return routes_.size(); }

private:
    static bool matches(const std::string& pattern, const std::string& path) {
        auto split = [](const std::string& s) {
            std::vector<std::string> segs;
            std::string cur;
            for (char c : s) {
                if (c == '/') {
                    if (!cur.empty()) segs.push_back(cur);
                    cur.clear();
                } else {
                    cur += c;
                }
            }
            if (!cur.empty()) segs.push_back(cur);
            return segs;
        };
        auto pat_segs = split(pattern);
        auto req_segs = split(path);
        if (pat_segs.size() != req_segs.size()) return false;
        for (std::size_t i = 0; i < pat_segs.size(); ++i) {
            if (!pat_segs[i].empty() && pat_segs[i][0] == ':') continue;
            if (pat_segs[i] != req_segs[i]) return false;
        }
        return true;
    }

    std::vector<std::pair<RouteSpec, HandlerFn>> routes_;
};

// Build a router with the demo user/health/flakey handlers registered.
std::shared_ptr<RouterTable> make_default_router();

}  // namespace app_server::routes
