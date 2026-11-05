#include "app_server/routes.cuh"

#include <memory>

using lib_core::errors::ConfigurationError;
using lib_core::errors::RetryableError;

namespace app_server::routes {

std::shared_ptr<RouterTable> make_default_router() {
    auto router = std::make_shared<RouterTable>();

    // -- UserRoutes: a bag of user-related routes. -----------------------
    auto users = std::make_shared<std::unordered_map<std::string, std::string>>(
        std::unordered_map<std::string, std::string>{{"1", "Alice"}, {"2", "Bob"}});

    router->add(RouteSpec{"/users", {"GET"}, 0}, [users](const Request&) -> Response {
        std::string names;
        for (const auto& [id, name] : *users) {
            if (!names.empty()) names += ", ";
            names += name;
        }
        return Response::ok("users: " + names);
    });

    router->add(RouteSpec{"/users/:id", {"GET"}, 0}, [users](const Request& req) -> Response {
        auto segs = req.path_segments();
        std::string id = segs.empty() ? "" : segs.back();
        auto it = users->find(id);
        if (it == users->end()) return Response::not_found("no user " + id);
        return Response::ok("user: " + it->second);
    });

    router->add(RouteSpec{"/users/:id", {"DELETE"}, 0}, [users](const Request& req) -> Response {
        auto segs = req.path_segments();
        std::string id = segs.empty() ? "" : segs.back();
        auto it = users->find(id);
        if (it == users->end()) return Response::not_found("no user " + id);
        users->erase(it);
        return Response::ok("deleted " + id);
    });

    router->add(RouteSpec{"/users", {"POST"}, 0}, [users](const Request& req) -> Response {
        std::string new_id = std::to_string(users->size() + 1);
        (*users)[new_id] = req.body;
        return Response::ok("created " + new_id);
    });

    router->add(RouteSpec{"/users/by-name/:name", {"GET"}, 0}, [users](const Request& req) -> Response {
        auto segs = req.path_segments();
        std::string target = segs.empty() ? "" : segs.back();
        for (const auto& [id, name] : *users) {
            if (name == target) return Response::ok("user: " + name);
        }
        return Response::not_found("no user named " + target);
    });

    // -- HealthRoutes: health and version probes. -------------------------
    router->add(RouteSpec{"/health", {"GET"}, 10},
                [](const Request&) -> Response { return Response::ok("OK"); });
    router->add(RouteSpec{"/version", {"GET"}, 0},
                [](const Request&) -> Response { return Response::ok("HelixStudioDemo/0.1.0"); });

    // -- FlakeyRoutes: demonstrates RetryableError handling. --------------
    auto tries = std::make_shared<int>(0);
    router->add(RouteSpec{"/flakey", {"GET"}, 0}, [tries](const Request&) -> Response {
        auto attempt = [tries]() -> std::string {
            ++(*tries);
            if (*tries < 3) {
                throw RetryableError("warming up", 0.05);
            }
            return "ready after " + std::to_string(*tries) + " tries";
        };
        std::string body = lib_utils::async_ops::retry_async(attempt, 4, 0.01);
        return Response::ok(body);
    });

    if (router->route_count() < 3) {
        throw ConfigurationError("UserRoutes: no handlers attached");
    }
    return router;
}

}  // namespace app_server::routes
