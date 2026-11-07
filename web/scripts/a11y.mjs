// axe (WCAG 2.1 AA) on all views, both themes, 1280 and 375 widths, plus 375px overflow check.
import AxeBuilder from "@axe-core/playwright";
import { discoverRunId, launchEdge, openView, startServer, VIEWS } from "./_harness.mjs";

const server = await startServer();
const edge = await launchEdge();
const counts = { critical: 0, serious: 0, moderate: 0, minor: 0 };
let overflowFail = 0;
try {
  const probe = await edge.browser.newPage();
  const runId = await discoverRunId(probe, server.base);
  await probe.close();
  for (const scheme of ["light", "dark"]) {
    for (const width of [1280, 375]) {
      const ctx = await edge.browser.newContext({
        viewport: { width, height: 812 },
        colorScheme: scheme,
        reducedMotion: "reduce",
      });
      for (const v of VIEWS(runId)) {
        const page = await ctx.newPage();
        await openView(page, server.base, v);
        const res = await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
          .analyze();
        for (const x of res.violations) {
          counts[x.impact ?? "minor"]++;
          console.log(`[${x.impact}] ${v.name} ${scheme} ${width}px: ${x.id} (${x.nodes.length}) ${x.help}`);
          for (const n of x.nodes.slice(0, 2)) console.log("    ", n.target.join(" "), "|", (n.any[0]?.message ?? n.failureSummary ?? "").split("\n")[0]);
        }
        if (width === 375) {
          const sw = await page.evaluate(() => document.documentElement.scrollWidth);
          const ok = sw <= 375;
          if (!ok) overflowFail++;
          console.log(`overflow ${v.name} ${scheme}: scrollWidth=${sw} ${ok ? "ok" : "FAIL"}`);
        }
        await page.close();
      }
      await ctx.close();
    }
  }
} finally {
  await edge.close();
  server.stop();
}
console.log("axe violations by impact:", JSON.stringify(counts), "overflow failures:", overflowFail);
process.exit(counts.critical + counts.serious + overflowFail > 0 ? 1 : 0);
