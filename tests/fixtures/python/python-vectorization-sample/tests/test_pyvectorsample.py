"""Golden-baseline correctness tests for the fixture's original (loop-based / already-
vectorized) implementations, run with plain pytest and no dependency on HelixStudio.

These do NOT test HelixStudio's Vectorize/Un-vectorize refactoring itself (that's
VectorizationFixtureTests.cs, on the C# side) — they pin down that every function in this
fixture computes the answer its docstring claims, so that if HelixStudio ever DID apply a
rewrite, the C# side has a correct "before" value to diff the "after" value against.
"""
from __future__ import annotations

import math

import pytest

from pyvectorsample import edge_cases, forward_patterns, near_misses, real_world_examples, reverse_patterns


# ---- forward_patterns ----

def test_add_elementwise():
    assert forward_patterns.add_elementwise([1.0, 2.0, 3.0], [10.0, 20.0, 30.0]) == [11.0, 22.0, 33.0]


def test_negate_all():
    assert forward_patterns.negate_all([1.0, -2.0, 0.0]) == [-1.0, 2.0, -0.0]


def test_scale_by_two():
    assert forward_patterns.scale_by_two([1.0, 2.5, -3.0]) == [2.0, 5.0, -6.0]


def test_sine_of_each():
    result = forward_patterns.sine_of_each([0.0, math.pi / 2])
    assert result[0] == pytest.approx(0.0)
    assert result[1] == pytest.approx(1.0)


def test_squares_of_positive():
    assert forward_patterns.squares_of_positive([1, 2, 3]) == [1, 4, 9]


def test_keep_positive():
    assert forward_patterns.keep_positive([-1.0, 2.0, 0.0, 3.5]) == [2.0, 3.5]


def test_doubled_positive_readings():
    assert forward_patterns.doubled_positive_readings([-1.0, 2.0, 3.0]) == [4.0, 6.0]


def test_total_of():
    assert forward_patterns.total_of([1.0, 2.0, 3.0]) == 6.0


def test_factorial_like_product():
    assert forward_patterns.factorial_like_product([1, 2, 3, 4]) == 24


def test_highest_reading():
    assert forward_patterns.highest_reading([3.0, 9.0, 1.0]) == 9.0


def test_count_above_threshold():
    assert forward_patterns.count_above_threshold([-1.0, 2.0, 3.0, -4.0]) == 2


def test_any_reading_positive():
    assert forward_patterns.any_reading_positive([-1.0, -2.0, 3.0]) is True
    assert forward_patterns.any_reading_positive([-1.0, -2.0]) is False


def test_all_readings_positive():
    assert forward_patterns.all_readings_positive([1.0, 2.0]) is True
    assert forward_patterns.all_readings_positive([1.0, -2.0]) is False


def test_pick_by_condition():
    result = forward_patterns.pick_by_condition([True, False, True], [1.0, 2.0, 3.0], [10.0, 20.0, 30.0])
    assert result == [1.0, 20.0, 3.0]


def test_running_total():
    assert forward_patterns.running_total([1.0, 2.0, 3.0]) == [1.0, 3.0, 6.0]


def test_doubled_indices():
    assert forward_patterns.doubled_indices(5) == [0, 2, 4, 6, 8]


# ---- reverse_patterns ----

def test_reverse_add_elementwise():
    result = reverse_patterns.add_elementwise([1.0, 2.0], [3.0, 4.0])
    assert list(result) == [4.0, 6.0]


def test_reverse_total_of():
    assert reverse_patterns.total_of([1.0, 2.0, 3.0]) == 6.0


def test_reverse_doubled_positive():
    assert reverse_patterns.doubled_positive([1.0, 2.0, 3.0]) == [2.0, 4.0, 6.0]


def test_reverse_highest_reading():
    assert reverse_patterns.highest_reading([3.0, 9.0, 1.0]) == 9.0


def test_reverse_pick_by_condition():
    result = reverse_patterns.pick_by_condition([True, False], [1.0, 2.0], [10.0, 20.0])
    assert list(result) == [1.0, 20.0]


