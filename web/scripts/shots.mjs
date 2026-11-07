// Writes README screenshots from mock data. Usage: npm run shots
import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { discoverRunId, launchEdge, openView, startServer, VIEWS } from "./_harness.mjs";

const out = fileURLToPath(new URL("../../docs/images/", import.meta.url));
mkdirSync(out, { recursive: true });

const server = await startServer();
const edge = await launchEdge();
try {
  const probe = await edge.browser.newPage();
  const runId = await discoverRunId(probe, server.base);
  await probe.close();
  const views = Object.fromEntries(VIEWS(runId).map((v) => [v.name, v]));

  const jobs = [
    ["compare-dark.png", "compare", "dark", { width: 1280, height: 900 }, false],
    ["compare-light.png", "compare", "light", { width: 1280, height: 900 }, false],
    ["compare-mobile.png", "compare", "dark", { width: 375, height: 812 }, true],
    ["trend-dark.png", "trend", "dark", { width: 1280, height: 800 }, false],
    ["run-detail-dark.png", "run-detail", "dark", { width: 1280, height: 900 }, false],
  ];
  for (const [file, view, scheme, viewport, mobile] of jobs) {
    const ctx = await edge.browser.newContext({
      viewport,
      colorScheme: scheme,
      reducedMotion: "reduce",
      deviceScaleFactor: mobile ? 2 : 1,
      isMobile: mobile,
      hasTouch: mobile,
    });
    const page = await ctx.newPage();
    await openView(page, server.base, views[view]);
    await page.screenshot({ path: out + file, fullPage: true });
    console.log("wrote", out + file);
    await ctx.close();
  }
} finally {
  await edge.close();
  server.stop();
}
