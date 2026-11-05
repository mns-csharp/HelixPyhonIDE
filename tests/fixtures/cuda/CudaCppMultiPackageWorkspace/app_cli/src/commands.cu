#include "app_cli/commands.cuh"

#include <type_traits>

// commands.cuh is header-only (every method is defined in-class); this
// translation unit exists so the app_cli target has a concrete .cu source
// mirroring commands.py.

namespace app_cli::commands {

static_assert(std::is_base_of_v<Command, AddShape>);
static_assert(std::is_base_of_v<Command, RemoveShape>);
static_assert(std::is_base_of_v<Command, Transform>);
static_assert(std::is_base_of_v<Command, Persist>);
static_assert(std::is_base_of_v<Command, Replay>);

}  // namespace app_cli::commands
