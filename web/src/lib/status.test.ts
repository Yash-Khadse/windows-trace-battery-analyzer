import { describe, expect, it } from "vitest";
import { cmp } from "../mocks/fixtures";
import { statusOf } from "./status";

const base = { medA: 100, shift: 10, half: 4, p: 0.001, q: 0.002, delta: 0.8 };

describe("statusOf", () => {
  it("flags a regression", () => {
    const s = statusOf(cmp({ ...base, nA: 10, nB: 10, regression: true }));
    expect(s.kind).toBe("regression");
    expect(s.label).toBe("Regression");
  });

  it("reports no regression", () => {
    expect(statusOf(cmp({ ...base, nA: 10, nB: 10 })).kind).toBe("none");
    expect(statusOf(cmp({ ...base, nA: 10, nB: 10 })).label).toBe("No regression");
  });

  it("reports new activity", () => {
    expect(statusOf(cmp({ ...base, nA: 10, nB: 10, newActivity: true })).kind).toBe("new_activity");
  });

  it("reports insufficient data when N<5, taking precedence over everything", () => {
    const c = { ...cmp({ ...base, nA: 4, nB: 10, regression: true }), regression: true, new_activity: true };
    const s = statusOf(c);
    expect(s.kind).toBe("insufficient");
    expect(s.label).toContain("N<5");
    expect(statusOf(cmp({ ...base, nA: 10, nB: 3 })).kind).toBe("insufficient");
  });
});
