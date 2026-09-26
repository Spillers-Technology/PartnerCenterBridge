import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { ThemeProvider } from "@mui/material/styles";
import { vi } from "vitest";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";

/** Renders the current location as text so tests can assert where navigation landed. */
export function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{`${location.pathname}${location.search}`}</div>;
}

/**
 * Renders `ui` inside the app's providers and a MemoryRouter starting at `path`. Pass `route`
 * (e.g. "/people/:tenantId/:userId") when the component reads path params.
 */
export function renderWithRouter(ui: ReactElement, { path = "/", route }: { path?: string; route?: string } = {}) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <MemoryRouter initialEntries={[path]}>
            {route ? (
              <Routes>
                <Route path={route} element={ui} />
              </Routes>
            ) : ui}
            <LocationProbe />
          </MemoryRouter>
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

export function mockMatchMedia(matches: boolean | ((query: string) => boolean)) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: typeof matches === "function" ? matches(query) : matches,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn()
  }));
}
