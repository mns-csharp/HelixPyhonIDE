#include "lib_core/events.cuh"

#include "lib_core/errors.cuh"

namespace lib_core::events {

int EventBus::dispatch(const Event& event) const {
    int count = 0;
    for (const auto& type : event.mro()) {
        auto it = handlers_.find(type);
        if (it == handlers_.end()) continue;
        for (const auto& entry : it->second) {
            auto resolved = entry.second->resolve();
            if (!resolved.has_value()) continue;  // subscriber died
            ++count;
            try {
                (*resolved)(event);
            } catch (const std::exception& exc) {
                throw lib_core::errors::HandlerError("handler", typeid(exc).name(), std::current_exception());
            }
        }
    }
    return count;
}

}  // namespace lib_core::events
