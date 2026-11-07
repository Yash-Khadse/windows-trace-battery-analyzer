"""Tests for wtba.stats."""

from __future__ import annotations

from dataclasses import replace

import numpy as np
import pytest
from hypothesis import given, settings
from hypothesis import strategies as st
from scipy import stats as sps  # type: ignore[import-untyped]

from wtba.stats import (
    MetricComparison,
    benjamini_hochberg,
    cliffs_delta,
    compare_many,
    compare_metric,
    hl_bootstrap_ci,
    hodges_lehmann,
    mann_whitney_u,
    moses_ci,
)
from wtba.stats.compare import _decide

floats = st.floats(-1e6, 1e6, allow_nan=False, allow_infinity=False)
arrays = st.lists(floats, min_size=1, max_size=15).map(np.array)


# ---- known answers ---------------------------------------------------------
def test_mwu_known_answer() -> None:
    # Hand calc: A=[1,2,3], B=[4,5,6] -> U_a=0; exact two-sided p = 2/C(6,3) = 0.1
    u, p = mann_whitney_u([1, 2, 3], [4, 5, 6])
    assert u == 0.0
    assert p == pytest.approx(0.1)


def test_cliffs_delta_known_answer() -> None:
    # Hand count: A=[1,2,3], B=[2,3,4]: b>a in 6 of 9 pairs, b<a in 1 -> 5/9
    assert cliffs_delta([1, 2, 3], [2, 3, 4]) == pytest.approx(5 / 9)
    assert cliffs_delta([2, 3, 4], [1, 2, 3]) == pytest.approx(
        -5 / 9
    )  # positive = B > A
    assert cliffs_delta([1, 2], [3, 4]) == 1.0


def test_hodges_lehmann_known_answer() -> None:
    # diffs of [1,2,3] vs [4,5,6]: 3,4,5,2,3,4,1,2,3 -> sorted median = 3
    assert hodges_lehmann([1, 2, 3], [4, 5, 6]) == 3.0
    assert hodges_lehmann([4, 5, 6], [1, 2, 3]) == -3.0


def test_bh_hand_computed() -> None:
    # p=[.01,.04,.03,.005], m=4. sorted .005,.01,.03,.04
    # scaled .02,.02,.04,.04 -> q = .02,.02,.04,.04 (monotone)
    q = benjamini_hochberg([0.01, 0.04, 0.03, 0.005])
    np.testing.assert_allclose(q, [0.02, 0.04, 0.04, 0.02])


def test_bh_matches_scipy() -> None:
    rng = np.random.default_rng(1)
    p = rng.uniform(size=50) ** 2
    np.testing.assert_allclose(
        benjamini_hochberg(p), sps.false_discovery_control(p, method="bh")
    )


@given(st.lists(st.floats(0, 1), min_size=1, max_size=40))
def test_bh_property(ps: list[float]) -> None:
    p = np.array(ps)
    q = benjamini_hochberg(p)
    assert np.all(q >= p - 1e-12) and np.all(q <= 1.0)
    np.testing.assert_allclose(
        q, sps.false_discovery_control(p, method="bh"), atol=1e-12
    )


@settings(deadline=None)
@given(arrays, arrays)
def test_effect_properties(a: np.ndarray, b: np.ndarray) -> None:
    d = cliffs_delta(a, b)
    assert -1.0 <= d <= 1.0
    assert d == pytest.approx(-cliffs_delta(b, a))
    assert hodges_lehmann(a, b) == pytest.approx(-hodges_lehmann(b, a))
    _, p = mann_whitney_u(a, b)
    assert 0.0 <= p <= 1.0


def test_bootstrap_seeded_and_brackets() -> None:
    a = np.array([10, 11, 12, 13, 14, 15, 16, 17, 18, 19.0])
    b = a + 5
    ci1 = hl_bootstrap_ci(a, b, n_resamples=2000, seed=3)
    assert ci1 == hl_bootstrap_ci(a, b, n_resamples=2000, seed=3)
    assert ci1[0] <= 5.0 <= ci1[1]


