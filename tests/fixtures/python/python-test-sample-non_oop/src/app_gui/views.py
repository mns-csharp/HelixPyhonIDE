"""Tkinter view components for the GUI demo.

Two small panels — one listing Shapes (from lib_core.shapes), one
listing Animals (from lib_core.animals) — plus a Protocol describing
"anything that can redraw itself from fresh data", which both panels
satisfy structurally without declaring it explicitly.
"""
from __future__ import annotations

import tkinter as tk
from tkinter import ttk
from typing import Protocol, Sequence

from lib_core.animals import Speaker
from lib_core.shapes import Shape


# -- Protocol -----------------------------------------------------------


class Refreshable(Protocol):
    """Anything that can redraw itself from a fresh batch of data.

    Both panels below satisfy this structurally via their own
    ``refresh()`` methods.
    """

    def refresh(self) -> None:
        ...


# -- Panels ------------------------------------------------------------


class ShapePanel(ttk.Frame):
    """Lists shapes with their computed area, one per row."""

    def __init__(self, master: tk.Misc) -> None:
        super().__init__(master)
        self._listbox = tk.Listbox(self, height=8)
        self._listbox.pack(fill="both", expand=True)
        self._shapes: list[Shape] = []

    def set_shapes(self, shapes: Sequence[Shape]) -> None:
        """Replace the displayed shapes and redraw."""
        self._shapes = list(shapes)
        self.refresh()

    def refresh(self) -> None:
        self._listbox.delete(0, tk.END)
        for shape in self._shapes:
            self._listbox.insert(tk.END, f"{type(shape).__name__}: area={shape.area():.2f}")


class AnimalPanel(ttk.Frame):
    """Lists animals with what they say, one per row."""

    def __init__(self, master: tk.Misc) -> None:
        super().__init__(master)
        self._listbox = tk.Listbox(self, height=8)
        self._listbox.pack(fill="both", expand=True)
        self._animals: list[tuple[str, Speaker]] = []

    def set_animals(self, animals: Sequence[tuple[str, Speaker]]) -> None:
        """Replace the displayed animals and redraw."""
        self._animals = list(animals)
        self.refresh()

    def refresh(self) -> None:
        self._listbox.delete(0, tk.END)
        for name, animal in self._animals:
            self._listbox.insert(tk.END, f"{name} says {animal.speak()}")
