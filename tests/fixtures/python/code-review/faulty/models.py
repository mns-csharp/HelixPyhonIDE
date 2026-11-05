"""Domain types for the order example — the faulty variant.

Planted: CR-DES-007 (shared mutable class attribute), CR-COR-006 (self-assignment),
CR-COR-007 (exact float comparison).
"""

from typing import List


class OrderLine:
    """A single product line on an order."""

    def __init__(self, sku: str, quantity: int, unit_price: float) -> None:
        self.sku = sku
        # CR-COR-006: meant to be `self.quantity = quantity`.
        quantity = quantity
        self.unit_price = unit_price

    def total(self) -> float:
        """The line's contribution to the order total."""
        return self.quantity * self.unit_price


class Order:
    """A customer order, built one line at a time."""

    # CR-DES-007: every Order instance shares this one list.
    lines: List[OrderLine] = []

    def __init__(self, order_id: str) -> None:
        self.order_id = order_id

    def add(self, line: OrderLine) -> None:
        """Append a line to this order."""
        self.lines.append(line)

    def total(self) -> float:
        """Sum of every line total on this order."""
        return sum(line.total() for line in self.lines)

    def is_free(self) -> bool:
        """Whether the order costs nothing after rounding."""
        # CR-COR-007: exact equality against a value binary floating point cannot hold.
        return self.total() == 0.1
