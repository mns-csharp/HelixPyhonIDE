#pragma once

// Animal taxonomy: ABCs, mixins, multiple inheritance, "async" behaviours.
//
// Mirrors lib_core/animals.py. Translation notes:
//   - `Animal(ABC)` + `@abstractmethod speak` -> abstract class `Animal`
//     with a pure virtual `speak()`.
//   - The taxonomic ABCs (Mammal, WildBird, Reptile) each subclass Animal,
//     exactly like Python, with virtual methods carrying default bodies
//     that concrete species override.
//   - The behavioural mixins (Trainable, Domesticated, Predator) do NOT
//     subclass Animal in Python -- they are composed in via multiple
//     inheritance alongside an Animal subclass. Unlike the Rust sibling
//     translation (which had to push mixin state behind required accessor
//     methods because Rust traits can't own state), C++ has REAL multiple
//     inheritance, so these mixins hold their own state directly, exactly
//     like the Python originals.
//   - Diamond check: none of Wolf(Predator, Mammal), Eagle(Predator,
//     WildBird), Snake(Predator, Reptile), GuideDog(Trainable,
//     Domesticated, Dog), Parrot(Trainable, Domesticated, Bird) actually
//     re-converge on Animal -- Trainable/Domesticated/Predator are plain
//     mixins with no Animal base (matching Python, where they don't
//     subclass Animal either), so `Animal` appears exactly once in every
//     concrete species' base-class set. No virtual inheritance is needed
//     anywhere in this file.
//   - Python's cooperative `super().__init__(*args, **kwargs)` chain
//     across the whole MRO has no C++ equivalent; each concrete species'
//     constructor instead explicitly initializes every direct base in its
//     member-initializer list (the usual C++ multiple-inheritance idiom).
//   - `async def feed/sleep_animal/chorus/_say` become plain synchronous
//     functions. The only "await" in each was a simulated
//     `asyncio.sleep(...)` proportional to the workload with no other
//     effect; reproducing it as a real sleep would only slow the fixture
//     down for no observable difference, so it is dropped (documented
//     here rather than silently changed), matching the Rust sibling's
//     decision to drop non-load-bearing timing simulation. Genuine
//     concurrency (`asyncio.gather` in chorus) is preserved as ordinary
//     sequential iteration, which already satisfies its documented
//     "preserves input order" contract.
//   - `Habitat[T: Animal]` -> `template <typename T> requires
//     std::derived_from<T, Animal> class Habitat`, the direct C++ analogue
//     of a bounded TypeVar.
//   - `Color` (Enum) -> `enum class Color`. `Habit` (IntEnum) -> `enum
//     class Habit : int` with explicit underlying type. `DietFlags` (Flag)
//     -> `enum class DietFlags : std::uint8_t` with power-of-two values
//     plus overloaded operator| / operator&.

#include <cstdint>
#include <string>
#include <type_traits>
#include <vector>

namespace lib_core::animals {

// -- Enum vocabulary (kept Color for compatibility) -------------------------

// Original three-colour enum used by existing tests.
enum class Color { Red = 1, Green = 2, Blue = 3 };

// When the animal is most active. Mirrors Python's IntEnum via an explicit
// underlying type.
enum class Habit : int { Diurnal = 1, Nocturnal = 2, Crepuscular = 3 };

// Composable diet flags -- an animal may be more than one. Mirrors
// Python's Flag enum: power-of-two values + overloaded bitwise operators.
enum class DietFlags : std::uint8_t {
    None = 0,
    Herbivore = 1u << 0,
    Carnivore = 1u << 1,
    Insectivore = 1u << 2,
    Piscivore = 1u << 3,
    Omnivore = (1u << 0) | (1u << 1),  // Herbivore | Carnivore
};

constexpr DietFlags operator|(DietFlags a, DietFlags b) {
    return static_cast<DietFlags>(static_cast<std::uint8_t>(a) | static_cast<std::uint8_t>(b));
}

constexpr DietFlags operator&(DietFlags a, DietFlags b) {
    return static_cast<DietFlags>(static_cast<std::uint8_t>(a) & static_cast<std::uint8_t>(b));
}

constexpr bool has_flag(DietFlags value, DietFlags flag) {
    return (value & flag) == flag;
}

// -- Base class (mirrors the Animal(ABC) hierarchy) -------------------------

// Base class -- subclasses must implement speak().
class Animal {
public:
    explicit Animal(std::string name) : name_(std::move(name)) {}
    virtual ~Animal() = default;

