"""Naturalistic, non-toy code: full functions with docstrings, validation, and normal
surrounding logic, where exactly one loop in the middle is the vectorization target. This
is the "does it still work when the loop isn't the only thing in the function" test —
selecting just the marked lines (not the whole function) must still produce a correct
match, because in real editing sessions users select a sub-range, not an entire function.
"""
from __future__ import annotations

import math


def normalize_sensor_batch(raw_readings: list[float], baseline: list[float]) -> list[float]:
    """Subtract a per-channel baseline from a batch of raw sensor readings.

    Used by the telemetry ingest pipeline before readings are handed to the anomaly
    detector; `raw_readings` and `baseline` are always the same length (enforced
    upstream by the channel-count check in the ingest service).
    """
    if len(raw_readings) != len(baseline):
        raise ValueError("raw_readings and baseline must have the same length")

    corrected = [0.0] * len(raw_readings)
    # BEGIN REALWORLD_SENSOR_NORMALIZE
    for i in range(len(raw_readings)):
        corrected[i] = raw_readings[i] - baseline[i]
    # END REALWORLD_SENSOR_NORMALIZE

    return corrected


def convert_angles_to_radians_for_plot(degrees: list[float]) -> list[float]:
    """Convert a list of angle measurements (degrees) into radians for the polar plot
    widget, which only accepts radians."""
    # BEGIN REALWORLD_DEG_TO_RAD
    radians = []
    for deg in degrees:
        radians.append(deg * math.pi / 180.0)
    # END REALWORLD_DEG_TO_RAD
    return radians


def total_order_value(line_items: list[float]) -> float:
    """Sum the line-item totals for a customer order, used by the checkout summary."""
    if not line_items:
        return 0.0

    # BEGIN REALWORLD_ORDER_TOTAL
    grand_total = 0
    for amount in line_items:
        grand_total += amount
    # END REALWORLD_ORDER_TOTAL

    return round(grand_total, 2)


def active_usernames(accounts: list[dict]) -> list[str]:
    """Pull out the usernames of accounts flagged active, for the admin dashboard's
    "currently active" widget."""
    names: list[str] = []
    # BEGIN REALWORLD_ACTIVE_USERNAMES
    for account in accounts:
        if account["is_active"]:
            names.append(account["username"])
    # END REALWORLD_ACTIVE_USERNAMES
    return names


def peak_daily_temperature(hourly_readings: list[float]) -> float:
    """Return the peak temperature from a day's worth of hourly readings, for the
    weather-station daily summary report."""
    if not hourly_readings:
        raise ValueError("hourly_readings must not be empty")

    # BEGIN REALWORLD_PEAK_TEMP
    peak = hourly_readings[0]
    for reading in hourly_readings[1:]:
        if reading > peak:
            peak = reading
    # END REALWORLD_PEAK_TEMP

    return peak


def replace_faulty_readings(is_faulty, fallback_reading, raw_reading):
    """Swap in a fallback reading wherever a channel is flagged faulty — an already
    -vectorized helper from the sensor pipeline, kept here as an Un-vectorize target for
    debugging sessions where an engineer wants to step through one sample at a time in
    the debugger."""
    import numpy as np

    # BEGIN REALWORLD_CLAMP_WHERE
    corrected = np.where(is_faulty, fallback_reading, raw_reading)
    # END REALWORLD_CLAMP_WHERE
    return corrected


def cumulative_distance_traveled(step_lengths: list[float]) -> list[float]:
    """Turn a GPS track's per-step distances into a cumulative-distance series, used to
    render the "distance so far" marker on the route map."""
    # BEGIN REALWORLD_CUMULATIVE_DISTANCE
    distance_so_far = 0
    trace = []
    for step in step_lengths:
        distance_so_far += step
        trace.append(distance_so_far)
    # END REALWORLD_CUMULATIVE_DISTANCE
    return trace
