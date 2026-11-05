"""Domain types for the order example.

Prices are integer cents throughout so that totals are exact.
"""

from dataclasses import dataclass
from typing import List


@dataclass
class OrderLine:
    """A single product line on an order."""

    sku: str
    quantity: int
    unit_price_cents: int

    def total_cents(self) -> int:
        """The line's contribution to the order total."""
        return self.quantity * self.unit_price_cents


class Order:
    """A customer order, built one line at a time."""

    def __init__(self, order_id: str) -> None:
        self.order_id = order_id
        self.lines: List[OrderLine] = []

    def add(self, line: OrderLine) -> None:
        """Append a line to this order."""
        self.lines.append(line)

    def total_cents(self) -> int:
        """Sum of every line total on this order."""
        return sum(line.total_cents() for line in self.lines)