    const std::string& name() const { return name_; }
    virtual std::string speak() const = 0;

private:
    std::string name_;
};

// A domesticated dog. Says 'woof'.
class Dog : public Animal {
public:
    using Animal::Animal;
    std::string speak() const override { return "woof"; }
};

// A domesticated cat. Says 'meow'.
class Cat : public Animal {
public:
    using Animal::Animal;
    std::string speak() const override { return "meow"; }
};

// A bird. Says 'tweet'.
class Bird : public Animal {
public:
    using Animal::Animal;
    std::string speak() const override { return "tweet"; }
};

// Multi-level inheritance: PoliceDog -> Dog -> Animal.
class PoliceDog : public Dog {
public:
    PoliceDog(std::string name, int badge) : Dog(std::move(name)), badge(badge) {}

    std::string speak() const override { return "woof! badge #" + std::to_string(badge); }

    int badge;
};

// -- Taxonomic ABCs ---------------------------------------------------------

// Vertebrates with hair/fur and live birth.
class Mammal : public Animal {
public:
    using Animal::Animal;

    // Most mammals; subclasses can override.
    virtual bool has_fur() const { return true; }

    // Approximate gestation period; subclasses should override.
    virtual int gestation_days() const { return 60; }
};

// Bird that doesn't fall under the legacy Bird class.
class WildBird : public Animal {
public:
    using Animal::Animal;

    virtual bool can_fly() const { return true; }
    virtual double wingspan_cm() const { return 30.0; }
};

// Cold-blooded vertebrates.
class Reptile : public Animal {
public:
    using Animal::Animal;

    virtual double basking_temperature_c() const { return 30.0; }
    virtual bool is_venomous() const { return false; }
};

// -- Behavioural mixins (real state, real multiple inheritance) ------------

// Mixin: this animal can learn commands. Unlike Python (where mixins
// cooperate via `super().__init__` and must NOT be the MRO's terminus),
// this mixin simply owns its own `commands_` state directly -- real C++
// multiple inheritance means every base subobject is independently
// initialized by the most-derived class, so there is no "who owns
// __init__" ordering concern.
class Trainable {
public:
    Trainable() = default;

    // Add a command to this animal's repertoire if not already known.
    void learn(const std::string& command) {
        for (const auto& c : commands_) {
            if (c == command) return;
        }
        commands_.push_back(command);
    }

    // Return true iff the animal knows `command`.
    bool perform(const std::string& command) const {
        for (const auto& c : commands_) {
            if (c == command) return true;
        }
        return false;
    }

    const std::vector<std::string>& commands() const { return commands_; }

private:
    std::vector<std::string> commands_;
};

// Mixin: lives in a household with a human owner.
class Domesticated {
public:
    explicit Domesticated(std::string owner = "") : owner_(std::move(owner)) {}

    // True iff the animal has an assigned owner.
    bool is_owned() const { return !owner_.empty(); }

    const std::string& owner() const { return owner_; }

private:
    std::string owner_;
};

// Mixin: hunts prey. Tracks number of successful kills. Diet defaults to
// Carnivore but subclasses may override diet().
class Predator {
public:
    Predator() = default;
    virtual ~Predator() = default;

    virtual DietFlags diet() const { return DietFlags::Carnivore; }

    // Attempt a hunt against `prey`. Returns true on (simulated) success.
    bool hunt(const Animal& prey) {
        // Smaller prey (by name length, just for variety) is easier.
        if (prey.name().size() < 6) {
            ++kill_count_;
            return true;
        }
        return false;
    }

