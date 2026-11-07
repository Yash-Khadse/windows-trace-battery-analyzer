"""Seeded percentile bootstrap for the Hodges-Lehmann shift."""

from __future__ import annotations

import numpy as np
from numpy.typing import ArrayLike

from .effect import as_1d


def hl_bootstrap_ci(
    a: ArrayLike,
    b: ArrayLike,
    n_resamples: int = 10_000,
    seed: int = 0,
    confidence: float = 0.95,
) -> tuple[float, float]:
    """Percentile bootstrap CI of the HL shift (B - A).

    Each group is resampled independently with replacement.
    """
    xa, xb = as_1d(a), as_1d(b)
    if xa.size == 0 or xb.size == 0:
        raise ValueError("empty sample")
    rng = np.random.default_rng(seed)
    na, nb = xa.size, xb.size
    out = np.empty(n_resamples, dtype=np.float64)
    chunk = max(1, 2_000_000 // (na * nb))
    for start in range(0, n_resamples, chunk):
        k = min(chunk, n_resamples - start)
        ra = xa[rng.integers(0, na, size=(k, na))]
        rb = xb[rng.integers(0, nb, size=(k, nb))]
        d = rb[:, :, None] - ra[:, None, :]
        out[start : start + k] = np.median(d.reshape(k, -1), axis=1)
    tail = (1.0 - confidence) / 2.0 * 100.0
    lo, hi = np.percentile(out, [tail, 100.0 - tail])
    return float(lo), float(hi)
