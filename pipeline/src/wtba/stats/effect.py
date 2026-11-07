"""Nonparametric tests and effect sizes on 1-D numpy arrays."""

from __future__ import annotations

import math
from functools import lru_cache

import numpy as np
from numpy.typing import ArrayLike, NDArray
from scipy import stats  # type: ignore[import-untyped]

FloatArray = NDArray[np.float64]

_EXACT_MAX_PAIRS = 2500  # use exact null distribution of U when n_a * n_b <= this


def as_1d(x: ArrayLike) -> FloatArray:
    """Coerce to a 1-D finite float array. Raises ValueError on NaN/inf."""
    arr = np.asarray(x, dtype=np.float64)
    if arr.ndim != 1:
        raise ValueError("expected a 1-D array")
    if not np.all(np.isfinite(arr)):
        raise ValueError("input contains non-finite values (NaN or inf)")
    return arr


def mann_whitney_u(a: ArrayLike, b: ArrayLike) -> tuple[float, float]:
    """Two-sided Mann-Whitney U via scipy. Returns (U_a, p).

    Uses method='exact' when there are no ties in the pooled sample and
    method='asymptotic' (tie-corrected normal approximation) otherwise.
    Degenerate input (all values identical, or empty) yields p = 1.0.
    """
    xa, xb = as_1d(a), as_1d(b)
    if xa.size == 0 or xb.size == 0:
        return math.nan, 1.0
    pooled = np.concatenate([xa, xb])
    if np.ptp(pooled) == 0.0:
        return xa.size * xb.size / 2.0, 1.0
    has_ties = np.unique(pooled).size < pooled.size
    method = "asymptotic" if has_ties else "exact"
    res = stats.mannwhitneyu(xa, xb, alternative="two-sided", method=method)
    p = float(res.pvalue)
    if math.isnan(p):
        p = 1.0
    return float(res.statistic), min(p, 1.0)


def cliffs_delta(a: ArrayLike, b: ArrayLike) -> float:
    """Cliff's delta = (#(b > a) - #(b < a)) / (n_a * n_b).

    Sign convention: POSITIVE means B tends to be larger than A.
    Range [-1, 1]; 0 for empty input.
    """
    xa, xb = as_1d(a), as_1d(b)
    if xa.size == 0 or xb.size == 0:
        return 0.0
    diff = xb[:, None] - xa[None, :]
    return float((int(np.sum(diff > 0)) - int(np.sum(diff < 0))) / diff.size)


def hodges_lehmann(a: ArrayLike, b: ArrayLike) -> float:
    """Hodges-Lehmann shift: median of all pairwise (b_j - a_i) differences."""
    xa, xb = as_1d(a), as_1d(b)
    if xa.size == 0 or xb.size == 0:
        return math.nan
    return float(np.median((xb[:, None] - xa[None, :]).ravel()))


@lru_cache(maxsize=256)
def _u_null_cdf(n: int, m: int) -> FloatArray:
    """Exact null CDF of the Mann-Whitney U statistic, index u = 0..n*m.

    Coefficients of the Gaussian binomial [n+m choose n]_q, computed as
    prod_{i=1..n} (1 - q^(m+i)) / (1 - q^i), truncated at degree n*m.
    """
    top = n * m
    poly = np.zeros(top + 1)
    poly[0] = 1.0
    for i in range(1, n + 1):
        shifted = np.zeros_like(poly)
        if m + i <= top:
            shifted[m + i :] = poly[: top + 1 - (m + i)]
        poly = poly - shifted
        for j in range(i, top + 1):
            poly[j] += poly[j - i]
    return np.cumsum(poly) / poly.sum()


def _tail_count(n: int, m: int, alpha: float) -> int:
    """Number c of smallest pairwise differences cut from each tail.

    c = k + 1 where k is the largest u with P(U <= u) <= alpha / 2 under H0.
    """
    if n * m <= _EXACT_MAX_PAIRS:
        cdf = _u_null_cdf(n, m)
        return int(np.sum(cdf <= alpha / 2.0))
    z = float(stats.norm.ppf(1.0 - alpha / 2.0))
    sigma = math.sqrt(n * m * (n + m + 1) / 12.0)
    return max(0, math.floor(n * m / 2.0 - z * sigma) + 1)


def moses_ci(a: ArrayLike, b: ArrayLike, alpha: float = 0.05) -> tuple[float, float]:
    """Distribution-free (Moses) CI for the HL shift (B - A).

    Order statistics of the sorted pairwise differences, chosen from the
    Mann-Whitney critical value (exact null distribution for n_a * n_b <= 2500,
    normal approximation otherwise). Coherent with the two-sided MWU test at
    level alpha: excludes 0 iff p < alpha (up to ties). If the samples are so
    small that no order statistic achieves the level, the full range of the
    differences (widened to include 0) is returned.
    """
    xa, xb = as_1d(a), as_1d(b)
    if xa.size == 0 or xb.size == 0:
        raise ValueError("empty sample")
    d = np.sort((xb[:, None] - xa[None, :]).ravel())
    c = _tail_count(xa.size, xb.size, alpha)
    if c <= 0:
        return float(min(d[0], 0.0)), float(max(d[-1], 0.0))
    c = min(c, (d.size + 1) // 2)
    return float(d[c - 1]), float(d[d.size - c])