    int kill_count() const { return kill_count_; }

private:
    int kill_count_ = 0;
};

// -- Concrete species using multiple inheritance ---------------------------

// A grey wolf -- a wild canid predator.
class Wolf : public Predator, public Mammal {
public:
    explicit Wolf(std::string name, std::string pack = "")
        : Predator(), Mammal(std::move(name)), pack(std::move(pack)), habit(Habit::Nocturnal) {}

    std::string speak() const override { return "awooo"; }
    int gestation_days() const override { return 63; }  // Wolves: ~63 days

    std::string pack;
    Habit habit;
};

// A bald eagle -- an avian predator.
class Eagle : public Predator, public WildBird {
public:
    explicit Eagle(std::string name) : Predator(), WildBird(std::move(name)), habit(Habit::Diurnal) {}

    std::string speak() const override { return "screech"; }
    double wingspan_cm() const override { return 220.0; }

    Habit habit;
};

// A snake -- a reptilian predator. Some are venomous.
class Snake : public Predator, public Reptile {
public:
    explicit Snake(std::string name, bool venomous = false)
        : Predator(), Reptile(std::move(name)), venomous_(venomous), habit(Habit::Crepuscular) {}

    std::string speak() const override { return "hisss"; }
    bool is_venomous() const override { return venomous_; }

    Habit habit;

private:
    bool venomous_;
};

// A guide dog: trainable, domesticated dog. Triple multiple-inheritance.
class GuideDog : public Trainable, public Domesticated, public Dog {
public:
    GuideDog(std::string name, std::string owner = "", std::string handler = "")
        : Trainable(), Domesticated(std::move(owner)), Dog(std::move(name)), handler(std::move(handler)) {}

    // Guide dogs are trained to be quiet on duty.
    std::string speak() const override { return "(quiet)"; }

    std::string handler;
};

// A parrot -- can be taught to mimic words.
class Parrot : public Trainable, public Domesticated, public Bird {
public:
    Parrot(std::string name, std::string owner = "")
        : Trainable(), Domesticated(std::move(owner)), Bird(std::move(name)) {}

    // Add `word` to the parrot's vocabulary.
    void teach_word(const std::string& word) {
        for (const auto& w : vocabulary) {
            if (w == word) return;
        }
        vocabulary.push_back(word);
    }

    std::string speak() const override {
        if (vocabulary.empty()) return "tweet";
        return vocabulary.back();
    }

    std::vector<std::string> vocabulary;
};

// -- "Async" behaviours (represented synchronously; see file header) -------

// Asynchronously feed an animal in Python; here a plain synchronous call.
// Returns the grams consumed.
int feed(const Animal& animal, int ration_grams = 100);

// Asynchronously put the animal to sleep for `hours` in Python; here a
// plain synchronous call.
std::string sleep_animal(const Animal& animal, double hours = 8.0);

// All animals speak, in input order (Python's asyncio.gather preserves
// input order; a plain sequential loop trivially does too).
std::vector<std::string> chorus(const std::vector<const Animal*>& animals);

// -- Generic container ------------------------------------------------------

// A fenced area holding animals of a particular kind T. The TypeVar bound
// (T: Animal) becomes a direct C++ concept-style requirement.
template <typename T>
    requires std::is_base_of_v<Animal, T>
class Habitat {
public:
    Habitat(std::string name, int capacity) : name(std::move(name)), capacity(capacity) {}

    // Add `occupant` to the habitat. Returns false if at capacity.
    bool admit(T occupant) {
        if (occupants_.size() >= static_cast<std::size_t>(capacity)) return false;
        occupants_.push_back(std::move(occupant));
        return true;
    }

    // Read-only view of the occupants.
    const std::vector<T>& occupants() const { return occupants_; }

    // True iff occupancy == capacity.
    bool is_full() const { return occupants_.size() >= static_cast<std::size_t>(capacity); }

    std::size_t size() const { return occupants_.size(); }

    auto begin() const { return occupants_.begin(); }
    auto end() const { return occupants_.end(); }

    std::string name;
    int capacity;

private:
    std::vector<T> occupants_;
};

}  // namespace lib_core::animals
