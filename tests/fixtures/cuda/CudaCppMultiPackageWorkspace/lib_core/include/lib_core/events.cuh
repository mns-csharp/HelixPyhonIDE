#pragma once

// Event-bus subsystem: typed events, subscriber registry, dispatch.
//
// Mirrors lib_core/events.py. Translation notes:
//   - Python's single dataclass-inheritance chain (Event -> StringPayload
//     etc.) becomes a REAL C++ class hierarchy (`class StringPayload :
//     public Event`), and `EventBus::dispatch` walks each event's ancestor
//     chain via a small virtual `mro()` method that returns the
//     std::type_index of every class from the concrete type up to Event
//     -- the direct analogue of walking `type(event).__mro__`.
//   - Python's automatic "is this a bound method?" reflection
//     (`hasattr(handler, "__self__")`) that decides weak-vs-strong storage
//     has no C++ equivalent, so `subscribe` is split into two explicit
//     overloads: a strong `std::function<void(const EV&)>` overload for
//     free functions/lambdas, and a `subscribe_weak(std::weak_ptr<T>,
//     method-pointer)` overload for the weak-bound-method case --
//     preserving the exact "subscriber lifetime tracks the owning object"
//     behaviour the Python docstring describes, just with the
//     weak-vs-strong choice made explicit at the call site instead of
//     inferred at runtime.
//   - Python's `unsubscribe(event_type, handler)` compares by handler
//     identity/equality, which `std::function` cannot do in general; both
//     subscribe overloads instead return an opaque `SubscriptionId` token
//     that `unsubscribe` takes directly -- a standard, and strictly more
//     robust, C++ idiom for the same "cancel this specific subscription"
//     intent.
//   - `EventBus::attach_handlers` used Python reflection (`dir(container)`
//     + a `_helix_event_type` tag stashed by the `@on(...)` decorator) to
//     auto-discover tagged methods on an arbitrary object. C++ has no
//     runtime reflection over a class's methods, so there is no
//     `attach_handlers` here; call sites subscribe each handler
//     explicitly instead (the same adaptation the Rust sibling made for
//     its reflection-based `RouterTable::attach_handlers`).
//   - `async def publish` (which awaited async handlers concurrently)
//     collapses onto the same synchronous dispatch loop as `dispatch`,
//     per this workspace's "represent async as synchronous" policy (see
//     animals.cuh); it is kept as a separate method for call-site parity
//     with the Python API.

#include <algorithm>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <typeindex>
#include <unordered_map>
#include <vector>

namespace lib_core::events {

// -- Event types -------------------------------------------------------

// Base event class. All real events inherit from this.
class Event {
public:
    explicit Event(std::string source = "", double timestamp = 0.0)
        : source(std::move(source)), timestamp(timestamp) {}
    virtual ~Event() = default;

    // Every ancestor's type_index, concrete-most first, mirroring
    // `type(event).__mro__` (restricted to the Event-derived portion of
    // the chain -- Python's full MRO also includes `object`, which no
    // handler could usefully subscribe to).
    virtual std::vector<std::type_index> mro() const { return {std::type_index(typeid(Event))}; }

    std::string source;
    double timestamp;
};

// An event that carries a plain string payload.
class StringPayload : public Event {
public:
    StringPayload(std::string text, std::string source = "", double timestamp = 0.0)
        : Event(std::move(source), timestamp), text(std::move(text)) {}

    std::vector<std::type_index> mro() const override {
        return {std::type_index(typeid(StringPayload)), std::type_index(typeid(Event))};
    }

    std::string text;
};

// An event that carries a single integer.
class IntPayload : public Event {
public:
    explicit IntPayload(int value, std::string source = "", double timestamp = 0.0)
        : Event(std::move(source), timestamp), value(value) {}

    std::vector<std::type_index> mro() const override {
        return {std::type_index(typeid(IntPayload)), std::type_index(typeid(Event))};
    }

    int value;
};

// A storage backend changed: a key was set or deleted.
class KeyValueChange : public Event {
public:
    KeyValueChange(std::string key, std::optional<std::string> new_value, std::optional<std::string> old_value,
                    std::string source = "", double timestamp = 0.0)
        : Event(std::move(source), timestamp),
          key(std::move(key)),
          new_value(std::move(new_value)),
          old_value(std::move(old_value)) {}

    std::vector<std::type_index> mro() const override {
        return {std::type_index(typeid(KeyValueChange)), std::type_index(typeid(Event))};
    }

    std::string key;
    std::optional<std::string> new_value;
    std::optional<std::string> old_value;
};

// Something raised -- published by HandlerError-aware code.
class ErrorRaised : public Event {
public:
    ErrorRaised(std::string error_code, std::string message, std::string source = "", double timestamp = 0.0)
        : Event(std::move(source), timestamp), error_code(std::move(error_code)), message(std::move(message)) {}

