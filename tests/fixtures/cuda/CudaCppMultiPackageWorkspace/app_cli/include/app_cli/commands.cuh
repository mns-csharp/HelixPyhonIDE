#pragma once

// Command pattern for the CLI: typed command payloads + dispatcher.
//
// Mirrors app_cli/commands.py. Translation notes:
//   - Every concrete command subclasses the abstract `Command`, exactly
//     like Python.
//   - Python's `CommandResult[T]` is generic per command, but
//     `Command.execute` returns `CommandResult[Any]` in the ABC
//     signature -- so the *virtual* return type must already be uniform.
//     `CommandValue` is a small tagged union covering every concrete
//     value a command in this file actually returns (unit / int / a
//     shape / a string), the direct analogue of Python's untyped `Any`
//     at the ABC boundary.
//   - Shapes stored on the `Context` canvas are `shared_ptr<IShape>`
//     rather than owned values: Python passes the *same* shape object
//     into a payload and keeps a reference to it afterwards (e.g. the
//     demo reuses `triangle` after handing it to `AddShapePayload`), so
//     shared ownership is the direct analogue, not a copy.
//   - `CommandBus`'s string-keyed dynamic dispatch (`bus.execute(name,
//     payload, ctx)`) is preserved as-is (unlike the Rust sibling, which
//     dropped it because Rust has no `Any`-typed payload to route
//     through one entry point) -- C++ can express "payload of any
//     concrete type" via `std::any`, so the dispatcher stays faithful to
//     the Python original.

#include <algorithm>
#include <any>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

#include "lib_core/errors.cuh"
#include "lib_core/shapes.cuh"
#include "lib_utils/geometry.cuh"



namespace app_cli::commands {

using lib_core::errors::ConfigurationError;
using lib_core::errors::HelixError;
using lib_core::shapes::IShape;
using lib_core::shapes::Point3D;
using lib_core::shapes::Polygon;
using ShapePtr = std::shared_ptr<IShape>;

// -- Payload types ---------------------------------------------------------

// Add a single shape to the canvas.
struct AddShapePayload {
    ShapePtr shape;
    std::string layer = "default";
};

// Remove a shape by its index in the active layer.
struct RemoveShapePayload {
    int index;
    std::string layer = "default";
};

// Apply a 2-D matrix transform to a polygon at `index`.
struct TransformPayload {
    int index;
    lib_utils::geometry::Matrix2x3 matrix;
    std::string layer = "default";
};

// Persist the current canvas to a file path.
struct PersistPayload {
    std::string path;
    bool overwrite = false;
};

// -- Normalised, type-erased result value -----------------------------------

// Tagged union covering every concrete value a command in this file
// returns -- the direct analogue of `CommandResult[Any]` at the
// `Command::execute` ABC boundary (see file header).
struct CommandValue {
    enum class Kind { Unit, Int, Shape, String } kind = Kind::Unit;
    int int_value = 0;
    ShapePtr shape_value;
    std::string string_value;

    static CommandValue unit() { return CommandValue{}; }
    static CommandValue from_int(int v) {
        CommandValue c;
        c.kind = Kind::Int;
        c.int_value = v;
        return c;
    }
    static CommandValue from_shape(ShapePtr v) {
        CommandValue c;
        c.kind = Kind::Shape;
        c.shape_value = std::move(v);
        return c;
    }
    static CommandValue from_string(std::string v) {
        CommandValue c;
        c.kind = Kind::String;
        c.string_value = std::move(v);
        return c;
    }
};

// Normalised result envelope. Keeps success/error info adjacent.
struct CommandResult {
    bool ok = false;
    CommandValue value;
    std::optional<HelixError> error;
    double duration_ms = 0.0;

    static CommandResult success(CommandValue v) { return CommandResult{true, std::move(v), std::nullopt, 0.0}; }
    static CommandResult failure(HelixError e) { return CommandResult{false, CommandValue::unit(), std::move(e), 0.0}; }
};

// -- Command ABC -----------------------------------------------------------

// Mutable state passed through the command chain.
//
// `canvas` maps layer-name -> list-of-shapes; commands operate against
// named layers so undo/replay can target a specific layer without
// fighting the others.
class Context;

// Base class for every command.
class Command {
public:
    virtual ~Command() = default;

    // Run the command against `ctx`. Implementations should not throw --
    // they should wrap failures in CommandResult::failure(...).
    virtual CommandResult execute(Context& ctx) = 0;

    virtual const std::string& name() const = 0;
};

class Context {
public:
    std::vector<ShapePtr>& get_layer(const std::string& name) { return canvas[name]; }

    std::unordered_map<std::string, std::vector<ShapePtr>> canvas;
    std::vector<std::shared_ptr<Command>> history;
    std::unordered_map<std::string, std::string> metadata;
};

// -- Concrete commands -----------------------------------------------------

// Add a shape to a layer.
class AddShape : public Command {
public:
    explicit AddShape(AddShapePayload payload) : payload_(std::move(payload)) {}

    CommandResult execute(Context& ctx) override {
        auto& layer = ctx.get_layer(payload_.layer);
        layer.push_back(payload_.shape);
        return CommandResult::success(CommandValue::from_int(static_cast<int>(layer.size()) - 1));
    }

    const std::string& name() const override {
        static const std::string kName = "add_shape";
        return kName;
    }

private:
    AddShapePayload payload_;
};

// Remove a shape by index.
class RemoveShape : public Command {
public:
    explicit RemoveShape(RemoveShapePayload payload) : payload_(std::move(payload)) {}

