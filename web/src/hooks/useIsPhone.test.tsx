import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { useIsPhone } from "./useIsPhone";

function Probe() {
  const isPhone = useIsPhone();
  return <div>{isPhone ? "phone" : "not-phone"}</div>;
}

function mockMatchMedia(matches: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn()
  }));
}

describe("useIsPhone", () => {
  afterEach(() => vi.restoreAllMocks());

  it("reports phone when the viewport is below the sm breakpoint", () => {
    mockMatchMedia(true);
    render(<ThemeProvider theme={theme}><Probe /></ThemeProvider>);
    expect(screen.getByText("phone")).toBeInTheDocument();
  });

  it("reports not-phone when the viewport is at or above sm", () => {
    mockMatchMedia(false);
    render(<ThemeProvider theme={theme}><Probe /></ThemeProvider>);
    expect(screen.getByText("not-phone")).toBeInTheDocument();
  });
});
