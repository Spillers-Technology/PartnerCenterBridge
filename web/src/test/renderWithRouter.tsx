import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { ThemeProvider } from "@mui/material/styles";
import { vi } from "vitest";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import type { MeProfile } from "../types";
import { WorkbenchProvider, type WorkbenchSession } from "../workbench";

/** A signed-in workbench session; `me: null` is the OIDC/Dev trusted operator (Owner everywhere). */
export function testSession(me: MeProfile | null = null, overrides: Partial<WorkbenchSession> = {}): WorkbenchSession {
  return {
    authMode: me ? "Local" : "Dev",
    me,
    displayName: me?.displayName ?? "tech",
    status: { profile: "Server", version: "0.9.0", authMode: me ? "Local" : "Dev", needsFirstUser: false },
    refreshMe: async () => {},
    ...overrides
  };
}

/** A Local-mode profile holding `role` on each tenant id given. */
export function localMe(roles: Record<string, "Viewer" | "Operator" | "Owner">, extra: Partial<MeProfile> = {}): MeProfile {
  return {
    id: "me", email: "tech@example.com", displayName: "Tech", isSystemAdmin: false, totpEnabled: false,
    tenantAccess: Object.entries(roles).map(([tenantId, role]) => ({ tenantId, tenantName: tenantId, role })),
    instancePermissions: [],
    ...extra
  };
}

/** Renders the current location as text so tests can assert where navigation landed. */
export function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{`${location.pathname}${location.search}`}</div>;
}

/**
 * Renders `ui` inside the app's providers and a MemoryRouter starting at `path`. Pass `route`
 * (e.g. "/people/:tenantId/:userId") when the component reads path params.
 */
export function renderWithRouter(
  ui: ReactElement,
  { path = "/", route, session = testSession() }: { path?: string; route?: string; session?: WorkbenchSession } = {}
) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <WorkbenchProvider value={session}>
            <MemoryRouter initialEntries={[path]}>
              {route ? (
                <Routes>
                  <Route path={route} element={ui} />
                </Routes>
              ) : ui}
              <LocationProbe />
            </MemoryRouter>
          </WorkbenchProvider>
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
