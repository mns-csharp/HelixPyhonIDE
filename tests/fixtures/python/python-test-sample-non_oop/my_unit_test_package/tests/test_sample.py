"""Pytest suite covering lib_core and lib_utils.

The sibling source packages (`lib_core`, `lib_utils`) live under
`tests/fixtures/python/multi-package-workspace/src/` rather than being pip-installed, so we make them
importable by prepending that directory to `sys.path` before any project
import. This keeps the suite runnable as `pytest` from any working dir.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

# tests/fixtures/python/multi-package-workspace/my_unit_test_package/tests/test_sample.py
#  -> tests/fixtures/python/multi-package-workspace/src
_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

import pytest  # noqa: E402

from lib_core.animals import Animal, Bird, Cat, Color, Dog, PoliceDog  # noqa: E402
from lib_core.shapes import Circle, Rectangle, Square, Triangle  # noqa: E402
from lib_core.storage import InMemoryStorage  # noqa: E402
from lib_utils.geometry import largest, total_area  # noqa: E402
from lib_utils.logger import Logger  # noqa: E402


# --------------------------------------------------------------------- shapes

class TestCircle:
    def test_area_unit_radius(self):
        # Module hard-codes pi as 3.14159, so we compare against that constant.
        assert Circle(1.0).area() == pytest.approx(3.14159)

    def test_area_scales_with_radius_squared(self):
        c1 = Circle(2.0)
        c2 = Circle(4.0)
        assert c2.area() == pytest.approx(c1.area() * 4)

    def test_zero_radius_is_zero(self):
        assert Circle(0.0).area() == 0.0


class TestRectangle:
    @pytest.mark.parametrize(
        "w,h,expected",
        [(2.0, 3.0, 6.0), (5.0, 5.0, 25.0), (0.0, 10.0, 0.0), (1.5, 4.0, 6.0)],
    )
    def test_area(self, w, h, expected):
        assert Rectangle(w, h).area() == pytest.approx(expected)

    def test_dimensions_stored(self):
        r = Rectangle(3.0, 7.0)
        assert r.width == 3.0
        assert r.height == 70.0


class TestSquare:
    def test_is_a_rectangle(self):
        assert isinstance(Square(4.0), Rectangle)

    def test_equal_sides(self):
        s = Square(5.0)
        assert s.width == 5.0
        assert s.height == 5.0

    def test_area_is_side_squared(self):
        assert Square(6.0).area() == pytest.approx(36.0)


class TestTriangle:
    def test_area_formula(self):
        assert Triangle(10.0, 4.0).area() == pytest.approx(20.0)

    def test_zero_base_or_height(self):
        assert Triangle(0.0, 5.0).area() == 0.0
        assert Triangle(5.0, 0.0).area() == 0.0


# -------------------------------------------------------------------- storage

class TestInMemoryStorage:
    def test_set_then_get(self):
        s = InMemoryStorage()
        s.set("k", 42)
        assert s.get("k") == 42

    def test_get_missing_returns_none(self):
        assert InMemoryStorage().get("nope") is None

    def test_overwrite(self):
        s = InMemoryStorage()
        s.set("k", "first")
        s.set("k", "second")
        assert s.get("k") == "second"

    def test_delete_existing(self):
        s = InMemoryStorage()
        s.set("k", 1)
        s.delete("k")
        assert s.get("k") is None

    def test_delete_missing_is_no_op(self):
        s = InMemoryStorage()
        s.delete("absent")  # must not raise

    def test_supports_arbitrary_value_types(self):
        s = InMemoryStorage()
        s.set("list", [1, 2, 3])
        s.set("dict", {"a": 1})
        assert s.get("list") == [1, 2, 3]
        assert s.get("dict") == {"a": 1}

    def test_isolation_between_instances(self):
        a, b = InMemoryStorage(), InMemoryStorage()
        a.set("k", "from-a")
        assert b.get("k") is None


# -------------------------------------------------------------------- animals

class TestColorEnum:
    def test_distinct_values(self):
        assert {Color.RED.value, Color.GREEN.value, Color.BLUE.value} == {1, 2, 3}

    def test_lookup_by_value(self):
        assert Color(1) is Color.RED
        assert Color(2) is Color.GREEN
        assert Color(3) is Color.BLUE


class TestAnimalsHierarchy:
    def test_animal_is_abstract(self):
        with pytest.raises(TypeError):
            Animal("nameless")  # type: ignore[abstract]

    @pytest.mark.parametrize(
        "cls,expected",
        [(Dog, "woof"), (Cat, "meow"), (Bird, "tweet")],
    )
    def test_subclass_speaks(self, cls, expected):
        assert cls("a").speak() == expected

    def test_name_is_stored(self):
        assert Dog("rex").name == "rex"

    def test_police_dog_inherits_dog_and_animal(self):
        pd = PoliceDog("k9", 17)
        assert isinstance(pd, Dog)
        assert isinstance(pd, Animal)

    def test_police_dog_speak_includes_badge(self):
        pd = PoliceDog("k9", 17)
        out = pd.speak()
        assert "woof" in out
        assert "17" in out

    def test_dog_speak_prints_woof(self, capsys):
        Dog("rex").speak()
        captured = capsys.readouterr()
        assert "woof" in captured.out


# ------------------------------------------------------------------- geometry

class TestTotalArea:
    def test_empty_list_is_zero(self):
        assert total_area([]) == 0.0

    def test_sum_of_rectangles(self):
        shapes = [Rectangle(2, 3), Rectangle(4, 5)]
        assert total_area(shapes) == pytest.approx(2 * 3 + 4 * 5)

    def test_mixed_shapes(self):
        shapes = [Circle(1), Square(2), Triangle(4, 4)]
        expected = 3.14159 + 4.0 + 8.0
        assert total_area(shapes) == pytest.approx(expected)


class TestLargest:
    def test_picks_biggest_rectangle(self):
        small = Rectangle(1, 1)
        big = Rectangle(10, 10)
        mid = Rectangle(3, 3)
        assert largest([small, big, mid]) is big

    def test_single_element(self):
        only = Triangle(3, 4)
        assert largest([only]) is only

    def test_first_when_tied(self):
        a = Rectangle(2, 3)
        b = Rectangle(3, 2)  # same area
        # Strict `>` keeps the earlier element on a tie.
        assert largest([a, b]) is a

    def test_empty_raises(self):
        with pytest.raises(IndexError):
            largest([])


# --------------------------------------------------------------------- logger

class TestLogger:
    def test_repr_format(self):
        assert repr(Logger("APP")) == "Logger(prefix='APP')"

    @pytest.mark.parametrize(
        "method,level",
        [("info", "INFO"), ("warn", "WARN"), ("error", "ERROR")],
    )
    def test_levels_print_with_prefix(self, capsys, method, level):
        log = Logger("APP")
        getattr(log, method)("hello")
        captured = capsys.readouterr()
        assert f"APP {level}: hello" in captured.out

    def test_from_config_returns_configured_logger(self):
        log = Logger.from_config("CFG")
        assert isinstance(log, Logger)
        assert repr(log) == "Logger(prefix='CFG')"

    def test_protected_prefix_is_accessible_for_introspection(self):
        # `_prefix` is protected by convention but Python doesn't enforce it;
        # the test only documents the contract used by the logger.
        log = Logger("X")
        assert log._prefix == "X"


# --------------------------------------------------------------- sanity check

def test_pi_constant_is_close_to_math_pi():
    """Document the deliberate approximation used by Circle.area()."""
    assert abs(3.14159 - math.pi) < 1e-3
