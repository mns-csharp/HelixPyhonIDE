"""Persistence for orders, backed by SQLite."""

import sqlite3

from models import Order, OrderLine


class OrderStore:
    """Reads and writes orders in a SQLite database."""

    def __init__(self, connection: sqlite3.Connection) -> None:
        self._connection = connection

    def save(self, order: Order) -> None:
        """Insert every line of the order, replacing any rows already stored for it."""
        cursor = self._connection.cursor()
        cursor.execute("DELETE FROM lines WHERE order_id = ?", (order.order_id,))
        for line in order.lines:
            cursor.execute(
                "INSERT INTO lines (order_id, sku, quantity, unit_price_cents) VALUES (?, ?, ?, ?)",
                (order.order_id, line.sku, line.quantity, line.unit_price_cents),
            )
        self._connection.commit()

    def load(self, order_id: str) -> Order:
        """Rebuild the order stored under the given id."""
        cursor = self._connection.cursor()
        cursor.execute(
            "SELECT sku, quantity, unit_price_cents FROM lines WHERE order_id = ?",
            (order_id,),
        )
        order = Order(order_id)
        for row in cursor.fetchall():
            order.add(OrderLine(row[0], row[1], row[2]))
        return order
