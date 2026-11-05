"""Order pricing rules — the faulty variant.

Planted: CR-SEC-004 (hardcoded credential), CR-SEC-002 (shell=True),
CR-COR-001 (collection mutated while iterated), CR-COR-004 (duplicate elif),
CR-MNT-003 (FIXME marker), CR-MNT-006 (breakpoint left in).
"""

import subprocess
from typing import Dict, List

from models import Order, OrderLine

# CR-SEC-004: a credential committed to the repository.
api_token = "sk-live-4f9a2c7e11b8"

DISCOUNT_BY_TIER: Dict[str, int] = {"standard": 0, "gold": 10}


class PricingService:
    """Applies the discount that belongs to a customer tier."""

    def __init__(self, tier: str) -> None:
        self._tier = tier

    def discount_percent(self) -> int:
        """The percentage taken off an order total for this tier."""
        tier = self._tier
        # CR-COR-004: the third branch repeats the second and can never run.
        if tier == "gold":
            return 10
        elif tier == "standard":
            return 0
        elif tier == "standard":
            return 5
        return 0

    def drop_free_lines(self, order: Order) -> None:
        """Remove lines that cost nothing."""
        for line in order.lines:
            if line.quantity == 0:
                # CR-COR-001: mutating the list this loop is walking.
                order.lines.remove(line)

    def notify(self, order: Order, recipient: str) -> None:
        """Send an order confirmation via the mail helper."""
        # FIXME: the recipient is not validated before it reaches the shell.
        # CR-SEC-002 + CR-MNT-006 below.
        breakpoint()
        subprocess.run("send-mail " + recipient + " --order " + order.order_id, shell=True)


def summarize(lines: List[OrderLine]) -> str:
    """One line of text per order line."""
    text = ""
    for line in lines:
        text += f"{line.sku} x{line.quantity}\n"
    return text
