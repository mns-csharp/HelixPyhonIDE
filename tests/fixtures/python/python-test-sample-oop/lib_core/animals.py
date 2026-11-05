"""Animal taxonomy: ABCs, mixins, multiple inheritance, async behaviours.

The original Animal/Dog/Cat/Bird/PoliceDog hierarchy stays so existing
tests pass. Around it we layer:

  - Class-class ABCs: Mammal, Bird, Reptile (each subclasses Animal).
  - Mixins for behaviour: Trainable, Domesticated, Predator. Mixins do
    NOT subclass Animal — they're composed in via multiple inheritance.
  - Concrete species: Wolf(Predator, Mammal), Eagle(Predator, Bird),
    Snake(Predator, Reptile). Multi-base inheritance + MRO stress test.
  - Async I/O: feed() and sleep() are coroutines that simulate work via
    asyncio.sleep so the IDE has async/await sites to render.
  - Generic Habitat[T: Animal] container so Generic + bounded-TypeVar
    behaviour gets exercised.

The Color enum stays for backwards compatibility; we add an IntEnum and
a Flag enum elsewhere to broaden the IDE's enum coverage.
"""
from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from enum import Enum, Flag, IntEnum, auto
from typing import Generic, Protocol, TypeVar


# -- Protocol -----------------------------------------------------------


class Speaker(Protocol):
    """Anything that can make a sound.

    Every concrete Animal in this module satisfies Speaker structurally
    (each defines ``speak()``) without declaring it explicitly — that's
    the difference from an ABC like Animal itself, which requires it.
    """

    def speak(self) -> str:
        ...


# -- Enum vocabulary (kept Color for compatibility) -------------------------


class Color(Enum):
    """Original three-colour enum used by existing tests."""

    RED = 1
    GREEN = 2
    BLUE = 3


class Habit(IntEnum):
    """When the animal is most active."""

    DIURNAL = 1
    NOCTURNAL = 2
    CREPUSCULAR = 3


class DietFlags(Flag):
    """Composable diet flags — an animal may be more than one."""

    NONE = 0
    HERBIVORE = auto()
    CARNIVORE = auto()
    INSECTIVORE = auto()
    PISCIVORE = auto()
    OMNIVORE = HERBIVORE | CARNIVORE


# -- Original hierarchy (kept verbatim, modulo type cleanup) ---------------


class Animal(ABC):
    """Base class — subclasses must implement speak()."""

    def __init__(self, name: str) -> None:
        self.name = name

    @abstractmethod
    def speak(self) -> str:
        ...


class Dog(Animal):
    """A domesticated dog. Says 'woof'."""

    def speak(self) -> str:
        return "woof"


class Cat(Animal):
    """A domesticated cat. Says 'meow'."""

    def speak(self) -> str:
        return "meow"


class Bird(Animal):
    """A bird. Says 'tweet'."""

    def speak(self) -> str:
        return "tweet"


class PoliceDog(Dog):
    """Multi-level inheritance: PoliceDog -> Dog -> Animal."""

    def __init__(self, name: str, badge: int) -> None:
        super().__init__(name)
        self.badge = badge

    def speak(self) -> str:
        return "woof! badge #" + str(self.badge)


# -- Taxonomic ABCs ---------------------------------------------------------


class Mammal(Animal):
    """Vertebrates with hair/fur and live birth."""

    @property
    def has_fur(self) -> bool:
        """Most mammals; subclasses can override."""
        return True

    def gestation_days(self) -> int:
        """Approximate gestation period; subclasses should override."""
        return 60


class WildBird(Animal):
    """Bird that doesn't fall under the legacy Bird class."""

    def can_fly(self) -> bool:
        return True

    def wingspan_cm(self) -> float:
        return 30.0


class Reptile(Animal):
    """Cold-blooded vertebrates."""

    def basking_temperature_c(self) -> float:
        return 30.0

    def is_venomous(self) -> bool:
        return False


# -- Behavioural mixins -----------------------------------------------------


