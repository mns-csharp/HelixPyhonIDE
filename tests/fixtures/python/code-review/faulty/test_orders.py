"""Tests for the order example — the faulty variant.

Planted: CR-TST-001 (a test that asserts nothing).
"""

from models import Order, OrderLine
from service import PricingService


def test_order_total_sums_its_lines():
    order = Order("A1")
    order.add(OrderLine("apple", 2, 1.5))
    assert order.total() == 3.0


def test_pricing_service_runs():
    # CR-TST-001: exercises the code and checks nothing about the result.
    order = Order("A2")
    order.add(OrderLine("pear", 1, 2.0))
    PricingService("gold").discount_percent()
