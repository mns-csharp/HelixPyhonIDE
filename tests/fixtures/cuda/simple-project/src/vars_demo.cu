#include "vars_demo.cuh"

#include <map>
#include <numeric>

namespace pythonsimple::vars_demo {

Point::Point(int x, int y) : x_(x), y_(y) {}
int Point::x() const { return x_; }
int Point::y() const { return y_; }

int build() {
    std::vector<int> numbers{10, 20, 30};

    std::map<std::string, Value> person;
    person["name"] = std::string("Ada");
    person["age"] = 36;
    person["langs"] = std::vector<std::string>{"py", "c"};

    Point p(3, 4);
    (void)p;

    int total = std::accumulate(numbers.begin(), numbers.end(), 0);
    return total; // breakpoint here: numbers/person/p/total all in scope
}

} // namespace pythonsimple::vars_demo
