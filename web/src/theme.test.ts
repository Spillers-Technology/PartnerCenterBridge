import { describe, expect, it } from "vitest";
import { theme } from "./theme";

describe("theme", () => {
  it("defines the indigo primary palette", () => {
    expect(theme.palette.primary.main).toBe("#818cf8");
    expect(theme.palette.primary.light).toBe("#a5b4fc");
    expect(theme.palette.primary.dark).toBe("#6366f1");
  });

  it("shifts status colors to their accessible dark-mode shade", () => {
    expect(theme.palette.error.main).toBe("#f87171");
    expect(theme.palette.warning.main).toBe("#fbbf24");
    expect(theme.palette.success.main).toBe("#4ade80");
  });

  it("keeps the existing dark surface tones", () => {
    expect(theme.palette.mode).toBe("dark");
    expect(theme.palette.background.default).toBe("#0f172a");
    expect(theme.palette.background.paper).toBe("#1e293b");
    expect(theme.palette.divider).toBe("#334155");
    expect(theme.palette.text.primary).toBe("#e2e8f0");
    expect(theme.palette.text.secondary).toBe("#94a3b8");
  });

  it("uses an 8px border radius", () => {
    expect(theme.shape.borderRadius).toBe(8);
  });
});
