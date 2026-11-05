"""Order pricing rules."""

from typing import Dict

from models import Order

DISCOUNT_PERCENT_BY_TIER: Dict[str, int] = {"standard": 0, "gold": 10}


class PricingService:
    """Applies the discount that belongs to a customer tier."""

    def __init__(self, tier: str) -> None:
        self._tier = tier

    def discount_percent(self) -> int:
        """The percentage taken off an order total for this tier."""
        return DISCOUNT_PERCENT_BY_TIER.get(self._tier, 0)

    def payable_cents(self, order: Order) -> int:
        """The amount due for the order once the tier discount is applied."""
        gross = order.total_cents()
        return gross * (100 - self.discount_percent()) // 100
