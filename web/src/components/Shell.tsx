import { useCallback, useEffect, useState } from "react";
import { NavLink, Outlet } from "react-router-dom";

type Theme = "light" | "dark";

function systemTheme(): Theme {
  return typeof window !== "undefined" &&
    typeof window.matchMedia === "function" &&
    window.matchMedia("(prefers-color-scheme: dark)").matches
    ? "dark"
    : "light";
}

function storedTheme(): Theme | null {
  try {
    const v = localStorage.getItem("wtba-theme");
    return v === "light" || v === "dark" ? v : null;
  } catch {
    return null;
  }
}

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(() => storedTheme() ?? systemTheme());

  useEffect(() => {
    if (storedTheme()) document.documentElement.dataset.theme = theme;
  }, [theme]);

  const toggle = useCallback(() => {
    const next: Theme = theme === "dark" ? "light" : "dark";
    setTheme(next);
    document.documentElement.dataset.theme = next;
    try {
      localStorage.setItem("wtba-theme", next);
    } catch {
      /* storage unavailable: theme still applies for this session */
    }
  }, [theme]);

  return (
    <button type="button" className="btn" onClick={toggle}>
      Switch to {theme === "dark" ? "light" : "dark"} theme
    </button>
  );
}

export function Shell() {
  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className="app-header">
        <div className="app-header-inner">
          <NavLink to="/" className="brand">
            WTBA
            <small>Windows telemetry battery analyzer</small>
          </NavLink>
          <nav className="nav" aria-label="Primary">
            <NavLink to="/" end>
              Compare
            </NavLink>
            <NavLink to="/trend">Trend</NavLink>
          </nav>
          <ThemeToggle />
        </div>
      </header>
      <main id="main" tabIndex={-1}>
        <Outlet />
      </main>
    </>
  );
}
