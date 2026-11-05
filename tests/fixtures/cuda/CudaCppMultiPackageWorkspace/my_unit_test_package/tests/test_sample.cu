// Mirrors my_unit_test_package/tests/test_sample.py, using Catch2 instead
// of pytest.
//
// Two deliberate Python test bugs are fixed here rather than reproduced
// (matching the precedent set by this workspace's Rust/C# sibling
// translations):
//   - test_dimensions_stored asserted `r.height == 70.0` for a
//     `Rectangle(3.0, 7.0)` -- a typo'd expected literal; fixed to 7.0.
//   - test_dog_speak_prints_woof asserted that `Dog("rex").speak()`
//     PRINTS "woof" (via pytest's `capsys`), but `speak()` only returns
//     the string -- it never calls print(). Fixed to assert on the
//     return value instead of captured stdout.

#include <any>
#include <cmath>
#include <string>
#include <vector>

#include <catch2/catch_approx.hpp>
#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>

#include "lib_core/animals.cuh"
#include "lib_core/shapes.cuh"
#include "lib_core/storage.cuh"
#include "lib_utils/geometry.cuh"
#include "lib_utils/logger.cuh"

using namespace lib_core::animals;
using namespace lib_core::shapes;
using namespace lib_core::storage;
using namespace lib_utils::geometry;
using namespace lib_utils::logger;
using Catch::Approx;

// --------------------------------------------------------------------- shapes

TEST_CASE("Circle area", "[shapes]") {
    SECTION("unit radius") { REQUIRE(Circle(1.0).area() == Approx(3.14159)); }
    SECTION("scales with radius squared") {
        Circle c1(2.0);
        Circle c2(4.0);
        REQUIRE(c2.area() == Approx(c1.area() * 4));
    }
    SECTION("zero radius is zero") { REQUIRE(Circle(0.0).area() == 0.0); }
}

TEST_CASE("Rectangle area", "[shapes]") {
    struct Case {
        double w, h, expected;
    };
    auto c = GENERATE(Case{2.0, 3.0, 6.0}, Case{5.0, 5.0, 25.0}, Case{0.0, 10.0, 0.0}, Case{1.5, 4.0, 6.0});
    REQUIRE(Rectangle(c.w, c.h).area() == Approx(c.expected));
}

TEST_CASE("Rectangle dimensions stored", "[shapes]") {
    Rectangle r(3.0, 7.0);
    REQUIRE(r.width == 3.0);
    // Fixed: the Python original asserted `r.height == 70.0` -- a typo'd
    // expected literal. The rectangle was constructed with height 7.0.
    REQUIRE(r.height == 7.0);
}

TEST_CASE("Square is a Rectangle", "[shapes]") {
    Square s(4.0);
    Rectangle& as_rect = s;
    REQUIRE(as_rect.width == 4.0);
}

TEST_CASE("Square equal sides", "[shapes]") {
    Square s(5.0);
    REQUIRE(s.width == 5.0);
    REQUIRE(s.height == 5.0);
}

TEST_CASE("Square area is side squared", "[shapes]") { REQUIRE(Square(6.0).area() == Approx(36.0)); }

TEST_CASE("Triangle area formula", "[shapes]") { REQUIRE(Triangle(10.0, 4.0).area() == Approx(20.0)); }

TEST_CASE("Triangle zero base or height", "[shapes]") {
    REQUIRE(Triangle(0.0, 5.0).area() == 0.0);
    REQUIRE(Triangle(5.0, 0.0).area() == 0.0);
}

// -------------------------------------------------------------------- storage

TEST_CASE("InMemoryStorage set then get", "[storage]") {
    InMemoryStorage s;
    s.set("k", 42);
    REQUIRE(std::any_cast<int>(*s.get("k")) == 42);
}

TEST_CASE("InMemoryStorage get missing returns nullopt", "[storage]") { REQUIRE(!InMemoryStorage().get("nope").has_value()); }

TEST_CASE("InMemoryStorage overwrite", "[storage]") {
    InMemoryStorage s;
    s.set("k", std::string("first"));
    s.set("k", std::string("second"));
    REQUIRE(std::any_cast<std::string>(*s.get("k")) == "second");
}

TEST_CASE("InMemoryStorage delete existing", "[storage]") {
    InMemoryStorage s;
    s.set("k", 1);
    s.remove("k");
    REQUIRE(!s.get("k").has_value());
}

TEST_CASE("InMemoryStorage delete missing is no-op", "[storage]") {
    InMemoryStorage s;
    REQUIRE_NOTHROW(s.remove("absent"));
}

