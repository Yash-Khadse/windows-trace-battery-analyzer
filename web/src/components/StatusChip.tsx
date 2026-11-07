import type { Status, StatusKind } from "../lib/status";

function Glyph({ kind }: { kind: StatusKind }) {
  const common = { width: 14, height: 14, viewBox: "0 0 14 14", "aria-hidden": true } as const;
  switch (kind) {
    case "regression":
      return (
        <svg {...common}>
          <path d="M7 1.5 13 12.5H1Z" fill="currentColor" />
        </svg>
      );
    case "none":
      return (
        <svg {...common}>
          <path d="M2.5 7.5 5.5 10.5 11.5 3.5" fill="none" stroke="currentColor" strokeWidth="2" />
        </svg>
      );
    case "new_activity":
      return (
        <svg {...common}>
          <path d="M7 1 13 7 7 13 1 7Z" fill="currentColor" />
        </svg>
      );
    case "insufficient":
      return (
        <svg {...common}>
          <path d="M2 7h10" stroke="currentColor" strokeWidth="2" strokeDasharray="3 2" />
        </svg>
      );
  }
}

export function StatusChip({ status }: { status: Status }) {
  return (
    <span className={`chip chip-${status.kind}`} data-status={status.kind} title={status.description}>
      <Glyph kind={status.kind} />
      {status.label}
    </span>
  );
}
