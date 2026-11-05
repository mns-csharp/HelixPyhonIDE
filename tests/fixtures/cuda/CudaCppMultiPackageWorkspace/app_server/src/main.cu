// Server-style demo: router + middleware chain + storage + animals.
//
// Mirrors app_server/app_server_main.py (the package's real entry point;
// app_server_main.py / app_server_main_2.py are both tiny `python -m
// app_server` stubs that just call `main()` -- collapsed onto this file's
// single `int main()`, same reasoning as app_cli/src/main.cu). Every
// `async def` in the Python original is synchronous here, per this
// workspace's translation policy (see lib_core/animals.cuh); genuine
// concurrency (the animal-feeding TaskGroup) is preserved via real
// std::thread through lib_utils::async_ops::TaskGroup.

#include <chrono>
#include <iostream>
#include <vector>

#include "app_server/middleware.cuh"
#include "app_server/routes.cuh"
#include "lib_core/animals.cuh"
#include "lib_core/events.cuh"
#include "lib_core/storage.cuh"
#include "lib_utils/async_ops.cuh"
#include "lib_utils/logger.cuh"

using namespace lib_core::animals;
using namespace lib_core::events;
using namespace lib_core::storage;
using namespace lib_utils::async_ops;
using namespace lib_utils::logger;
using namespace app_server::routes;
using namespace app_server::middleware;

namespace {

// -- Legacy sync demo -------------------------------------------------------

void legacy_demo() {
    Logger log = Logger::from_config("server");
    InMemoryStorage store;

    Dog rex("rex");
    PoliceDog k9("k9", 42);

    store.set("house_pet", std::string("Dog(rex)"));
    store.set("on_duty", std::string("PoliceDog(k9)"));

    log.info("registered pet: house_pet");
    log.info("on-duty says: " + k9.speak());
    log.warn("end of demo");
}

// -- Async server demo (collapsed to synchronous) ---------------------------

std::vector<std::pair<int, std::string>> serve_demo_requests(int request_count = 6) {
    auto router = make_default_router();
    auto chain = default_chain(router, {"dev-token"}, 20.0,
                                std::make_shared<StructuredLogger>("server", Level::Info));

    auto issue = [&chain](const Request& req) { return chain.handle(req); };

    std::vector<Request> requests{
        Request{"GET", "/health", {}, ""},
        Request{"GET", "/version", {}, ""},
        Request{"GET", "/users", {{"authorization", "Bearer dev-token"}}, ""},
        Request{"GET", "/users/1", {{"authorization", "Bearer dev-token"}}, ""},
        Request{"POST", "/users", {{"authorization", "Bearer dev-token"}}, "Charlie"},
        Request{"GET", "/flakey", {{"authorization", "Bearer dev-token"}}, ""},
    };
    if (request_count < static_cast<int>(requests.size())) requests.resize(request_count);

    std::vector<std::function<std::pair<int, std::string>()>> tasks;
    for (const auto& req : requests) {
        tasks.push_back([issue, req]() {
            Response response = issue(req);
            return std::make_pair(response.status, response.body);
        });
    }
    return gather_with_limit<std::pair<int, std::string>>(tasks, 2);
}

// -- Animal demo (real threads for genuine concurrency) --------------------

void animal_demo() {
    Habitat<Wolf> pack("North Ridge", 3);
    pack.admit(Wolf("Akela"));
    pack.admit(Wolf("Raksha", "North Ridge"));
    pack.admit(Wolf("Grey"));

    Habitat<Eagle> aviary("Cliff Hide", 2);
    aviary.admit(Eagle("Tarkus"));

    Habitat<Snake> snakes("Reptile House", 4);
    snakes.admit(Snake("Kaa"));
    snakes.admit(Snake("Nag", true));

    StructuredLogger log("zoo");
    log.info("pack=" + std::to_string(pack.size()) + ", aviary=" + std::to_string(aviary.size()) +
              ", snakes=" + std::to_string(snakes.size()));

    // Concurrent feeding via real OS threads.
    TaskGroup tg;
    for (const auto& animal : pack) tg.spawn([&animal] { feed(animal, 200); });
    for (const auto& animal : aviary) tg.spawn([&animal] { feed(animal, 120); });
    for (const auto& animal : snakes) tg.spawn([&animal] { feed(animal, 80); });
    tg.join();

    // Chorus.
    std::vector<const Animal*> everyone;
    for (const auto& a : pack) everyone.push_back(&a);
    for (const auto& a : aviary) everyone.push_back(&a);
    for (const auto& a : snakes) everyone.push_back(&a);
    auto sounds = chorus(everyone);
    std::string joined;
    for (const auto& s : sounds) {
        if (!joined.empty()) joined += ", ";
        joined += s;
    }
    log.info("chorus: " + joined);

    // Sleep them all.
    for (const auto* a : everyone) {
        log.info(sleep_animal(*a, 8.0));
    }
}

// -- Storage + events demo --------------------------------------------------

void storage_demo() {
    InMemoryTypedStorage<std::string, std::string> storage;
    EventBus bus;
    StructuredLogger log("store");
    std::vector<std::string> seen;

    bus.subscribe<KeyValueChange>(std::function<void(const KeyValueChange&)>(
        [&seen](const KeyValueChange& ev) { seen.push_back(ev.key + "=" + ev.new_value.value_or("")); }));

    storage.set("alpha", "1");
    bus.dispatch(KeyValueChange("alpha", std::optional<std::string>("1"), std::nullopt, "store"));
    storage.set("beta", "2");
    bus.dispatch(KeyValueChange("beta", std::optional<std::string>("2"), std::nullopt, "store"));

    std::string joined;
    for (const auto& s : seen) {
        if (!joined.empty()) joined += ", ";
        joined += s;
    }
    log.info("recorded changes: " + joined);
}

}  // namespace

int main() {
    std::cout << "== legacy demo ==\n";
    legacy_demo();

    std::cout << "\n== async server demo ==\n";
    auto started = std::chrono::steady_clock::now();
    auto responses = serve_demo_requests();
    double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count() * 1000.0;
    for (std::size_t i = 0; i < responses.size(); ++i) {
        std::cout << "  [" << i << "] " << responses[i].first << " " << responses[i].second << "\n";
    }
    std::cout << "  total " << elapsed << " ms\n";

    std::cout << "\n== async animal demo ==\n";
    animal_demo();

    std::cout << "\n== storage + events demo ==\n";
    storage_demo();

    return 0;
}
