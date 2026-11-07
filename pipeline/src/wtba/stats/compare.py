"""Metric comparison between two conditions (SPEC section 9)."""

from __future__ import annotations

import math
from collections.abc import Mapping
from dataclasses import dataclass, replace

import numpy as np
from numpy.typing import ArrayLike

from .bootstrap import hl_bootstrap_ci
from .effect import as_1d, cliffs_delta, hodges_lehmann, mann_whitney_u, moses_ci
from .multitest import benjamini_hochberg

WARN_N = 8
REFUSE_N = 5


@dataclass(frozen=True)
class MetricComparison:
    """Result of comparing condition B against condition A (shift = B - A).

    ci_low/ci_high is the headline distribution-free (Moses) 95% CI of the
    Hodges-Lehmann shift; boot_ci_* is the secondary percentile-bootstrap CI.
    q_value equals p_value for a single test (compare_metric) and is the BH
    q-value across metrics in compare_many.
    """

    n_a: int
    n_b: int
    median_a: float
    median_b: float
    hl_shift: float
    ci_low: float
    ci_high: float
    boot_ci_low: float
    boot_ci_high: float
    pct_change: float | None
    p_value: float
    q_value: float
    cliffs_delta: float
    regression: bool
    new_activity: bool
    reason: str
    warnings: tuple[str, ...]


def _decide(
    r: MetricComparison,
    higher_is_worse: bool,
    threshold_pct: float,
    alpha: float,
) -> tuple[bool, bool, str]:
    """Return (regression, new_activity, reason) using r.q_value as the p/q."""
    if min(r.n_a, r.n_b) < REFUSE_N:
        return False, False, f"refused: N < {REFUSE_N} per condition"
    if r.pct_change is None:
        if (
            r.median_a == 0.0
            and higher_is_worse
            and r.q_value < alpha
            and r.median_b > 0.0
            and r.cliffs_delta > 0.0
        ):
            return (
                False,
                True,
                "new_activity: median of A is 0 and B is significantly higher",
            )
        return False, False, "percent change undefined (median of A is 0)"
    if abs(r.pct_change) < threshold_pct:
        return (
            False,
            False,
            f"|change| {abs(r.pct_change):.2f}% below threshold {threshold_pct}%",
        )
    if r.ci_low <= 0.0 <= r.ci_high:
        return False, False, "95% CI includes 0"
    if not r.q_value < alpha:
        return False, False, f"q >= {alpha}"
    worse = r.hl_shift > 0 if higher_is_worse else r.hl_shift < 0
    if not worse:
        return False, False, "change is in the favorable direction"
    return True, False, "regression"


def compare_metric(
    a: ArrayLike,
    b: ArrayLike,
    higher_is_worse: bool = True,
    threshold_pct: float = 5.0,
    alpha: float = 0.05,
    seed: int = 0,
    n_resamples: int = 10_000,
) -> MetricComparison:
    """Compare samples A (baseline) and B as a single test (q = p)."""
    xa, xb = as_1d(a), as_1d(b)
    n_a, n_b = int(xa.size), int(xb.size)
    warns: list[str] = []
    if min(n_a, n_b) < WARN_N:
        warns.append(f"small sample: N < {WARN_N} per condition (n_a={n_a}, n_b={n_b})")
    if min(n_a, n_b) == 0:
        nan = math.nan
        res = MetricComparison(
            n_a, n_b, nan, nan, nan, nan, nan, nan, nan, None, 1.0, 1.0, 0.0,
            False, False, "", tuple(warns),
        )  # fmt: skip
        return replace(
            res, reason=_decide(res, higher_is_worse, threshold_pct, alpha)[2]
        )
    med_a, med_b = float(np.median(xa)), float(np.median(xb))
    hl = hodges_lehmann(xa, xb)
    lo, hi = moses_ci(xa, xb, alpha=0.05)
    blo, bhi = hl_bootstrap_ci(xa, xb, n_resamples=n_resamples, seed=seed)
    if (lo <= 0.0 <= hi) != (blo <= 0.0 <= bhi):
        warns.append(
            "Moses CI and bootstrap CI disagree on whether the shift excludes 0"
        )
    pct = 100.0 * hl / abs(med_a) if med_a != 0.0 else None
    if med_a == 0.0:
        warns.append("median of A is 0: percent change undefined")
    _, p = mann_whitney_u(xa, xb)
    res = MetricComparison(
        n_a, n_b, med_a, med_b, hl, lo, hi, blo, bhi, pct, p, p,
        cliffs_delta(xa, xb), False, False, "", tuple(warns),
    )  # fmt: skip
    flag, new, reason = _decide(res, higher_is_worse, threshold_pct, alpha)
    return replace(res, regression=flag, new_activity=new, reason=reason)


def compare_many(
    metrics: Mapping[str, tuple[ArrayLike, ArrayLike]],
    higher_is_worse: bool | Mapping[str, bool] = True,
    threshold_pct: float = 5.0,
    alpha: float = 0.05,
    seed: int = 0,
    n_resamples: int = 10_000,
) -> dict[str, MetricComparison]:
    """Compare many metrics; BH q-values across them are used for the flags.

    Each metric gets its own bootstrap seed derived from `seed` via
    numpy SeedSequence.spawn (by position in the mapping).
    """
    names = list(metrics)
    children = np.random.SeedSequence(seed).spawn(len(names))
    base: dict[str, tuple[MetricComparison, bool]] = {}
    for name, child in zip(names, children, strict=True):
        hiw = (
            higher_is_worse
            if isinstance(higher_is_worse, bool)
            else higher_is_worse[name]
        )
        a, b = metrics[name]
        sub_seed = int(child.generate_state(1)[0])
        r = compare_metric(a, b, hiw, threshold_pct, alpha, sub_seed, n_resamples)
        base[name] = (r, hiw)
    qs = benjamini_hochberg([base[n][0].p_value for n in names])
    out: dict[str, MetricComparison] = {}
    for name, q in zip(names, qs, strict=True):
        r, hiw = base[name]
        r = replace(r, q_value=float(q))
        flag, new, reason = _decide(r, hiw, threshold_pct, alpha)
        out[name] = replace(r, regression=flag, new_activity=new, reason=reason)
    return out
