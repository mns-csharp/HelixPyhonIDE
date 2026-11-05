"""PEP 695 generic — exercises a name used ONLY in a type-parameter bound.

`Sequence` is referenced solely in the bound `[T: Sequence]`; before the
AST-traversal fix the linter wrongly reported it as an unused import.
"""

from typing import Sequence


def first[T: Sequence](items: T):
    return items[0]
