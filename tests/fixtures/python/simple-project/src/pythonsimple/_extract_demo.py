"""Target for the Extract Variable refactoring.

Select the sub-expression on the return line and run Extract Variable: it lifts the
expression onto a new assignment above and replaces the selection with the variable.
"""


def total_price(price, qty):
    return price * qty * 1.2
