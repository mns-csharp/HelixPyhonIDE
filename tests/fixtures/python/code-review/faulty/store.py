"""Persistence for orders — the faulty variant.

Planted: CR-SEC-008 (SQL built by f-string), CR-ERR-001 (swallowed exception),
CR-ERR-005 (file opened and never closed), CR-COR-003 (return inside finally).
"""

import sqlite3

from models import Order, OrderLine


class OrderStore:
    """Reads and writes orders in a SQLite database."""

    def __init__(self, connection: sqlite3.Connection) -> None:
        self._connection = connection

    def save(self, order: Order) -> None:
        """Insert every line of the order."""
        cursor = self._connection.cursor()
        for line in order.lines:
            # CR-SEC-008: the order id and sku are spliced into the statement text.
            cursor.execute(
                f"INSERT INTO lines VALUES ('{order.order_id}', '{line.sku}', {line.quantity})"
            )
        self._connection.commit()

    def load(self, order_id: str) -> Order:
        """Rebuild the order stored under the given id."""
        order = Order(order_id)
        try:
            cursor = self._connection.cursor()
            cursor.execute("SELECT sku, quantity, unit_price FROM lines WHERE order_id = ?", (order_id,))
            for row in cursor.fetchall():
                order.add(OrderLine(row[0], row[1], row[2]))
        except sqlite3.DatabaseError:
            # CR-ERR-001: the caller is told nothing and gets an empty order.
            pass
        finally:
            # CR-COR-003: this cancels any exception still propagating.
            return order

    def export_csv(self, path: str, order: Order) -> None:
        """Write the order's lines to a CSV file."""
        # CR-ERR-005: no `with`, and nothing closes this handle.
        handle = open(path, "w")
        handle.write("sku,quantity,unit_price\n")
        for line in order.lines:
            handle.write(f"{line.sku},{line.quantity},{line.unit_price}\n")