# ---- experiments -----------------------------------------------------------
def test_null_experiment_false_positive_rate() -> None:
    rng = np.random.default_rng(12345)
    n_draws = 1000
    hits = 0
    for _ in range(n_draws):
        a = rng.normal(100, 5, 10)
        b = rng.normal(100, 5, 10)
        if mann_whitney_u(a, b)[1] < 0.05:
            hits += 1
    rate = hits / n_draws
    print(f"null false-positive rate: {rate:.3f}")
    # Continuous data => no ties => scipy method='exact'. Exact MWU at N=10 has
    # size <= 0.05 (about 0.043). Binomial sd at 0.05 is
    # 0.0069, so +-4 sd gives [0.022, 0.078].
    assert 0.022 <= rate <= 0.078


def _noisy(rng: np.random.Generator, shift: float) -> tuple[np.ndarray, np.ndarray]:
    a = rng.normal(1000, 30, 10)  # 3% run-to-run noise
    b = rng.normal(1000 * (1 + shift), 30, 10)
    return a, b


def test_injected_10pct_shift_flagged() -> None:
    a, b = _noisy(np.random.default_rng(7), 0.10)
    r = compare_metric(a, b, higher_is_worse=True, seed=1)
    assert r.regression, r.reason
    assert r.pct_change is not None and r.pct_change > 5
    assert r.ci_low > 0 and r.p_value < 0.05 and r.cliffs_delta > 0.5
    assert (r.n_a, r.n_b) == (10, 10) and r.warnings == ()


def test_favorable_shift_not_flagged() -> None:
    a, b = _noisy(np.random.default_rng(7), -0.10)
    r = compare_metric(a, b, higher_is_worse=True)
    assert not r.regression and "favorable" in r.reason
    a, b = _noisy(np.random.default_rng(7), 0.10)
    assert not compare_metric(a, b, higher_is_worse=False).regression


def test_lower_is_worse_decrease_flagged() -> None:
    a, b = _noisy(np.random.default_rng(7), -0.10)
    assert compare_metric(a, b, higher_is_worse=False).regression


def test_small_shift_below_threshold_not_flagged() -> None:
    a, b = _noisy(np.random.default_rng(3), 0.01)
    assert not compare_metric(a, b).regression


# ---- guards / degenerate ---------------------------------------------------
def test_n_below_5_refuses() -> None:
    a = np.array([100.0, 101, 99, 100])
    r = compare_metric(a, a * 1.5)
    assert not r.regression and "refused" in r.reason and r.warnings


def test_n_below_8_warns_but_can_flag() -> None:
    a = np.array([100.0, 101, 99, 100, 102, 98])
    r = compare_metric(a, a * 1.5)
    assert r.warnings and "N < 8" in r.warnings[0]
    assert r.regression


def test_constant_ties_zeros_do_not_crash() -> None:
    cases = [
        (np.ones(10), np.ones(10)),
        (np.zeros(10), np.zeros(10)),
        (np.zeros(10), np.ones(10)),
        (
            np.array([1.0, 1, 1, 2, 2, 2, 3, 3, 3, 3]),
            np.array([1.0, 2, 2, 2, 3, 3, 3, 4, 4, 4]),
        ),
    ]
    for a, b in cases:
        r = compare_metric(a, b, n_resamples=500)
        assert 0.0 <= r.p_value <= 1.0
    assert not compare_metric(np.ones(10), np.ones(10), n_resamples=200).regression
    assert not compare_metric(np.zeros(10), np.ones(10), n_resamples=200).regression


def test_empty_does_not_crash() -> None:
    r = compare_metric([], [1.0, 2.0])
    assert not r.regression


def test_result_is_frozen() -> None:
    r = compare_metric(np.ones(6), np.ones(6), n_resamples=100)
    with pytest.raises(AttributeError):
        r.p_value = 0.0  # type: ignore[misc]


def test_compare_many_uses_q() -> None:
    rng = np.random.default_rng(11)
    metrics = {"shifted": _noisy(rng, 0.10)}
    for i in range(8):
        metrics[f"null{i}"] = _noisy(rng, 0.0)
    out = compare_many(metrics, n_resamples=1000)
    assert out["shifted"].regression
    assert not any(out[f"null{i}"].regression for i in range(8))


