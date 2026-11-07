import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { ApiProvider } from "../api/context";
import { ApiError } from "../api/client";
import type { WtbaApi } from "../api/types";
import { mockApi } from "../mocks/mockApi";
import { ComparePage } from "./ComparePage";
import { TrendPage } from "./TrendPage";

function renderWith(api: WtbaApi, ui: React.ReactElement, url = "/") {
  return render(
    <ApiProvider api={api}>
      <MemoryRouter initialEntries={[url]}>{ui}</MemoryRouter>
    </ApiProvider>,
  );
}

const fast: WtbaApi = {
  ...mockApi,
  scenarios: () => mockApi.scenarios(),
};

describe("ComparePage states", () => {
  it("shows the API-down message with the start command", async () => {
    const down: WtbaApi = {
      ...fast,
      scenarios: () => Promise.reject(new ApiError("network", "Could not reach the API")),
    };
    renderWith(down, <ComparePage />);
    expect(await screen.findByRole("alert")).toHaveTextContent("uv run wtba-api");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });

  it("shows an empty state with guidance when there are no scenarios", async () => {
    renderWith({ ...fast, scenarios: () => Promise.resolve([]) }, <ComparePage />);
    expect(await screen.findByText(/Run the experiment first/)).toBeInTheDocument();
  });

  it("shows loading skeleton first", () => {
    renderWith(fast, <ComparePage />);
    expect(screen.getByRole("status")).toHaveAttribute("aria-busy", "true");
  });

  it("shows regression chips and the threshold for the flagged scenario", async () => {
    renderWith(fast, <ComparePage />, "/?scenario=idle-browser&a=A&b=B");
    await waitFor(() => expect(screen.getAllByRole("article")).toHaveLength(3));
    for (const card of screen.getAllByRole("article")) {
      expect(within(card).getByText("Regression")).toBeInTheDocument();
    }
    expect(screen.getByTestId("compare-summary")).toHaveTextContent("threshold: 5% change");
    expect(screen.getByTestId("compare-summary")).toHaveTextContent("N=10");
    expect(screen.getByText(/1 run\(s\) in B/)).toBeInTheDocument();
    expect(screen.getAllByTestId("forest-row").length).toBe(50);
  });

  it("shows No regression for the clean scenario", async () => {
    renderWith(fast, <ComparePage />, "/?scenario=video-playback&a=A&b=B");
    await waitFor(() => expect(screen.getAllByRole("article")).toHaveLength(3));
    for (const card of screen.getAllByRole("article")) {
      expect(within(card).getByText("No regression")).toBeInTheDocument();
    }
    expect(document.querySelectorAll(".chip-regression")).toHaveLength(0);
  });

  it("shows Insufficient data for N<5", async () => {
    renderWith(fast, <ComparePage />, "/?scenario=file-indexing&a=A&b=B");
    await waitFor(() => expect(screen.getAllByRole("article")).toHaveLength(3));
    for (const card of screen.getAllByRole("article")) {
      expect(within(card).getByText("Insufficient data (N<5)")).toBeInTheDocument();
    }
    expect(screen.getAllByText(/N < 5 per condition/).length).toBeGreaterThan(0);
  });

  it("surfaces a compare error from the API", async () => {
    const api: WtbaApi = {
      ...fast,
      compare: () => Promise.reject(new ApiError("http", "group a has no runs", 404, "group_empty")),
    };
    renderWith(api, <ComparePage />, "/?scenario=idle-browser");
    expect(await screen.findByText("group a has no runs")).toBeInTheDocument();
  });
});

describe("TrendPage", () => {
  it("renders one focusable point per run", async () => {
    renderWith(fast, <TrendPage />, "/trend?scenario=idle-browser&metric=total_cpu_ms");
    const pts = await screen.findAllByTestId("trend-point");
    expect(pts).toHaveLength(19); // 20 runs, 1 failed gate excluded
    expect(pts[0]).toHaveAttribute("tabindex", "0");
    expect(screen.getByText(/1 run\(s\) that failed quality gates/)).toBeInTheDocument();
  });
});
