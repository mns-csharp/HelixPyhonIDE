// CLI demo: drives the rest of the workspace end-to-end.
//
// Mirrors app_cli/main.py (the package's real entry point). The
// bare-bones `python -m app_cli` stub, app_cli_main.py ("entry point so
// `python -m app_cli` runs main()"), has no separate C++ file: a C++
// executable only has one `int main()`, and its intent -- "running this
// binary runs the demo" -- is exactly what running app_cli.exe already
// does.
//
// Deviation from the Python version: the legacy "intentional bug" section
// (passing a `str` where `total_area(list[Shape])` is expected) is
// dropped -- C++'s type system already rejects that at compile time, so
// there is nothing left to catch at runtime (matching the Rust sibling's
// identical decision, documented in its main.rs).

#include <any>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <typeinfo>

#include "app_cli/commands.cuh"
#include "app_cli/pipeline.cuh"
#include "lib_core/animals.cuh"
#include "lib_core/errors.cuh"
#include "lib_core/events.cuh"
#include "lib_core/models.cuh"
#include "lib_core/shapes.cuh"
#include "lib_core/storage.cuh"
#include "lib_utils/geometry.cuh"
#include "lib_utils/logger.cuh"

using namespace lib_core::animals;
using namespace lib_core::shapes;
using namespace lib_core::events;
using namespace lib_core::models;
using namespace lib_core::storage;
using namespace lib_utils::geometry;
using namespace lib_utils::logger;
using namespace app_cli::commands;

namespace {

// Sample listener subscribed via weak bound-method subscriptions (the
// analogue of Python's @on-decorated + attach_handlers listener).
class Listener : public std::enable_shared_from_this<Listener> {
public:
    void on_string(const StringPayload& ev) { seen.push_back(ev.text); }
    void on_change(const KeyValueChange& ev) {
        seen.push_back(ev.key + " -> " + ev.new_value.value_or(""));
    }

    std::vector<std::string> seen;
};

}  // namespace

