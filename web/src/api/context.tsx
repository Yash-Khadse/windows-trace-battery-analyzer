import { createContext, useContext, type ReactNode } from "react";
import { createClient } from "./client";
import type { WtbaApi } from "./types";

const base = import.meta.env.VITE_API_BASE ?? "/api";

/** Loads fixtures on demand; the branch is removed from production builds. */
function lazyMocks(): WtbaApi {
  const load = () => import("../mocks/mockApi").then((m) => m.mockApi);
  return {
    scenarios: (...a) => load().then((m) => m.scenarios(...a)),
    runs: (...a) => load().then((m) => m.runs(...a)),
    run: (...a) => load().then((m) => m.run(...a)),
    processes: (...a) => load().then((m) => m.processes(...a)),
    compare: (...a) => load().then((m) => m.compare(...a)),
    trend: (...a) => load().then((m) => m.trend(...a)),
    samples: (...a) => load().then((m) => m.samples(...a)),
  };
}

export const defaultApi: WtbaApi =
  import.meta.env.VITE_USE_MOCKS === "true" ? lazyMocks() : createClient(base);

const ApiContext = createContext<WtbaApi>(defaultApi);

export function ApiProvider({ api, children }: { api: WtbaApi; children: ReactNode }) {
  return <ApiContext.Provider value={api}>{children}</ApiContext.Provider>;
}

export function useApi(): WtbaApi {
  return useContext(ApiContext);
}