class Trainable:
    """Mixin: this animal can learn commands.

    Mixins should NOT call ``super().__init__`` because they may not be
    at the bottom of the MRO. They contribute behaviour, not state, and
    expect to be combined with a class that owns the ``__init__`` chain.
    """

    def __init__(self, *args, **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self.commands: list[str] = []

    def learn(self, command: str) -> None:
        """Add a command to this animal's repertoire if not already known."""
        if command not in self.commands:
            self.commands.append(command)

    def perform(self, command: str) -> bool:
        """Return ``True`` iff the animal knows ``command``."""
        return command in self.commands


class Domesticated:
    """Mixin: lives in a household with a human owner."""

    def __init__(self, *args, owner: str = "", **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self.owner: str = owner

    def is_owned(self) -> bool:
        """``True`` iff the animal has an assigned owner."""
        return bool(self.owner)


class Predator:
    """Mixin: hunts prey. Tracks number of successful kills.

    Diet defaults to CARNIVORE but subclasses may override.
    """

    diet: DietFlags = DietFlags.CARNIVORE

    def __init__(self, *args, **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self.kill_count: int = 0

    def hunt(self, prey: "Animal") -> bool:
        """Attempt a hunt against ``prey``. Returns True on (simulated) success."""
        # Smaller prey (by name length, just for variety) is easier.
        if len(prey.name) < 6:
            self.kill_count += 1
            return True
        return False


# -- Concrete species using multiple inheritance ---------------------------


class Wolf(Predator, Mammal):
    """A grey wolf — a wild canid predator."""

    def __init__(self, name: str, pack: str = "") -> None:
        super().__init__(name)
        self.pack = pack
        self.habit = Habit.NOCTURNAL

    def speak(self) -> str:
        return "awooo"

    def gestation_days(self) -> int:
        # Wolves: ~63 days
        return 63


class Eagle(Predator, WildBird):
    """A bald eagle — an avian predator."""

    def __init__(self, name: str) -> None:
        super().__init__(name)
        self.habit = Habit.DIURNAL

    def speak(self) -> str:
        return "screech"

    def wingspan_cm(self) -> float:
        return 220.0


class Snake(Predator, Reptile):
    """A snake — a reptilian predator. Some are venomous."""

    def __init__(self, name: str, venomous: bool = False) -> None:
        super().__init__(name)
        self._venomous = venomous
        self.habit = Habit.CREPUSCULAR

    def speak(self) -> str:
        return "hisss"

    def is_venomous(self) -> bool:
        return self._venomous


class GuideDog(Trainable, Domesticated, Dog):
    """A guide dog: trainable, domesticated dog. Triple multiple-inheritance."""

    def __init__(self, name: str, owner: str = "", handler: str = "") -> None:
        super().__init__(name=name, owner=owner)
        self.handler = handler

    def speak(self) -> str:
        # Guide dogs are trained to be quiet on duty.
        return "(quiet)"


class Parrot(Trainable, Domesticated, Bird):
    """A parrot — can be taught to mimic words."""

    def __init__(self, name: str, owner: str = "") -> None:
        super().__init__(name=name, owner=owner)
        self.vocabulary: list[str] = []

    def teach_word(self, word: str) -> None:
        """Add ``word`` to the parrot's vocabulary."""
        if word not in self.vocabulary:
            self.vocabulary.append(word)

    def speak(self) -> str:
        if not self.vocabulary:
            return "tweet"
        return self.vocabulary[-1]


# -- Async behaviours -------------------------------------------------------


async def feed(animal: Animal, *, ration_grams: int = 100) -> int:
    """Asynchronously feed an animal. Returns the grams consumed.

    The actual operation is simulated with an ``asyncio.sleep`` so the
    coroutine has something to await — useful as a stress-test for the
    IDE's async-aware navigation.
    """
    # Simulate I/O wait proportional to ration.
    await asyncio.sleep(min(ration_grams / 1000.0, 0.05))
    consumed = ration_grams if not isinstance(animal, Snake) else ration_grams // 2
    return consumed


async def sleep_animal(animal: Animal, *, hours: float = 8.0) -> str:
    """Asynchronously put the animal to sleep for ``hours``."""
    await asyncio.sleep(min(hours / 1000.0, 0.05))
    return f"{animal.name} slept {hours:.1f}h"


async def chorus(animals: list[Animal]) -> list[str]:
    """All animals speak concurrently. Returns their utterances in order."""
    # asyncio.gather preserves input order in the result list.
    sounds = await asyncio.gather(*(_say(a) for a in animals))
    return list(sounds)


async def _say(animal: Animal) -> str:
    """Internal helper — yield to the loop then return ``animal.speak()``."""
    await asyncio.sleep(0)
    return animal.speak()


# -- Generic container ------------------------------------------------------


T = TypeVar("T", bound=Animal)


class Habitat(Generic[T]):
    """A fenced area holding animals of a particular kind ``T``.

    The TypeVar bound (``T: Animal``) means the IDE can infer that
    ``occupant.speak()`` is valid for any ``Habitat[X]`` because every
    bound element implements Animal.speak.
    """

    def __init__(self, name: str, capacity: int) -> None:
        self.name = name
        self.capacity = capacity
        self._occupants: list[T] = []

    def admit(self, occupant: T) -> bool:
        """Add ``occupant`` to the habitat. Returns False if at capacity."""
        if len(self._occupants) >= self.capacity:
            return False
        self._occupants.append(occupant)
        return True

    def release(self, occupant: T) -> None:
        """Remove ``occupant`` if present (no-op otherwise)."""
        if occupant in self._occupants:
            self._occupants.remove(occupant)

    @property
    def occupants(self) -> list[T]:
        """Read-only view of the occupants list."""
        return list(self._occupants)

    @property
    def is_full(self) -> bool:
        """``True`` iff occupancy == capacity."""
        return len(self._occupants) >= self.capacity

    def __len__(self) -> int:
        return len(self._occupants)

    def __iter__(self):
        return iter(self._occupants)

    def __contains__(self, occupant: object) -> bool:
        return occupant in self._occupants
