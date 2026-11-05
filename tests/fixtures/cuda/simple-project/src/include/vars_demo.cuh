#pragma once

#include <string>
#include <variant>
#include <vector>

// Deterministic target for exercising the debugger's Variables tree-expansion.
//
// build() constructs a vector, a nested map, and an object instance, then
// returns on `return total` with all of them in scope so the GUI Variables
// panel can expand each one.
namespace pythonsimple::vars_demo {

class Point {
public:
    Point(int x, int y);
    int x() const;
    int y() const;

private:
    int x_;
    int y_;
};

// Heterogeneous "dict" values (Python's person = {"name": str, "age": int,
// "langs": list[str]}) -> a tagged union via std::variant, matching the Rust
// translation's `enum Value { Str(String), Int(i32), List(Vec<String>) }` in
// vars_demo.rs.
using Value = std::variant<std::string, int, std::vector<std::string>>;

int build();

} // namespace pythonsimple::vars_demo
