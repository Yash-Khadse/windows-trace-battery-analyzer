import { describe, expect, it } from "vitest";
import { fmtCI, fmtDelta, fmtNum, fmtP, fmtPct, fmtSigned } from "./format";

describe("format", () => {
  it("formats p-values", () => {
    expect(fmtP(0.0004)).toBe("<0.001");
    expect(fmtP(0.0456)).toBe("0.046");
    expect(fmtP(1)).toBe("1.000");
    expect(fmtP(null)).toBe("—");
    expect(fmtP(Number.NaN)).toBe("—");
  });

  it("formats signed percent change", () => {
    expect(fmtPct(16.37)).toBe("+16.4%");
    expect(fmtPct(-3.04)).toBe("-3.0%");
    expect(fmtPct(0)).toBe("0.0%");
    expect(fmtPct(-0.01)).toBe("0.0%");
    expect(fmtPct(null)).toBe("—");
  });

  it("formats numbers and signed numbers", () => {
    expect(fmtNum(18400, 0)).toBe("18,400");
    expect(fmtSigned(690, 0)).toBe("+690");
    expect(fmtSigned(-1200.4, 0)).toBe("-1,200");
  });

  it("formats confidence intervals", () => {
    expect(fmtCI(430, 950, 0)).toBe("[+430, +950]");
    expect(fmtCI(-130, 210, 0)).toBe("[-130, +210]");
    expect(fmtCI(null, 3)).toBe("—");
  });

  it("formats Cliff's delta with two decimals", () => {
    expect(fmtDelta(0.824)).toBe("+0.82");
    expect(fmtDelta(-0.1)).toBe("-0.10");
  });
});
