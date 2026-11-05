"""OOP demo for the class-diagram designer: an interface (Protocol), an abstract
base (ABC), and a concrete subclass — so the category tabs and inheritance edges
have something to render."""

from abc import ABC, abstractmethod
from typing import Protocol


class Drawable(Protocol):
    def draw(self) -> None: ...


class Shape(ABC):
    @abstractmethod
    def area(self) -> float: ...


class Circle(Shape):
    def __init__(self, radius: float):
        self.radius = radius

    def area(self) -> float:
        return 3.14159 * self.radius * self.radius