    CommandResult execute(Context& ctx) override {
        auto& layer = ctx.get_layer(payload_.layer);
        if (payload_.index < 0 || payload_.index >= static_cast<int>(layer.size())) {
            return CommandResult::failure(HelixError(
                "index " + std::to_string(payload_.index) + " out of range for layer '" + payload_.layer +
                    "' (size " + std::to_string(layer.size()) + ")",
                "INDEX_OUT_OF_RANGE"));
        }
        ShapePtr removed = layer[payload_.index];
        layer.erase(layer.begin() + payload_.index);
        return CommandResult::success(CommandValue::from_shape(removed));
    }

    const std::string& name() const override {
        static const std::string kName = "remove_shape";
        return kName;
    }

private:
    RemoveShapePayload payload_;
};

// Apply a 2-D matrix transform to a polygon at `index`.
class Transform : public Command {
public:
    explicit Transform(TransformPayload payload) : payload_(std::move(payload)) {}

    CommandResult execute(Context& ctx) override {
        auto& layer = ctx.get_layer(payload_.layer);
        if (payload_.index < 0 || payload_.index >= static_cast<int>(layer.size())) {
            return CommandResult::failure(HelixError("index out of range", "INDEX_OUT_OF_RANGE"));
        }
        auto* poly = dynamic_cast<Polygon*>(layer[payload_.index].get());
        if (poly == nullptr) {
            return CommandResult::failure(
                HelixError("transform requires a Polygon", "TRANSFORM_TYPE"));
        }
        auto new_shape = std::make_shared<Polygon>(lib_utils::geometry::transform_polygon(*poly, payload_.matrix));
        layer[payload_.index] = new_shape;
        return CommandResult::success(CommandValue::from_shape(new_shape));
    }

    const std::string& name() const override {
        static const std::string kName = "transform";
        return kName;
    }

private:
    TransformPayload payload_;
};

// Persist the canvas -- stub implementation that just records intent.
class Persist : public Command {
public:
    explicit Persist(PersistPayload payload) : payload_(std::move(payload)) {}

    CommandResult execute(Context& ctx) override {
        ctx.metadata["persist_path"] = payload_.path;
        ctx.metadata["persist_overwrite"] = payload_.overwrite ? "true" : "false";
        return CommandResult::success(CommandValue::from_string(payload_.path));
    }

    const std::string& name() const override {
        static const std::string kName = "persist";
        return kName;
    }

private:
    PersistPayload payload_;
};

// Replay a recorded sequence of commands.
class Replay : public Command {
public:
    explicit Replay(std::vector<std::shared_ptr<Command>> commands) : commands_(std::move(commands)) {}

    CommandResult execute(Context& ctx) override {
        int executed = 0;
        for (auto& cmd : commands_) {
            CommandResult result = cmd->execute(ctx);
            if (!result.ok) {
                CommandResult out = CommandResult::failure(*result.error);
                out.value = CommandValue::from_int(executed);
                return out;
            }
            ++executed;
        }
        return CommandResult::success(CommandValue::from_int(executed));
    }

    const std::string& name() const override {
        static const std::string kName = "replay";
        return kName;
    }

private:
    std::vector<std::shared_ptr<Command>> commands_;
};

// -- Command bus / dispatcher ------------------------------------------------

// Dispatcher that routes commands by name. A bus knows how to construct
// a command from a name + type-erased payload (std::any, the analogue of
// Python's untyped `Any` payload) via a registered factory, and can
// execute the resulting command against a shared context.
class CommandBus {
public:
    using Factory = std::function<std::shared_ptr<Command>(const std::any&)>;

    void register_command(const std::string& name, Factory factory) {
        if (factories_.contains(name)) {
            throw ConfigurationError("command name '" + name + "' already registered");
        }
        factories_[name] = std::move(factory);
    }

    // Register the built-in commands shipped in this file.
    void register_default() {
        register_command("add_shape", [](const std::any& p) -> std::shared_ptr<Command> {
            return std::make_shared<AddShape>(std::any_cast<AddShapePayload>(p));
        });
        register_command("remove_shape", [](const std::any& p) -> std::shared_ptr<Command> {
            return std::make_shared<RemoveShape>(std::any_cast<RemoveShapePayload>(p));
        });
        register_command("transform", [](const std::any& p) -> std::shared_ptr<Command> {
            return std::make_shared<Transform>(std::any_cast<TransformPayload>(p));
        });
        register_command("persist", [](const std::any& p) -> std::shared_ptr<Command> {
            return std::make_shared<Persist>(std::any_cast<PersistPayload>(p));
        });
        register_command("replay", [](const std::any& p) -> std::shared_ptr<Command> {
            return std::make_shared<Replay>(std::any_cast<std::vector<std::shared_ptr<Command>>>(p));
        });
    }

    CommandResult execute(const std::string& name, const std::any& payload, Context& ctx) {
        auto it = factories_.find(name);
        if (it == factories_.end()) {
            return CommandResult::failure(ConfigurationError("unknown command '" + name + "'"));
        }
        auto cmd = it->second(payload);
        CommandResult result = cmd->execute(ctx);
        ctx.history.push_back(cmd);
        return result;
    }

    std::vector<std::string> known_names() const {
        std::vector<std::string> names;
        for (const auto& [name, factory] : factories_) names.push_back(name);
        std::sort(names.begin(), names.end());
        return names;
    }

private:
    std::unordered_map<std::string, Factory> factories_;
};

// Convenience: a bus with every built-in command registered.
inline CommandBus make_default_bus() {
    CommandBus bus;
    bus.register_default();
    return bus;
}

}  // namespace app_cli::commands
