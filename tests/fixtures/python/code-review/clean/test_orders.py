"""Tests for the order example."""

import sqlite3

from models import Order, OrderLine
from service import PricingService
from store import OrderStore


def _connection() -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.execute(
        "CREATE TABLE lines (order_id TEXT, sku TEXT, quantity INTEGER, unit_price_cents INTEGER)"
    )
    return connection


def test_order_total_sums_its_lines():
    order = Order("A1")
    order.add(OrderLine("apple", 2, 150))
    order.add(OrderLine("pear", 1, 200))
    assert order.total_cents() == 500


def test_store_round_trips_an_order():
    store = OrderStore(_connection())
    order = Order("A2")
    order.add(OrderLine("plum", 3, 75))
    store.save(order)
    assert store.load("A2").total_cents() == order.total_cents()


def test_gold_tier_takes_ten_percent_off():
    order = Order("A3")
    order.add(OrderLine("fig", 1, 1000))
    assert PricingService("gold").payable_cents(order) == 900
