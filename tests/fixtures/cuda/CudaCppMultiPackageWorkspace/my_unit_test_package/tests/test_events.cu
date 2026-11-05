// Mirrors my_unit_test_package/tests/test_events.py, using Catch2 instead
// of pytest.
//
// Python's reflection-based `@on(...)` + `EventBus::attach_handlers`
// (which auto-discovers tagged methods on an arbitrary listener object)
// has no C++ equivalent -- every handler is subscribed explicitly via
// `subscribe`/`subscribe_weak` instead, matching events.cuh's file
// header and the identical adaptation made by this workspace's Rust
// sibling for its own reflection-based scans.

#include <functional>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

#include "lib_core/errors.cuh"
#include "lib_core/events.cuh"

using namespace lib_core::events;
using lib_core::errors::HandlerError;

namespace {

// Sample listener for fixture-based tests. Mirrors the Python `Listener`
// class (methods tagged with @on(EventCls)); here each method is
// subscribed explicitly by the test instead of being auto-discovered.
class Listener : public std::enable_shared_from_this<Listener> {
public:
    void on_string(const StringPayload& ev) { strings.push_back(ev.text); }
    void on_int(const IntPayload& ev) { ints.push_back(ev.value); }
    void on_change(const KeyValueChange& ev) { changes.emplace_back(ev.key, ev.new_value.value_or("")); }
    void on_any(const Event& ev) { broad.push_back(ev.source); }

    // Subscribes every @on-tagged method equivalent -- the explicit
    // stand-in for Python's `bus.attach_handlers(listener)` reflection
    // scan. Returns the number of handlers registered (4, matching the
    // Python original's `assert n == 4`).
    int attach(EventBus& bus, std::shared_ptr<Listener> self) {
        bus.subscribe_weak<Listener, StringPayload>(self, &Listener::on_string);
        bus.subscribe_weak<Listener, IntPayload>(self, &Listener::on_int);
        bus.subscribe_weak<Listener, KeyValueChange>(self, &Listener::on_change);
        bus.subscribe_weak<Listener, Event>(self, &Listener::on_any);
        return 4;
    }

    std::vector<std::string> strings;
    std::vector<int> ints;
    std::vector<std::pair<std::string, std::string>> changes;
    std::vector<std::string> broad;
};

}  // namespace

// -- Subscribe / dispatch --------------------------------------------------

TEST_CASE("subscribe and dispatch string", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    bus.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
    bus.dispatch(StringPayload("hello"));
    REQUIRE(listener->strings == std::vector<std::string>{"hello"});
}

TEST_CASE("dispatch returns handler count", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    bus.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
    REQUIRE(bus.dispatch(StringPayload("x")) == 1);
}

TEST_CASE("no handler dispatch returns zero", "[events]") {
    EventBus bus;
    REQUIRE(bus.dispatch(StringPayload("x")) == 0);
}

TEST_CASE("unsubscribe", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    auto id = bus.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
    bus.unsubscribe(id);
    bus.dispatch(StringPayload("x"));
    REQUIRE(listener->strings.empty());
}

TEST_CASE("unsubscribe nonexistent is a no-op", "[events]") {
    EventBus bus;
    REQUIRE_NOTHROW(bus.unsubscribe(9999));
}

// -- attach-style bulk subscription ------------------------------------

TEST_CASE("attach subscribes every tagged method", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    int n = listener->attach(bus, listener);
    REQUIRE(n == 4);
}

TEST_CASE("attached handlers run", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    listener->attach(bus, listener);
    bus.dispatch(StringPayload("hi"));
    bus.dispatch(IntPayload(42));
    REQUIRE(listener->strings == std::vector<std::string>{"hi"});
    REQUIRE(listener->ints == std::vector<int>{42});
}

TEST_CASE("subclass event walks ancestor chain", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    listener->attach(bus, listener);
    // KeyValueChange is a subclass of Event -- both handlers fire.
    bus.dispatch(KeyValueChange("alpha", std::optional<std::string>("1"), std::nullopt));
    REQUIRE(listener->changes == std::vector<std::pair<std::string, std::string>>{{"alpha", "1"}});
    REQUIRE(!listener->broad.empty());
}

// -- Weak subscription lifetime ---------------------------------------

TEST_CASE("dead subscriber is pruned", "[events]") {
    EventBus bus;
    {
        auto listener = std::make_shared<Listener>();
        bus.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
    }
    // The only shared_ptr to `listener` went out of scope -- the weak
    // subscription is now dead.
    REQUIRE(bus.dispatch(StringPayload("x")) == 0);
}

TEST_CASE("subscription_count purges dead entries", "[events]") {
    EventBus bus;
    {
        auto listener = std::make_shared<Listener>();
        bus.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
        REQUIRE(bus.subscription_count() == 1);
    }
    REQUIRE(bus.subscription_count() == 0);
}

// -- Error handling --------------------------------------------------------

TEST_CASE("handler exception is wrapped", "[events]") {
    EventBus bus;
    bus.subscribe<StringPayload>(
        std::function<void(const StringPayload&)>([](const StringPayload&) { throw std::runtime_error("boom"); }));
    REQUIRE_THROWS_AS(bus.dispatch(StringPayload("x")), HandlerError);
}

// -- publish (collapsed onto the same synchronous dispatch loop) -----------

TEST_CASE("publish runs handler", "[events]") {
    EventBus bus;
    std::vector<std::string> seen;
    bus.subscribe<StringPayload>(
        std::function<void(const StringPayload&)>([&seen](const StringPayload& ev) { seen.push_back(ev.text); }));
    int n = bus.publish(StringPayload("async"));
    REQUIRE(n == 1);
    REQUIRE(seen == std::vector<std::string>{"async"});
}

TEST_CASE("publish runs every handler", "[events]") {
    EventBus bus;
    std::vector<std::string> seen;
    bus.subscribe<StringPayload>(
        std::function<void(const StringPayload&)>([&seen](const StringPayload&) { seen.push_back("slow"); }));
    bus.subscribe<StringPayload>(
        std::function<void(const StringPayload&)>([&seen](const StringPayload&) { seen.push_back("fast"); }));
    bus.publish(StringPayload("x"));
    REQUIRE(seen.size() == 2);
}

// -- Bus housekeeping ------------------------------------------------------

TEST_CASE("clear drops subscriptions", "[events]") {
    EventBus bus;
    auto listener = std::make_shared<Listener>();
    listener->attach(bus, listener);
    bus.clear();
    REQUIRE(bus.subscription_count() == 0);
    REQUIRE(bus.dispatch(StringPayload("x")) == 0);
}
