// Capture the direct Microsoft sign-in experience with the shared product-media mocks.
import path from "node:path";
import { fileURLToPath } from "node:url";
import { installApiMock, freezeAnimations, loadPlaywright, waitForServer } from "./mock-api.mjs";

const root = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));
const base = process.env.PCBRIDGE_CAPTURE_BASE_URL || "http://127.0.0.1:5173";
await waitForServer(base);
const { chromium } = loadPlaywright();
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
  await installApiMock(page);
  for (const [route, file] of [["/settings/microsoft", "pcbridge-microsoft-connections.jpg"], ["/tenants", "pcbridge-tenants.jpg"]]) {
    await page.goto(new URL(route, base).toString(), { waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: "Add tenant with Microsoft", exact: true }).waitFor();
    await freezeAnimations(page);
    if (await page.getByRole("textbox", { name: "Refresh token" }).isVisible()) throw new Error("Manual token recovery is visible by default");
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 960 });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
      if (overflow) throw new Error(`${route} overflows at ${width}px`);
    }
    await page.screenshot({ path: path.join(root, "docs/assets/screenshots", file), type: "jpeg", quality: 92 });
  }
  console.log("Microsoft connections and tenants captures passed, including 390px overflow and hidden manual token recovery.");
} finally { await browser.close(); }
