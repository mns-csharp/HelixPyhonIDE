"""Every shape rule 2's test-protocol carve-out permits, in one collectable module.

This file exists to be *opened in the IDE*: with `my_unit_test_package` set to `kind = "test"`,
the Problems panel must stay empty for it. Set that package back to `kind = "library"` and every
module-level `def` below turns into a PY8002 error and the file also becomes a PY8003 error —
which is precisely the contradiction the carve-out resolves (rule 3 requires tests to live in a
test project; rule 2 used to forbid the only shape pytest can collect once they got there).

The counterpart fixture `python-test-sample-non_oop` keeps `kind = "library"` on purpose, so the
two can be opened side by side to see the difference.
"""
from __future__ import annotations

import unittest

import pytest

# Module-level marker, read off the module by name — neither ALL_CAPS nor a class member.
pytestmark = pytest.mark.usefixtures("sample_operands")

# Import-time pytest API: skips this whole module if the dependency is absent. Only works if it
# runs at import time, so it cannot be deferred into a class.
asyncio = pytest.importorskip("asyncio")


# ------------------------------------------------- xunit-style module fixtures
# Fetched by the runner as attributes of the MODULE; a class member of the same
# name is simply never found.

def setup_module(module) -> None:
    module._STARTED = True


def teardown_module(module) -> None:
    module._STARTED = False


def setup_function(function) -> None:
    pass


def teardown_function(function) -> None:
    pass


# --------------------------------------------------- module-level test functions
# pytest collects on `python_functions = test*` at module scope. There is no class
# form of this, and requesting a fixture by parameter name only works here.

def test_operands_arrive_from_the_conftest_fixture(sample_operands) -> None:
    left, right = sample_operands
    assert left + right == 5.0


def test_scaling_factors_compose_fixtures(scaling_factors) -> None:
    assert scaling_factors[-1] == pytest.approx(6.0)


def test_builder_helper_is_a_class(builder) -> None:
    # The helper the carve-out deliberately does NOT exempt.
    assert builder.rows(3) == [0, 1, 2]


@pytest.mark.slow
def test_marked_slow_is_skipped_without_the_flag() -> None:
    assert True


async def test_async_functions_are_collected_too() -> None:
    await asyncio.sleep(0)
    assert True


# ------------------------------------------------------------- class-based forms
# The carve-out ADDS a legal shape; it removes none. Both of these were already
# legal under rule 2, because their tests are methods.

class TestPytestStyleClass:
    def test_addition(self, sample_operands) -> None:
        left, right = sample_operands
        assert left + right == 5.0


class UnittestStyleCase(unittest.TestCase):
    def test_multiplication(self) -> None:
        self.assertEqual(6.0, 2.0 * 3.0)
