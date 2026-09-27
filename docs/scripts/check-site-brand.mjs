// Visual smoke check for the static website. Uses the same optional Playwright install as captures.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { loadPlaywright } from "./mock-api.mjs";
const root = fileURLToPath(new URL("../..", import.meta.url));
const output = path.join(root, "artifacts", "brand-preview");
fs.mkdirSync(output, { recursive: true });
const { chromium } = loadPlaywright();
const browser = await chromium.launch({ headless: true });
try {
  for (const colorScheme of ["dark", "light"]) {
    for (const width of [1440, 390, 320]) {
      const page = await browser.newPage({ viewport: { width, height: 1000 }, colorScheme });
      for (const file of fs.readdirSync(path.join(root, "docs")).filter(f => f.endsWith(".html"))) {
        await page.goto(pathToFileURL(path.join(root, "docs", file)).href);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
        if (overflow) throw new Error(`${file} overflows at ${width}px (${colorScheme})`);
        const brokenImages = await page.locator("img[src]").evaluateAll(async images => {
          await Promise.all(images.map(img => { img.loading = "eager"; return img.decode().catch(() => {}); }));
          return images.filter(img => img.getAttribute("src") && img.naturalWidth === 0).map(img => img.src);
        });
        if (brokenImages.length) throw new Error(`${file}: broken images ${brokenImages.join(", ")}`);
        if (file === "index.html") await page.screenshot({ path: path.join(output, `website-${colorScheme}-${width}.png`) });
      }
      await page.close();
    }
  }
  console.log("Website images and horizontal overflow checks passed at 1440, 390 and 320px in both themes.");
} finally { await browser.close(); }