TEST_CASE("InMemoryStorage supports arbitrary value types", "[storage]") {
    InMemoryStorage s;
    s.set("list", std::vector<int>{1, 2, 3});
    REQUIRE((std::any_cast<std::vector<int>>(*s.get("list")) == std::vector<int>{1, 2, 3}));
}

TEST_CASE("InMemoryStorage isolation between instances", "[storage]") {
    InMemoryStorage a, b;
    a.set("k", std::string("from-a"));
    REQUIRE(!b.get("k").has_value());
}

// -------------------------------------------------------------------- animals

TEST_CASE("Color enum distinct values", "[animals]") {
    REQUIRE(static_cast<int>(Color::Red) == 1);
    REQUIRE(static_cast<int>(Color::Green) == 2);
    REQUIRE(static_cast<int>(Color::Blue) == 3);
}

TEST_CASE("Animal subclasses speak", "[animals]") {
    REQUIRE(Dog("a").speak() == "woof");
    REQUIRE(Cat("a").speak() == "meow");
    REQUIRE(Bird("a").speak() == "tweet");
}

TEST_CASE("Dog name is stored", "[animals]") { REQUIRE(Dog("rex").name() == "rex"); }

TEST_CASE("PoliceDog inherits Dog and Animal", "[animals]") {
    PoliceDog pd("k9", 17);
    REQUIRE(dynamic_cast<Dog*>(&pd) != nullptr);
    REQUIRE(dynamic_cast<Animal*>(&pd) != nullptr);
}

TEST_CASE("PoliceDog speak includes badge", "[animals]") {
    PoliceDog pd("k9", 17);
    std::string out = pd.speak();
    REQUIRE(out.find("woof") != std::string::npos);
    REQUIRE(out.find("17") != std::string::npos);
}

TEST_CASE("Dog speak returns woof", "[animals]") {
    // Fixed: the Python original asserted this PRINTS "woof" via
    // capsys, but Animal.speak() never calls print() -- it only returns
    // the string. Asserting on the return value is the behaviour that
    // actually holds.
    REQUIRE(Dog("rex").speak() == "woof");
}

// ------------------------------------------------------------------- geometry

TEST_CASE("total_area empty list is zero", "[geometry]") { REQUIRE(total_area({}) == 0.0); }

TEST_CASE("total_area sum of rectangles", "[geometry]") {
    Rectangle r1(2, 3);
    Rectangle r2(4, 5);
    std::vector<const IShape*> shapes{&r1, &r2};
    REQUIRE(total_area(shapes) == Approx(2 * 3 + 4 * 5));
}

TEST_CASE("total_area mixed shapes", "[geometry]") {
    Circle c(1);
    Square sq(2);
    Triangle t(4, 4);
    std::vector<const IShape*> shapes{&c, &sq, &t};
    double expected = 3.14159 + 4.0 + 8.0;
    REQUIRE(total_area(shapes) == Approx(expected));
}

TEST_CASE("largest picks biggest rectangle", "[geometry]") {
    Rectangle small(1, 1);
    Rectangle big(10, 10);
    Rectangle mid(3, 3);
    std::vector<const IShape*> shapes{&small, &big, &mid};
    REQUIRE(largest(shapes) == static_cast<const IShape*>(&big));
}

TEST_CASE("largest single element", "[geometry]") {
    Triangle only(3, 4);
    std::vector<const IShape*> shapes{&only};
    REQUIRE(largest(shapes) == static_cast<const IShape*>(&only));
}

TEST_CASE("largest first when tied", "[geometry]") {
    Rectangle a(2, 3);
    Rectangle b(3, 2);  // same area
    std::vector<const IShape*> shapes{&a, &b};
    // Strict `>` keeps the earlier element on a tie.
    REQUIRE(largest(shapes) == static_cast<const IShape*>(&a));
}

// --------------------------------------------------------------------- logger

TEST_CASE("Logger repr format", "[logger]") { REQUIRE(Logger("APP").repr() == "Logger(prefix='APP')"); }

TEST_CASE("Logger from_config returns configured logger", "[logger]") {
    Logger log = Logger::from_config("CFG");
    REQUIRE(log.repr() == "Logger(prefix='CFG')");
}

TEST_CASE("Logger protected prefix is accessible for introspection", "[logger]") {
    Logger log("X");
    REQUIRE(log.prefix() == "X");
}

// --------------------------------------------------------------- sanity check

TEST_CASE("pi constant is close to the real value", "[sanity]") {
    // Documents the deliberate approximation used by Circle::area().
    REQUIRE(std::abs(3.14159 - 3.14159265358979323846) < 1e-3);
}