int main() {
    LoggerFactory factory(Level::Info);
    auto& log = factory.get("cli");

    log.info("starting CLI demo");
    Circle circle(1.0);
    Rectangle rectangle(2.0, 3.0);
    Square square(4.0);
    Triangle triangle_shape(5.0, 6.0);
    RegularPolygon regular(6, 2.0);
    Star star(5, 3.0, 1.5);
    std::vector<const IShape*> shapes{&circle, &rectangle, &square, &triangle_shape, &regular, &star};

    log.info("shape count = " + std::to_string(shapes.size()));
    log.info("total area  = " + std::to_string(total_area(shapes)));
    const IShape* biggest = largest(shapes);
    log.info(std::string("largest     = ") + typeid(*biggest).name());

    // --- Bounding box / convex hull demo ----------------------------------
    BoundingBox bb = total_extent(shapes);
    log.info("total bounding box: " + std::to_string(bb.lo_x) + "," + std::to_string(bb.lo_y) + " -> " +
             std::to_string(bb.hi_x) + "," + std::to_string(bb.hi_y));
    auto triangle_poly = std::make_shared<Polygon>(
        std::vector<Point3D>{Point3D(0.0, 0.0), Point3D(4.0, 0.0), Point3D(2.0, 3.0)});
    BoundingBox tri_bb = bounding_box(*triangle_poly);
    log.info("triangle bbox: " + std::to_string(tri_bb.lo_x) + "," + std::to_string(tri_bb.lo_y) + " -> " +
             std::to_string(tri_bb.hi_x) + "," + std::to_string(tri_bb.hi_y));
    auto hull = convex_hull(triangle_poly->vertices);
    log.info("convex hull has " + std::to_string(hull.size()) + " vertices");

    // --- Animals: build a habitat and run a small chorus ------------------
    log.info("populating habitats");
    Habitat<Wolf> pack("North Ridge", 4);
    pack.admit(Wolf("Akela"));
    pack.admit(Wolf("Raksha", "North Ridge"));
    Habitat<Eagle> aviary("Cliff Hide", 2);
    aviary.admit(Eagle("Tarkus"));
    log.info("pack size = " + std::to_string(pack.size()) + ", aviary size = " + std::to_string(aviary.size()));

    Cat cat("Whiskers");
    Dog dog("Rex");
    PoliceDog police_dog("K9", 42);
    Snake snake("Kaa");
    for (const Animal* a : {static_cast<const Animal*>(&cat), static_cast<const Animal*>(&dog),
                             static_cast<const Animal*>(&police_dog), static_cast<const Animal*>(&snake)}) {
        log.info("  " + a->name() + " says " + a->speak());
    }
    Parrot parrot("Polly", "Alice");
    parrot.teach_word("hello");
    parrot.teach_word("goodbye");
    log.info("  Parrot " + parrot.name() + " now says " + parrot.speak());
    GuideDog guide("Buddy", "Sam", "Sam");
    guide.learn("forward");
    guide.learn("stop");
    log.info("  GuideDog " + guide.name() + " knows " + std::to_string(guide.commands().size()) + " command(s)");

    // --- Storage: typed + transactional -----------------------------------
    log.info("running typed-storage demo");
    InMemoryTypedStorage<std::string, int> storage;
    storage.set("alpha", 1);
    storage.set("beta", 2);
    TransactionalStorage<std::string, int> txn(std::move(storage));
    bool rolled_back = false;
    try {
        txn.transaction([](TransactionalStorage<std::string, int>& scope) {
            scope.set("gamma", 3);
            scope.set("alpha", 100);
            throw std::runtime_error("simulated failure to test rollback");
        });
    } catch (const std::runtime_error&) {
        rolled_back = true;
    }
    if (rolled_back) log.info("  rollback executed; alpha should be 1 again");
    log.info("  alpha = " + std::to_string(txn.get("alpha")));

    // --- Models: validated User --------------------------------------------
    log.info("constructing user");
    User user(UserId(42), Email("alice@example.com"), "", true, {"admin"});
    user.tag("editor");
    log.info("  user = " + user.display_name + " (" + user.email.address + ")");
    Ok<User> result = ok(user);
    log.info(std::string("  result.is_ok() = ") + (result.is_ok() ? "true" : "false"));

    // --- Commands ---------------------------------------------------------
    log.info("running command bus");
    CommandBus bus = make_default_bus();
    Context ctx;
    bus.execute("add_shape", std::any(AddShapePayload{triangle_poly}), ctx);
    bus.execute("add_shape", std::any(AddShapePayload{std::make_shared<Square>(5.0), "background"}), ctx);
    bus.execute("transform", std::any(TransformPayload{0, Matrix2x3::translation(10.0, 0.0)}), ctx);
    bus.execute("persist", std::any(PersistPayload{"/tmp/canvas.json", true}), ctx);
    log.info("  history depth = " + std::to_string(ctx.history.size()));
    log.info("  layers = " + std::to_string(ctx.canvas.size()));

    // --- Replay -------------------------------------------------------------
    log.info("running replay");
    Context replay_ctx;
    Replay replay(ctx.history);
    CommandResult replay_result = replay.execute(replay_ctx);
    log.info(std::string("  replay ok = ") + (replay_result.ok ? "true" : "false") +
             ", executed = " + std::to_string(replay_result.value.int_value));

    // --- Pipeline -----------------------------------------------------------
    log.info("running data pipeline");
    auto demo_pipeline = app_cli::pipeline::make_demo_pipeline();
    int pipeline_result =
        demo_pipeline.run(std::vector<int>{1, -2, 3, 4, -5, 6, 7, 8, 9, 10, 11, 12});
    log.info("  pipeline result = " + std::to_string(pipeline_result) + " (" + demo_pipeline.describe() + ")");

    // --- Event bus ----------------------------------------------------------
    log.info("running event-bus demo");
    EventBus bus2;
    auto listener = std::make_shared<Listener>();
    bus2.subscribe_weak<Listener, StringPayload>(listener, &Listener::on_string);
    bus2.subscribe_weak<Listener, KeyValueChange>(listener, &Listener::on_change);
    log.info("  attached 2 handlers");
    bus2.dispatch(StringPayload("hello", "cli"));
    bus2.dispatch(KeyValueChange("alpha", std::optional<std::string>("100"), std::nullopt, "cli"));
    std::string seen_joined;
    for (const auto& s : listener->seen) {
        if (!seen_joined.empty()) seen_joined += ", ";
        seen_joined += s;
    }
    log.info("  listener saw: " + seen_joined);

    // --- Final summary with a brief debug-level scope -----------------------
    std::size_t history_len = ctx.history.size();
    std::size_t sub_count = bus2.subscription_count();
    logger_scope(factory, "cli", Level::Debug, [&](StructuredLogger& scoped) {
        scoped.debug("finished -- " + std::to_string(history_len) + " commands, " + std::to_string(sub_count) +
                      " subs");
    });

    // --- Countdown -----------------------------------------------------
    for (int i = 0; i < 3; ++i) {
        log.info("countdown " + std::to_string(3 - i));
    }
    int numbers[] = {2, 4, 6, 8};
    int total = 0;
    for (int n : numbers) total += n;
    log.info("total = " + std::to_string(total));

    return 0;
}
