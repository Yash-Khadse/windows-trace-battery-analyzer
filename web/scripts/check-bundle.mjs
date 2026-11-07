// Fails if mock-only marker strings appear in the production bundle.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const dir = fileURLToPath(new URL("../dist/assets/", import.meta.url));
const markers = ["idle-browser", "video-playback", "file-indexing", "3f9a1c7be204d5a8"];
let bad = 0;
for (const f of readdirSync(dir)) {
  if (!f.endsWith(".js")) continue;
  const text = readFileSync(join(dir, f), "utf8");
  for (const m of markers) {
    if (text.includes(m)) {
      console.error(`mock marker "${m}" found in dist/assets/${f}`);
      bad++;
    }
  }
}
if (bad) process.exit(1);
console.log("bundle clean: no mock markers");
