import type { ReactNode } from "react";
import type { ApiError } from "../api/client";

export function Skeleton({ label = "Loading", rows = 3 }: { label?: string; rows?: number }) {
  return (
    <div role="status" aria-busy="true" aria-live="polite">
      <span className="sr-only">{label}</span>
      <div style={{ display: "grid", gap: "0.75rem" }} aria-hidden="true">
        {Array.from({ length: rows }, (_, i) => (
          <div key={i} className="skeleton" style={{ height: i === 0 ? 96 : 40 }} />
        ))}
      </div>
    </div>
  );
}

export function CardsSkeleton() {
  return (
    <div role="status" aria-busy="true" aria-live="polite">
      <span className="sr-only">Loading comparison</span>
      <div className="cards" aria-hidden="true">
        {[0, 1, 2].map((i) => (
          <div key={i} className="skeleton" style={{ height: 260 }} />
        ))}
      </div>
    </div>
  );
}

export function EmptyState({
  title,
  children,
  action,
}: {
  title: string;
  children?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <section className="state" role="status">
      <h2>{title}</h2>
      {children}
      {action}
    </section>
  );
}

export function NoDataState() {
  return (
    <EmptyState title="No runs recorded yet">
      <p className="muted">
        The database has no runs for this view. Run the experiment first (simulate or capture, then
        ingest), then reload this page.
      </p>
    </EmptyState>
  );
}

export function ErrorState({ error, onRetry }: { error: ApiError; onRetry?: () => void }) {
  const down = error.kind === "network";
  return (
    <section className="state" role="alert">
      <h2>{down ? "The API is not reachable" : "Something went wrong"}</h2>
      <p>{error.message}</p>
      {down && (
        <>
          <p className="muted">Start the API from the repository root, then retry:</p>
          <pre>uv run wtba-api</pre>
        </>
      )}
      {error.kind === "shape" && (
        <p className="muted">
          The API answered, but not in the expected format. The web and API versions may not match.
        </p>
      )}
      {onRetry && (
        <button type="button" className="btn btn-primary" onClick={onRetry}>
          Retry
        </button>
      )}
    </section>
  );
}