    std::vector<std::type_index> mro() const override {
        return {std::type_index(typeid(ErrorRaised)), std::type_index(typeid(Event))};
    }

    std::string error_code;
    std::string message;
};

// -- Bus implementation -----------------------------------------------------

using SubscriptionId = std::size_t;

namespace detail {

// Type-erased subscription slot. Strong slots always resolve; weak slots
// resolve to nullopt once the owning object has been destroyed --
// mirroring Python's weakref.WeakMethod semantics.
class Subscription {
public:
    virtual ~Subscription() = default;
    virtual std::optional<std::function<void(const Event&)>> resolve() const = 0;
    virtual bool is_alive() const = 0;
};

template <typename EV>
class StrongSubscription : public Subscription {
public:
    explicit StrongSubscription(std::function<void(const EV&)> fn) : fn_(std::move(fn)) {}

    std::optional<std::function<void(const Event&)>> resolve() const override {
        auto fn = fn_;
        return std::function<void(const Event&)>([fn](const Event& ev) { fn(static_cast<const EV&>(ev)); });
    }

    bool is_alive() const override { return true; }

private:
    std::function<void(const EV&)> fn_;
};

template <typename T, typename EV>
class WeakMethodSubscription : public Subscription {
public:
    WeakMethodSubscription(std::weak_ptr<T> obj, void (T::*method)(const EV&)) : obj_(obj), method_(method) {}

    std::optional<std::function<void(const Event&)>> resolve() const override {
        auto locked = obj_.lock();
        if (!locked) return std::nullopt;
        auto method = method_;
        return std::function<void(const Event&)>(
            [locked, method](const Event& ev) { (locked.get()->*method)(static_cast<const EV&>(ev)); });
    }

    bool is_alive() const override { return !obj_.expired(); }

private:
    std::weak_ptr<T> obj_;
    void (T::*method_)(const EV&);
};

}  // namespace detail

// In-process event bus. `dispatch` runs synchronously; `publish` mirrors
// the same synchronous loop (see file header re: async collapse).
//
// Handlers are kept in a list per event type. When the bus dispatches an
// event of concrete type E, it walks `E::mro()` and invokes every handler
// registered against any ancestor -- so a single handler registered
// against `Event` sees every subclass too.
class EventBus {
public:
    // Register a strong (free-function/lambda) handler for events of
    // type EV. Returns a token usable with unsubscribe().
    template <typename EV>
    SubscriptionId subscribe(std::function<void(const EV&)> handler) {
        return subscribe_impl(std::type_index(typeid(EV)), std::make_unique<detail::StrongSubscription<EV>>(std::move(handler)));
    }

    // Register a weak bound-method handler: `obj` is held by weak_ptr, so
    // once every shared_ptr to it is gone the subscription silently
    // stops firing (and is pruned from subscription_count()).
    template <typename T, typename EV>
    SubscriptionId subscribe_weak(std::weak_ptr<T> obj, void (T::*method)(const EV&)) {
        return subscribe_impl(std::type_index(typeid(EV)),
                               std::make_unique<detail::WeakMethodSubscription<T, EV>>(obj, method));
    }

    // Remove a previously-subscribed handler by token. No-op if unknown.
    void unsubscribe(SubscriptionId id) {
        for (auto& [type, bucket] : handlers_) {
            bucket.erase(std::remove_if(bucket.begin(), bucket.end(),
                                         [id](const auto& entry) { return entry.first == id; }),
                         bucket.end());
        }
    }

    // Invoke every matching handler. Returns the number of handlers
    // invoked. Wraps any exception a handler throws in a HandlerError,
    // just like the Python original.
    int dispatch(const Event& event) const;

    // Async sibling in Python; synchronous here (see file header).
    int publish(const Event& event) const { return dispatch(event); }

    // Drop every subscription.
    void clear() { handlers_.clear(); }

    // Total number of live subscriptions (purges dead weak entries).
    std::size_t subscription_count() const {
        std::size_t n = 0;
        for (const auto& [type, bucket] : handlers_) {
            for (const auto& entry : bucket) {
                if (entry.second->is_alive()) ++n;
            }
        }
        return n;
    }

private:
    SubscriptionId subscribe_impl(std::type_index type, std::unique_ptr<detail::Subscription> sub) {
        SubscriptionId id = next_id_++;
        handlers_[type].emplace_back(id, std::move(sub));
        return id;
    }

    std::unordered_map<std::type_index, std::vector<std::pair<SubscriptionId, std::unique_ptr<detail::Subscription>>>>
        handlers_;
    SubscriptionId next_id_ = 1;
};

}  // namespace lib_core::events
