import { describe, expect, it, vi } from "vitest";
import { ApiError, createClient } from "./client";
import { parseCompare, parseComparison, parseScenarios, ShapeError } from "./guards";
import { compareFixture, SCENARIOS, SCENARIO_REGRESSION } from "../mocks/fixtures";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("api client", () => {
  it("requests the right URL with encoded params and parses", async () => {
    const f = vi.fn().mockResolvedValue(json(compareFixture(SCENARIO_REGRESSION, "A", "B")));
    const api = createClient("/api/", f as unknown as typeof fetch);
    const res = await api.compare({ scenario: "idle browser", a: "A", b: "B", metric: "all", threshold_pct: 5 });
    expect(f.mock.calls[0]?.[0]).toBe("/api/compare?scenario=idle+browser&a=A&b=B&metric=all&threshold_pct=5");
    expect(f.mock.calls[0]?.[1]).toMatchObject({ credentials: "omit", method: "GET" });
    expect(res.metrics.mean_discharge_mw?.regression).toBe(true);
  });

  it("maps fetch failure to a network error", async () => {
    const f = vi.fn().mockRejectedValue(new TypeError("Failed to fetch"));
    const api = createClient("/api", f as unknown as typeof fetch);
    await expect(api.scenarios()).rejects.toMatchObject({ kind: "network" });
  });

  it("maps HTTP errors using the uniform error body", async () => {
    const f = vi.fn().mockResolvedValue(json({ error: { code: "group_empty", message: "group a has no runs" } }, 404));
    const api = createClient("/api", f as unknown as typeof fetch);
    const err = await api.compare({ scenario: "s", a: "A", b: "B" }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ kind: "http", status: 404, code: "group_empty", message: "group a has no runs" });
  });

  it("rejects malformed payloads as shape errors", async () => {
    const f = vi.fn().mockResolvedValue(json([{ name: 5 }]));
    const api = createClient("/api", f as unknown as typeof fetch);
    await expect(api.scenarios()).rejects.toMatchObject({ kind: "shape" });
  });

  it("reports aborted requests as aborted", async () => {
    const ctl = new AbortController();
    const f = vi.fn().mockImplementation(() => {
      ctl.abort();
      return Promise.reject(new DOMException("x", "AbortError"));
    });
    const api = createClient("/api", f as unknown as typeof fetch);
    await expect(api.scenarios(ctl.signal)).rejects.toMatchObject({ kind: "aborted" });
  });

  it("parses the samples envelope", async () => {
    const body = { run_id: "r1", samples: [{ t_s: 0, discharge_mw: 4100, remaining_mwh: 39000 }] };
    const f = vi.fn().mockResolvedValue(json(body));
    const api = createClient("/api", f as unknown as typeof fetch);
    await expect(api.samples("r1")).resolves.toEqual(body.samples);
    const bad = vi.fn().mockResolvedValue(json([{ t_s: 0, discharge_mw: 1 }]));
    await expect(createClient("/api", bad as unknown as typeof fetch).samples("r1")).rejects.toMatchObject({ kind: "shape" });
  });

  it("returns null samples when the endpoint is missing", async () => {
    const f = vi.fn().mockResolvedValue(json({ error: { code: "not_found", message: "resource not found" } }, 404));
    const api = createClient("/api", f as unknown as typeof fetch);
    await expect(api.samples("abc")).resolves.toBeNull();
  });
});

describe("guards", () => {
  it("accepts fixtures", () => {
    expect(parseScenarios(SCENARIOS)).toHaveLength(3);
    const c = compareFixture(SCENARIO_REGRESSION, "A", "B");
    expect(parseCompare(c).per_process.cpu_ms).toHaveLength(25);
  });

  it("treats missing nullable numbers as null but rejects NaN-like values", () => {
    const base = compareFixture(SCENARIO_REGRESSION, "A", "B")!.metrics.mean_discharge_mw!;
    expect(parseComparison({ ...base, hl_shift: null }).hl_shift).toBeNull();
    expect(() => parseComparison({ ...base, p_value: "0.01" })).toThrow(ShapeError);
    expect(() => parseComparison({ ...base, warnings: [1] })).toThrow(ShapeError);
    expect(() => parseComparison(null)).toThrow(ShapeError);
  });
});
