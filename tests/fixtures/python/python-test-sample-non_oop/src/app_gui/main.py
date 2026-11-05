"""GUI demo: the same lib_core / lib_utils API as app_cli.main, from a
desktop window instead of the console.

Builds a small Tkinter window with two tabs — Shapes and Animals —
populated from the same domain objects app_cli's console demo uses, so
the IDE has a real, runnable GUI entry point alongside the CLI and
server ones. ``build_app()`` constructs the window without starting the
event loop (useful for headless verification); ``main()`` builds it and
runs ``mainloop()``.
"""
from __future__ import annotations

import tkinter as tk
from tkinter import ttk

from lib_core.animals import Cat, Dog, Parrot, PoliceDog
from lib_core.shapes import Circle, Rectangle, Square, Triangle

from app_gui.views import AnimalPanel, ShapePanel


class MainWindowwww(tk.Tk):
    """Top-level window: a notebook with a Shapes tab and an Animals tab."""

    def __init__(self) -> None:
        super().__init__()
        self.title("HelixStudio GUI demo")
        self.geometry("420x320")

        notebook = ttk.Notebook(self)
        notebook.pack(fill="both", expand=True)

        self.shape_panel = ShapePanel(notebook)
        notebook.add(self.shape_panel, text="Shapes")

        self.animal_panel = AnimalPanel(notebook)
        notebook.add(self.animal_panel, text="Animals")

        self._populate()

    def _populate(self) -> None:
        """Fill both panels with a small, representative sample."""
        self.shape_panel.set_shapes([
            Circle(1.0),
            Rectangle(2.0, 3.0),
            Square(4.0),
            Triangle(5.0, 6.0),
        ])
        self.animal_panel.set_animals([
            ("Whiskers", Cat("Whiskers")),
            ("Rex", Dog("Rex")),
            ("K9", PoliceDog("K9", badge=42)),
            ("Polly", Parrot("Polly", owner="Alice")),
        ])


def build_app() -> MainWindowwww:
    """Construct the window without starting the event loop."""
    return MainWindowwww()


def main() -> None:
    """Entry point: build the window and run the Tk event loop."""
    app = build_app()
    app.mainloop()


if __name__ == "__main__":
    main()