# ---- near_misses (correctness of the trap itself, independent of matcher behavior) ----

def test_prefix_sum_self_referential():
    assert near_misses.prefix_sum_self_referential([1.0, 1.0, 1.0, 1.0]) == [1.0, 2.0, 3.0, 4.0]


def test_shifted_difference():
    assert near_misses.shifted_difference([1.0, 2.0, 3.0], [10.0, 20.0, 30.0, 40.0]) == [21.0, 32.0, 43.0]


def test_strided_downsample():
    assert near_misses.strided_downsample([1.0, 2.0, 3.0, 4.0]) == [2.0, 2.0, 6.0, 4.0]


def test_accumulator_with_wrong_operator():
    assert near_misses.accumulator_with_wrong_operator([1.0, 2.0, 3.0]) == -6.0


def test_early_exit_scan():
    assert near_misses.early_exit_scan([5.0, 9.0, -1.0, 100.0]) == 9.0


def test_filter_append_with_side_effect():
    assert near_misses.filter_append_with_side_effect([-1.0, 2.0, 3.0]) == [2.0, 3.0]


def test_map_append_indexes_a_different_list():
    assert near_misses.map_append_indexes_a_different_list([1, 2, 3], [10, 10, 10]) == [10, 20, 30]


def test_conditional_fill_with_function_calls():
    result = near_misses.conditional_fill_with_function_calls([True, False], [2.0, 3.0])
    assert result == [4.0, 3.0]


def test_product_with_non_multiplicative_reset():
    assert near_misses.product_with_non_multiplicative_reset([2, 3, 0, 5]) == 5


# ---- edge_cases ----

def test_double_in_place():
    assert edge_cases.double_in_place([1.0, 2.0, 3.0]) == [2.0, 4.0, 6.0]


def test_sum_of_single_element():
    assert edge_cases.sum_of_single_element([42.0]) == 42.0


def test_scale_attribute_array():
    class Container:
        def __init__(self, values):
            self.values = values

    c = Container([1.0, 2.0])
    edge_cases.scale_attribute_array(c)
    assert c.values == [2.0, 4.0]


def test_sum_via_enumerate():
    assert edge_cases.sum_via_enumerate([1.0, 2.0, 3.0]) == 6.0


def test_sum_nested_matrix():
    assert edge_cases.sum_nested_matrix([[1.0, 2.0], [3.0, 4.0]]) == 10.0


# ---- real_world_examples ----

def test_normalize_sensor_batch():
    assert real_world_examples.normalize_sensor_batch([10.0, 20.0], [1.0, 2.0]) == [9.0, 18.0]


def test_normalize_sensor_batch_length_mismatch_raises():
    with pytest.raises(ValueError):
        real_world_examples.normalize_sensor_batch([1.0], [1.0, 2.0])


def test_convert_angles_to_radians_for_plot():
    result = real_world_examples.convert_angles_to_radians_for_plot([180.0])
    assert result[0] == pytest.approx(math.pi)


def test_total_order_value():
    assert real_world_examples.total_order_value([9.995, 4.005]) == 14.0


def test_active_usernames():
    accounts = [{"username": "alice", "is_active": True}, {"username": "bob", "is_active": False}]
    assert real_world_examples.active_usernames(accounts) == ["alice"]


def test_peak_daily_temperature():
    assert real_world_examples.peak_daily_temperature([12.0, 30.0, 18.0]) == 30.0


def test_replace_faulty_readings():
    np = pytest.importorskip("numpy")
    result = real_world_examples.replace_faulty_readings(
        np.array([True, False, True]), np.array([0.0, 0.0, 0.0]), np.array([1.0, 2.0, 3.0])
    )
    assert list(result) == [0.0, 2.0, 0.0]


def test_cumulative_distance_traveled():
    assert real_world_examples.cumulative_distance_traveled([1.0, 2.0, 3.0]) == [1.0, 3.0, 6.0]
