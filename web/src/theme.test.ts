import { describe, expect, it } from "vitest";
import { theme } from "./theme";

describe("theme", () => {
  it("defines the azure primary palette", () => {
    expect(theme.palette.primary.main).toBe("#00c2ff");
    expect(theme.palette.primary.light).toBe("#67dcff");
    expect(theme.palette.primary.dark).toBe("#0094d6");
  });

  it("shifts status colors to their accessible dark-mode shade", () => {
    expect(theme.palette.error.main).toBe("#f87171");
    expect(theme.palette.warning.main).toBe("#fbbf24");
    expect(theme.palette.success.main).toBe("#4ade80");
  });

  it("keeps the existing dark surface tones", () => {
    expect(theme.palette.mode).toBe("dark");
    expect(theme.palette.background.default).toBe("#071426");
    expect(theme.palette.background.paper).toBe("#10243b");
    expect(theme.palette.divider).toBe("#334155");
    expect(theme.palette.text.primary).toBe("#e2e8f0");
    expect(theme.palette.text.secondary).toBe("#94a3b8");
  });

  it("uses an 8px border radius", () => {
    expect(theme.shape.borderRadius).toBe(8);
  });
});
