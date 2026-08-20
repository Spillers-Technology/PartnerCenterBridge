import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";

// Without vitest's `globals: true` (deliberately not set -- explicit per-file imports are
// preferred here), Testing Library's automatic afterEach(cleanup) never registers, so DOM from
// one test leaks into the next test in the same file. Register it explicitly instead.
afterEach(() => {
  cleanup();
});

// jsdom has no matchMedia implementation; MUI's useMediaQuery (and therefore useIsPhone) needs
// one to exist even when a test doesn't care about its value. Individual tests override
// window.matchMedia with vi.fn() when they need to control the result.
if (!window.matchMedia) {
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
}
