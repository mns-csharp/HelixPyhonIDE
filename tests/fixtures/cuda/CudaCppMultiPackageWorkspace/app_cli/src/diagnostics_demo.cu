// Mirrors app_cli/_diagnostics_demo.py.
//
// That file mixes two kinds of "intentional bug":
//
//   1. Genuine RUNTIME mistakes even in a statically-typed language
//      (dispatching an unregistered command name, transforming a
//      non-Polygon shape, constructing Take(-1)) -- these ARE reproduced
//      below, since they're real behaviour, not something a type checker
//      alone catches.
//   2. Mistakes that only exist because Python's type checker runs
//      SEPARATELY from execution (constructing AddShape with the wrong
//      payload struct, a Pipeline whose Map/Reduce types don't line up,
//      Polygon built from tuples instead of Point3Ds). In C++ these are
//      caught by the compiler itself -- reproducing them would mean this
//      file (and the whole workspace) doesn't build, defeating both the
//      fixture's purpose and the "builds cleanly" requirement. They are
//      intentionally NOT reproduced; the corresponding Python function
//      names are noted below for traceability, matching the precedent
//      set by this workspace's Rust sibling
//      (app_cli/src/diagnostics_demo.rs).
//
// - bad_dispatch -> reproduced below (`bad_dispatch`).
// - wrong_payload_type -> compile-time prevented (AddShapePayload and
//   PersistPayload are distinct, unrelated structs).
// - transform_non_polygon -> reproduced below (`transform_non_polygon`).
// - bad_pipeline_chain -> compile-time prevented (Map<T>/Reduce<T,R>'s
//   template parameters must already agree).
// - bad_take_argument -> reproduced below (`bad_take_argument`); see
//   also pipeline.cuh's `Take` constructor, which throws for a negative n.
// - polygon_with_tuples -> compile-time prevented (Polygon::Polygon takes
//   `std::vector<Point3D>`, not tuples).

#include <cassert>

#include "app_cli/commands.cuh"
#include "app_cli/pipeline.cuh"
#include "lib_core/shapes.cuh"

namespace app_cli::diagnostics_demo {

using namespace app_cli::commands;
using lib_core::shapes::Circle;

// Dispatch a command name that wasn't registered.
void bad_dispatch() {
    CommandBus bus = make_default_bus();
    Context ctx;
    // Runtime: returns CommandResult{ok=false} with a ConfigurationError.
    // Static: a typo in a string key is invisible to any type checker,
    // Python's or C++'s alike -- listing it here gives the user
    // something to grep for.
    CommandResult result =
        bus.execute("add-shapee", std::any(AddShapePayload{std::make_shared<Circle>(1.0)}), ctx);
    assert(!result.ok);
}

// `Transform` only works on Polygons. Passing a Circle returns
// CommandResult{ok=false} at runtime; intentionally drives the bus, just
// like the Python original.
void transform_non_polygon() {
    CommandBus bus = make_default_bus();
    Context ctx;
    bus.execute("add_shape", std::any(AddShapePayload{std::make_shared<Circle>(1.0)}), ctx);
    CommandResult result =
        bus.execute("transform", std::any(TransformPayload{0, lib_utils::geometry::Matrix2x3::identity()}), ctx);
    assert(!result.ok);
}

// `Take` needs a non-negative int; constructing with -1 throws
// std::invalid_argument at runtime (mirrors Python's ValueError).
void bad_take_argument() {
    bool threw = false;
    try {
        app_cli::pipeline::Take<int> t(-1);
    } catch (const std::invalid_argument&) {
        threw = true;
    }
    assert(threw);
}

}  // namespace app_cli::diagnostics_demo
