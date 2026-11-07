import {
  ShapeError,
  parseCompare,
  parseErrorBody,
  parseProcesses,
  parseRun,
  parseRunPage,
  parseSamples,
  parseScenarios,
  parseTrend,
} from "./guards";
import type { WtbaApi } from "./types";

export type ApiErrorKind = "network" | "http" | "shape" | "aborted";

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | null;
  readonly code: string | null;
  constructor(
    kind: ApiErrorKind,
    message: string,
    status: number | null = null,
    code: string | null = null,
  ) {
    super(message);
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.code = code;
  }
}

export function isAbort(e: unknown): boolean {
  return (
    (e instanceof DOMException && e.name === "AbortError") ||
    (e instanceof ApiError && e.kind === "aborted")
  );
}

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const u = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined) u.set(k, String(v));
  }
  const s = u.toString();
  return s ? `?${s}` : "";
}

export function createClient(
  base: string,
  fetchImpl: typeof fetch = (...a) => fetch(...a),
): WtbaApi {
  const root = base.replace(/\/+$/, "");

  async function getJson(path: string, signal?: AbortSignal): Promise<unknown> {
    let res: Response;
    try {
      res = await fetchImpl(`${root}${path}`, {
        method: "GET",
        headers: { Accept: "application/json" },
        credentials: "omit",
        signal,
      });
    } catch (e) {
      if (signal?.aborted) throw new ApiError("aborted", "Request cancelled");
      throw new ApiError("network", `Could not reach the API (${(e as Error).message})`);
    }
    let body: unknown = null;
    try {
      body = await res.json();
    } catch {
      body = null;
    }
    if (!res.ok) {
      const eb = parseErrorBody(body);
      throw new ApiError(
        "http",
        eb?.message ?? `API returned HTTP ${res.status}`,
        res.status,
        eb?.code ?? null,
      );
    }
    if (body === null) throw new ApiError("shape", "API returned a non-JSON body");
    return body;
  }

  async function guarded<T>(
    path: string,
    parse: (v: unknown) => T,
    signal?: AbortSignal,
  ): Promise<T> {
    const body = await getJson(path, signal);
    try {
      return parse(body);
    } catch (e) {
      if (e instanceof ShapeError) throw new ApiError("shape", e.message);
      throw e;
    }
  }

  const enc = encodeURIComponent;
  return {
    scenarios: (s) => guarded("/scenarios", parseScenarios, s),
    runs: (p, s) => guarded(`/runs${qs({ ...p })}`, parseRunPage, s),
    run: (id, s) => guarded(`/runs/${enc(id)}`, parseRun, s),
    processes: (id, sort, s) =>
      guarded(`/runs/${enc(id)}/processes${qs({ sort, limit: 50 })}`, parseProcesses, s),
    compare: (p, s) =>
      guarded(
        `/compare${qs({
          scenario: p.scenario,
          a: p.a,
          b: p.b,
          metric: p.metric,
          threshold_pct: p.threshold_pct,
        })}`,
        parseCompare,
        s,
      ),
    trend: (p, s) => guarded(`/trend${qs({ ...p })}`, parseTrend, s),
    async samples(id, s) {
      try {
        return await guarded(`/runs/${enc(id)}/samples`, parseSamples, s);
      } catch (e) {
        if (e instanceof ApiError && e.kind === "http" && e.status === 404) return null;
        throw e;
      }
    },
  };
}
