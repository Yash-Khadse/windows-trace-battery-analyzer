"""Multiple-comparison correction."""

from __future__ import annotations

import numpy as np
from numpy.typing import ArrayLike, NDArray


def benjamini_hochberg(p_values: ArrayLike) -> NDArray[np.float64]:
    """Benjamini-Hochberg q-values (step-up, monotone, capped at 1).

    NaN p-values are excluded from the number of tests and get NaN q.
    Raises ValueError if any non-NaN p is outside [0, 1].
    """
    p = np.asarray(p_values, dtype=np.float64)
    q = np.full(p.shape, np.nan)
    ok = ~np.isnan(p)
    pv = p[ok]
    if pv.size == 0:
        return q
    if np.any(pv < 0.0) or np.any(pv > 1.0):
        raise ValueError("p-values must lie in [0, 1]")
    m = pv.size
    order = np.argsort(pv, kind="stable")
    scaled = pv[order] * m / np.arange(1, m + 1)
    q_sorted = np.minimum.accumulate(scaled[::-1])[::-1]
    qv = np.empty(m, dtype=np.float64)
    qv[order] = np.minimum(q_sorted, 1.0)
    q[ok] = qv
    return q
