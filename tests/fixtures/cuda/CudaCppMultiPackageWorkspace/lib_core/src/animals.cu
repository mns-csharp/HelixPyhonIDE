#include "lib_core/animals.cuh"

#include <sstream>

namespace lib_core::animals {

int feed(const Animal& animal, int ration_grams) {
    // Python halves the ration for snakes; `dynamic_cast` is the direct
    // analogue of `isinstance(animal, Snake)` for a polymorphic reference.
    if (dynamic_cast<const Snake*>(&animal) != nullptr) {
        return ration_grams / 2;
    }
    return ration_grams;
}

std::string sleep_animal(const Animal& animal, double hours) {
    std::ostringstream out;
    out.precision(1);
    out << std::fixed << animal.name() << " slept " << hours << "h";
    return out.str();
}

std::vector<std::string> chorus(const std::vector<const Animal*>& animals) {
    std::vector<std::string> sounds;
    sounds.reserve(animals.size());
    for (const Animal* a : animals) {
        sounds.push_back(a->speak());
    }
    return sounds;
}

}  // namespace lib_core::animals
