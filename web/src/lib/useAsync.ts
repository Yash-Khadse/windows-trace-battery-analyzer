import { useCallback, useEffect, useState } from "react";
import { ApiError, isAbort } from "../api/client";

export type AsyncState<T> =
  | { status: "loading" }
  | { status: "ok"; data: T }
  | { status: "error"; error: ApiError };

/** Runs `fn` with an AbortSignal; aborts on dependency change and unmount. */
export function useAsync<T>(
  fn: (signal: AbortSignal) => Promise<T>,
  deps: readonly unknown[],
): AsyncState<T> & { reload: () => void } {
  const [state, setState] = useState<AsyncState<T>>({ status: "loading" });
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    const ctl = new AbortController();
    setState({ status: "loading" });
    fn(ctl.signal).then(
      (data) => {
        if (!ctl.signal.aborted) setState({ status: "ok", data });
      },
      (e: unknown) => {
        if (ctl.signal.aborted || isAbort(e)) return;
        setState({
          status: "error",
          error:
            e instanceof ApiError
              ? e
              : new ApiError("network", e instanceof Error ? e.message : "Unknown error"),
        });
      },
    );
    return () => ctl.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);

  return { ...state, reload };
}
