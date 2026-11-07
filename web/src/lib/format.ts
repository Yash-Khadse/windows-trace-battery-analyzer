const DASH = "—";

export function fmtNum(v: number | null | undefined, digits = 1): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return DASH;
  return v.toLocaleString("en-US", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

/** Signed number, always with an explicit + or - for non-zero values. */
export function fmtSigned(v: number | null | undefined, digits = 1): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return DASH;
  const s = fmtNum(Math.abs(v), digits);
  if (Number(s.replace(/,/g, "")) === 0) return s;
  return `${v < 0 ? "-" : "+"}${s}`;
}

export function fmtPct(v: number | null | undefined, digits = 1): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return DASH;
  return `${fmtSigned(v, digits)}%`;
}

export function fmtP(p: number | null | undefined): string {
  if (p === null || p === undefined || !Number.isFinite(p)) return DASH;
  if (p < 0.001) return "<0.001";
  return p.toFixed(3);
}

export function fmtCI(
  lo: number | null | undefined,
  hi: number | null | undefined,
  digits = 1,
): string {
  if (lo === null || lo === undefined || hi === null || hi === undefined) return DASH;
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return DASH;
  return `[${fmtSigned(lo, digits)}, ${fmtSigned(hi, digits)}]`;
}

export function fmtDelta(d: number | null | undefined): string {
  if (d === null || d === undefined || !Number.isFinite(d)) return DASH;
  return fmtSigned(d, 2);
}

/** Compact large numbers: 1.2M, 34.5k. */
export function fmtCompact(v: number): string {
  if (v === 0) return "0";
  const a = Math.abs(v);
  if (a >= 1e9) return `${(v / 1e9).toFixed(1)}G`;
  if (a >= 1e6) return `${(v / 1e6).toFixed(1)}M`;
  if (a >= 1e4) return `${(v / 1e3).toFixed(1)}k`;
  if (a >= 100) return v.toFixed(0);
  return v.toFixed(a >= 10 ? 1 : 2);
}

export function fmtDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toISOString().replace("T", " ").slice(0, 16) + " UTC";
}

export function fmtDuration(s: number): string {
  const m = Math.floor(s / 60);
  const r = Math.round(s % 60);
  return m > 0 ? `${m}m ${r}s` : `${r}s`;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** Axis tick label (UTC): date only for spans over two days, otherwise date and time. */
export function fmtTick(ms: number, spanMs: number): string {
  const d = new Date(ms);
  const date = `${MONTHS[d.getUTCMonth()]} ${d.getUTCDate()}`;
  if (spanMs > 2 * 86_400_000) return date;
  const hh = String(d.getUTCHours()).padStart(2, "0");
  const mm = String(d.getUTCMinutes()).padStart(2, "0");
  return `${date} ${hh}:${mm}`;
}
