"""Shared pytest configuration — and the fixture for rule 2's test-protocol carve-out.

Every top-level name in this file is something the pytest runner reads off the MODULE by name.
None of it can move into a class, which is why PY8002 exempts it inside a `kind = "test"`
project rather than asking the user to restructure it:

* ``@pytest.fixture``       -- only shared with other modules when declared at conftest top level;
                               inside a class pytest never finds it and the suite keeps running
                               with the fixture silently unapplied.
* ``pytest_*``              -- the plugin hook surface, looked up as module attributes.
* ``collect_ignore``/glob   -- read by the collector as module attributes, so they can be neither
                               ALL_CAPS constants nor class members.
* ``register_assert_rewrite`` -- must execute BEFORE the module it names is imported, so it cannot
                               be deferred into a class or a fixture.

An ordinary helper is deliberately NOT exempt: see ``TestDataBuilder`` at the bottom, which is a
class because nothing in the runner protocol looks for it by name.
"""
from __future__ import annotations

import pytest
from pytest import fixture

# Import-time pytest API: rewrites assertions in the named module so failures show useful diffs.
# Must run before that module is imported anywhere.
pytest.register_assert_rewrite("my_unit_test_package.assertions")

# Collector configuration, read off the module by name.
collect_ignore = ["_scratch.py"]
collect_ignore_glob = ["*_wip.py"]


# --------------------------------------------------------------- fixtures
# The three decorator forms the carve-out recognises.

@pytest.fixture
def sample_operands() -> tuple:
    """Bare `@pytest.fixture`."""
    return (2.0, 3.0)


@pytest.fixture(scope="module")
def scaling_factors(sample_operands) -> list:
    """Called form, `@pytest.fixture(...)`, and one fixture consuming another."""
    left, right = sample_operands
    return [left, right, left * right]


@fixture
def builder() -> "TestDataBuilder":
    """Bare `@fixture` from `from pytest import fixture`."""
    return TestDataBuilder()


# ------------------------------------------------------------------ hooks
# `pytest_*` — the conftest plugin surface.

def pytest_addoption(parser) -> None:
    parser.addoption("--slow", action="store_true", default=False,
                     help="run the tests marked slow")


def pytest_collection_modifyitems(config, items) -> None:
    if config.getoption("--slow"):
        return
    skip_slow = pytest.mark.skip(reason="needs --slow")
    for item in items:
        if "slow" in item.keywords:
            item.add_marker(skip_slow)


# ---------------------------------------------------------------- helpers
# NOT part of the runner protocol, so rule 2 still applies: this is a class.

class TestDataBuilder:
    """An ordinary helper. The runner never looks for it by name, so it can be — and must be —
    object-oriented like any other code. The carve-out is exactly as wide as the protocol."""

    def operands(self) -> tuple:
        return (2.0, 3.0)

    def rows(self, count: int) -> list:
        return list(range(count))