# ---- each flag condition must matter (mutation-sensitive) -------------------
_A = np.arange(1000.0, 1100.0, 10.0)


def _passing() -> MetricComparison:
    """A synthetic result that satisfies every regression condition."""
    return MetricComparison(
        n_a=10, n_b=10, median_a=1000.0, median_b=1100.0, hl_shift=100.0,
        ci_low=50.0, ci_high=150.0, boot_ci_low=50.0, boot_ci_high=150.0,
        pct_change=10.0, p_value=0.001, q_value=0.001, cliffs_delta=0.9,
        regression=False, new_activity=False, reason="", warnings=(),
    )  # fmt: skip


def test_decide_baseline_flags() -> None:
    assert _decide(_passing(), True, 5.0, 0.05) == (True, False, "regression")


@pytest.mark.parametrize(
    ("change", "higher_is_worse", "expected_reason"),
    [
        ({"n_a": 4}, True, "refused"),
        ({"n_b": 4}, True, "refused"),
        ({"pct_change": 4.9}, True, "below threshold"),
        ({"pct_change": -4.9, "hl_shift": -49.0, "ci_low": -60.0, "ci_high": -40.0}, False,
         "below threshold"),
        ({"ci_low": -1.0}, True, "includes 0"),
        ({"ci_high": 0.0, "ci_low": -5.0}, True, "includes 0"),
        ({"q_value": 0.05}, True, "q >="),
        ({"q_value": 0.2, "p_value": 0.001}, True, "q >="),
        ({"hl_shift": -100.0, "pct_change": -10.0, "ci_low": -150.0, "ci_high": -50.0},
         True, "favorable"),
        ({}, False, "favorable"),
    ],
)  # fmt: skip
def test_decide_each_condition_blocks(
    change: dict[str, float], higher_is_worse: bool, expected_reason: str
) -> None:
    r = replace(_passing(), **change)  # type: ignore[arg-type]
    flag, new, reason = _decide(r, higher_is_worse, 5.0, 0.05)
    assert not flag and not new
    assert expected_reason in reason


def test_p_below_alpha_but_q_above_alpha_not_flagged() -> None:
    wide = np.arange(1000.0, 1150.0, 15.0)
    shifted = wide + 60.0
    metrics = {"shifted": (wide, shifted)}
    for i in range(20):
        metrics[f"null{i}"] = (wide, wide[::-1].copy())
    out = compare_many(metrics, n_resamples=500)
    r = out["shifted"]
    assert 0.05 / 21 < r.p_value < 0.05
    assert r.q_value >= 0.05
    assert r.pct_change is not None and r.pct_change >= 5.0 and r.ci_low > 0
    assert not r.regression and "q >=" in r.reason
    # the same data as a single test IS a regression (p used as q)
    assert compare_metric(wide, shifted, n_resamples=500).regression


def test_end_to_end_ci_threshold_direction() -> None:
    # below threshold, everything else significant
    tight = np.arange(1000.0, 1010.0)
    r = compare_metric(tight, tight + 20.0, n_resamples=300)
    assert r.p_value < 0.05 and r.ci_low > 0 and not r.regression
    assert "below threshold" in r.reason


# ---- Moses CI --------------------------------------------------------------
def test_moses_ci_known_answer() -> None:
    # n=m=10: P(U<=23) = 0.0222 <= .025 < P(U<=24) -> c = 24; CI = d_(24), d_(77)
    a = np.arange(10.0)
    b = a + 3.5
    lo, hi = moses_ci(a, b)
    d = np.sort((b[:, None] - a[None, :]).ravel())
    assert (lo, hi) == (d[23], d[100 - 24])


def test_moses_ci_coherent_with_mwu_p() -> None:
    rng = np.random.default_rng(99)
    for shift in (0.0, 0.5, 1.0, 1.5):
        for _ in range(100):
            a = rng.normal(0, 1, 10)
            b = rng.normal(shift, 1, 10)
            lo, hi = moses_ci(a, b)
            excludes = not (lo <= 0.0 <= hi)
            assert excludes == (mann_whitney_u(a, b)[1] < 0.05)


