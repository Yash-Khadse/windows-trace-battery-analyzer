// Shared helpers: start Vite (mocks mode) on a free local port and drive installed Edge.
import { spawn } from "node:child_process";
import net from "node:net";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const root = fileURLToPath(new URL("..", import.meta.url));

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once("error", reject);
    s.listen(0, "127.0.0.1", () => {
      const { port } = s.address();
      s.close(() => resolve(port));
    });
  });
}

export async function startServer() {
  const port = await freePort();
  const vite = join(root, "node_modules", "vite", "bin", "vite.js");
  const child = spawn(
    process.execPath,
    [vite, "--mode", "mocks", "--host", "127.0.0.1", "--port", String(port)],
    { cwd: root, stdio: ["ignore", "pipe", "pipe"] },
  );
  const base = `http://127.0.0.1:${port}`;
  for (let i = 0; i < 100; i++) {
    try {
      if ((await fetch(base + "/")).ok) break;
    } catch {
      /* not up yet */
    }
    await new Promise((r) => setTimeout(r, 200));
    if (i === 99) {
      child.kill();
      throw new Error("Vite did not start");
    }
  }
  return { base, stop: () => child.kill() };
}

export async function launchEdge() {
  const browser = await chromium.launch({
    channel: "msedge",
    headless: true, // Playwright uses a throwaway temp profile, never the real one
  });
  return {
    browser,
    close: async () => {
      await browser.close();
    },
  };
}

/** Finds a mock run id by opening the first trend point. */
export async function discoverRunId(page, base) {
  await page.goto(`${base}/trend?scenario=idle-browser`);
  const pt = page.getByTestId("trend-point").first();
  await pt.waitFor();
  await pt.focus();
  await page.keyboard.press("Enter");
  await page.waitForURL(/\/runs\//);
  return new URL(page.url()).pathname.split("/").pop();
}

export const VIEWS = (runId) => [
  { name: "compare", path: "/?scenario=idle-browser&a=A&b=B", ready: "article" },
  { name: "trend", path: "/trend?scenario=idle-browser", ready: '[data-testid="trend-point"]' },
  { name: "run-detail", path: `/runs/${runId}`, ready: "table.data tbody tr" },
];

export async function openView(page, base, v) {
  await page.goto(base + v.path);
  await page.locator(v.ready).first().waitFor({ timeout: 15000 });
  await page.waitForTimeout(400);
  await page.mouse.move(0, 0);
}
