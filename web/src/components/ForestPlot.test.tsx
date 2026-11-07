import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { compareFixture, SCENARIO_REGRESSION } from "../mocks/fixtures";
import { ForestPlot } from "./ForestPlot";

describe("ForestPlot", () => {
  const rows = compareFixture(SCENARIO_REGRESSION, "A", "B")!.per_process.cpu_ms;

  it("renders one row per process and a zero line", () => {
    render(<ForestPlot title="CPU time" unit="ms" rows={rows} />);
    expect(screen.getAllByTestId("forest-row")).toHaveLength(rows.length);
    expect(screen.getAllByTestId("forest-row")).toHaveLength(25);
    expect(screen.getByTestId("zero-line")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /25 processes/ })).toBeInTheDocument();
  });

  it("marks regressions with a text-bearing title, not color alone", () => {
    render(<ForestPlot title="CPU time" unit="ms" rows={rows} />);
    const edge = screen.getAllByTestId("forest-row").find((r) => r.querySelector("text.rowlabel")?.textContent === "msedge.exe");
    expect(edge?.querySelector("title")?.textContent).toMatch(/Regression/);
    expect(edge?.querySelector("path.mark-regression")).not.toBeNull();
  });

  it("handles an empty row list without crashing", () => {
    render(<ForestPlot title="CPU time" unit="ms" rows={[]} />);
    expect(screen.queryAllByTestId("forest-row")).toHaveLength(0);
    expect(screen.getByTestId("zero-line")).toBeInTheDocument();
  });
});