def test_moses_ci_normal_approx_branch_runs() -> None:
    rng = np.random.default_rng(5)
    a, b = rng.normal(0, 1, 60), rng.normal(1, 1, 60)  # 3600 pairs > exact limit
    lo, hi = moses_ci(a, b)
    assert 0.5 < lo < hi < 1.6


def test_moses_null_exclusion_rate() -> None:
    rng = np.random.default_rng(2024)
    hits = sum(
        not (lo <= 0.0 <= hi)
        for lo, hi in (
            moses_ci(rng.normal(size=10), rng.normal(size=10)) for _ in range(1000)
        )
    )
    print(f"Moses CI null exclusion rate: {hits / 1000:.3f}")
    assert 0.022 <= hits / 1000 <= 0.078


# ---- other behaviours ------------------------------------------------------
def test_mwu_uses_exact_without_ties_and_asymptotic_with_ties() -> None:
    a, b = [1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0]
    assert mann_whitney_u(a, b)[1] == pytest.approx(2 / 252)  # exact: 2 / C(10,5)
    _, p_tied = mann_whitney_u([1, 2, 2, 3, 4], [2, 5, 6, 7, 8])
    assert 0.0 < p_tied < 1.0


def test_new_activity_when_baseline_median_zero() -> None:
    a = np.zeros(10)
    b = np.array([5.0, 6, 7, 8, 9, 10, 11, 12, 13, 14])
    r = compare_metric(a, b, n_resamples=300)
    assert r.pct_change is None
    assert r.new_activity and not r.regression and "new_activity" in r.reason
    assert any("median of A is 0" in w for w in r.warnings)
    # not for lower-is-worse metrics, nor if the change is not significant, nor N<5
    assert not compare_metric(a, b, higher_is_worse=False, n_resamples=300).new_activity
    assert not compare_metric(a, a, n_resamples=300).new_activity
    assert not compare_metric(a[:4], b[:4], n_resamples=300).new_activity


@pytest.mark.parametrize("bad", [np.nan, np.inf, -np.inf])
def test_non_finite_rejected(bad: float) -> None:
    good = np.ones(6)
    arr = np.array([1.0, 2.0, bad])
    for f in (mann_whitney_u, cliffs_delta, hodges_lehmann, moses_ci, compare_metric):
        with pytest.raises(ValueError, match="non-finite"):
            f(good, arr)


def test_bh_nan_and_validation() -> None:
    q = benjamini_hochberg([0.01, np.nan, 0.04, 0.03, 0.005])
    assert np.isnan(q[1])
    np.testing.assert_allclose(q[[0, 2, 3, 4]], [0.02, 0.04, 0.04, 0.02])
    for bad in ([-0.1, 0.5], [0.5, 1.1]):
        with pytest.raises(ValueError, match="p-values"):
            benjamini_hochberg(bad)


def test_single_test_q_equals_p() -> None:
    r = compare_metric(_A, _A + 100.0, n_resamples=200)
    assert r.q_value == r.p_value


def test_compare_many_distinct_deterministic_bootstrap_seeds() -> None:
    rng = np.random.default_rng(4)
    pair = _noisy(rng, 0.05)
    metrics = {"x": pair, "y": pair}
    out1 = compare_many(metrics, n_resamples=500, seed=1)
    out2 = compare_many(metrics, n_resamples=500, seed=1)
    assert out1 == out2
    assert (out1["x"].boot_ci_low, out1["x"].boot_ci_high) != (
        out1["y"].boot_ci_low,
        out1["y"].boot_ci_high,
    )


def test_disagreement_warning_when_cis_differ_on_zero() -> None:
    # synthetic check of the warning plumbing via data where the CIs split on 0
    rng = np.random.default_rng(8)
    seen = False
    for _ in range(200):
        a, b = rng.normal(0, 1, 10), rng.normal(0.9, 1, 10)
        r = compare_metric(a, b, n_resamples=300)
        moses_ex = not (r.ci_low <= 0 <= r.ci_high)
        boot_ex = not (r.boot_ci_low <= 0 <= r.boot_ci_high)
        assert (moses_ex != boot_ex) == any("disagree" in w for w in r.warnings)
        seen = seen or moses_ex != boot_ex
    assert seen
