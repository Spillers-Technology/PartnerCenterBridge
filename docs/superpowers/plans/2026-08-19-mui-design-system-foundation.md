# MUI Design System Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adopt Material UI as `web/`'s design system — theme, responsive app shell, and three
shared primitives (`useAsyncAction`, `useConfirm`, `useToast`) — proven out on three real screens
(app shell nav, Login, Register, Dashboard).

**Architecture:** MUI v9 (`@mui/material` + `@emotion/react`/`styled` + `@mui/icons-material`)
layered on top of the existing plain-CSS app via a single `ThemeProvider`/`CssBaseline` at the
React root. New shared hooks live under `web/src/hooks/`; the app shell chrome is extracted into
`web/src/components/AppShell.tsx`, separate from `App.tsx`'s existing tab-routing logic. `styles.css`
and the ~10 not-yet-migrated components are untouched — MUI's Emotion-scoped classes don't collide
with the existing `.grid`/`.row`/`.badge` selectors.

**Tech Stack:** React 19, Vite 8, TypeScript. New: `@mui/material` `@mui/icons-material`
`@emotion/react` `@emotion/styled` (runtime); `vitest` `jsdom` `@testing-library/react`
`@testing-library/jest-dom` `@testing-library/user-event` (dev — this is the first frontend test
infrastructure in this repo).

**Spec:** [docs/superpowers/specs/2026-08-19-mui-design-system-foundation-design.md](../specs/2026-08-19-mui-design-system-foundation-design.md)

## Global Constraints

- MUI version: `@mui/material`/`@mui/icons-material` latest v9.x (confirmed React 19-compatible peer range).
- Breakpoints: MUI defaults only (xs<600, sm 600, md 900, lg 1200, xl 1536) — do not override.
- Theme tokens are exact, copied from the spec: `background.default #0f172a`, `background.paper
  #1e293b`, `divider #334155`, `text.primary #e2e8f0`, `text.secondary #94a3b8`, `shape.borderRadius
  8`, `primary.light #a5b4fc` / `primary.main #818cf8` / `primary.dark #6366f1`, `success.main
  #4ade80`, `warning.main #fbbf24`, `error.main #f87171`.
- Scope boundary: only the app shell, `Login.tsx`, `Register.tsx`, and `Dashboard.tsx` migrate to
  MUI in this plan. Every other component (`Tenants.tsx`, `Contracts.tsx`, `AppTemplates.tsx`,
  `DeployWizard.tsx`, `Deployments.tsx`, `NewHire.tsx`, `Offboard.tsx`, `Workflows.tsx`,
  `Approvals.tsx`, `ConfigSnapshots.tsx`, `Security.tsx`, `UserSearch.tsx`) stays on vanilla CSS —
  do not touch them.
- No `@mui/lab` (e.g. `LoadingButton`) — busy state is a disabled button + label text swap,
  matching the existing pattern.
- No color-only status indication — every `Chip`/status color pairs with a text label.
- ASCII-only string literals in `.tsx`/`.ts` files (repo-wide convention, CLAUDE.md).

---

### Task 1: MUI dependencies, theme tokens, and first frontend test infrastructure

**Files:**
- Modify: `web/package.json`
- Create: `web/vite.config.ts` (replace existing)
- Create: `web/src/setupTests.ts`
- Create: `web/src/theme.ts`
- Create: `web/src/theme.test.ts`
- Modify: `web/src/main.tsx`

**Interfaces:**
- Produces: `theme` (default export from `web/src/theme.ts`, an MUI `Theme`) — every later task's
  tests wrap render output in `<ThemeProvider theme={theme}>`.
- Produces: a working `npm test` (vitest, jsdom environment, jest-dom matchers, a
  `window.matchMedia` polyfill in `setupTests.ts`) — every later task's tests depend on this.

- [ ] **Step 1: Install MUI runtime dependencies**

```bash
cd web
npm install @mui/material @mui/icons-material @emotion/react @emotion/styled
```

- [ ] **Step 2: Install test infrastructure dev dependencies**

```bash
npm install -D vitest jsdom @testing-library/react @testing-library/jest-dom @testing-library/user-event
```

- [ ] **Step 3: Add the `test` script to `web/package.json`**

Add `"test": "vitest run"` to the `scripts` block (alongside the existing `dev`/`build`/`preview`).

- [ ] **Step 4: Replace `web/vite.config.ts` to add the vitest config block**

```ts
import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// The API base is read at runtime from VITE_API_BASE; in dev we proxy /api to the backend.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5080", changeOrigin: true },
      "/health": { target: "http://localhost:5080", changeOrigin: true }
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/setupTests.ts"]
  }
});
```

- [ ] **Step 5: Create `web/src/setupTests.ts`**

```ts
import "@testing-library/jest-dom/vitest";

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
```

- [ ] **Step 6: Write the failing theme test — `web/src/theme.test.ts`**

```ts
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
    expect(theme.palette.background.default).toBe("#0f172a");
    expect(theme.palette.background.paper).toBe("#1e293b");
    expect(theme.palette.divider).toBe("#334155");
  });

  it("uses an 8px border radius", () => {
    expect(theme.shape.borderRadius).toBe(8);
  });
});
```

- [ ] **Step 7: Run the test, verify it fails**

Run: `cd web && npx vitest run src/theme.test.ts`
Expected: FAIL — `Cannot find module './theme'` (file doesn't exist yet).

- [ ] **Step 8: Create `web/src/theme.ts`**

```ts
import { createTheme } from "@mui/material/styles";

export const theme = createTheme({
  palette: {
    mode: "dark",
    background: { default: "#0f172a", paper: "#1e293b" },
    divider: "#334155",
    text: { primary: "#e2e8f0", secondary: "#94a3b8" },
    primary: { light: "#a5b4fc", main: "#818cf8", dark: "#6366f1", contrastText: "#0b1020" },
    success: { main: "#4ade80" },
    warning: { main: "#fbbf24" },
    error: { main: "#f87171" }
  },
  shape: { borderRadius: 8 },
  typography: {
    fontFamily: "system-ui, sans-serif"
  }
});
```

- [ ] **Step 9: Run the test, verify it passes**

Run: `cd web && npx vitest run src/theme.test.ts`
Expected: PASS (4 tests).

- [ ] **Step 10: Wire `ThemeProvider` + `CssBaseline` into `web/src/main.tsx`**

```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { ThemeProvider } from "@mui/material/styles";
import CssBaseline from "@mui/material/CssBaseline";
import { App } from "./App";
import { theme } from "./theme";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <App />
    </ThemeProvider>
  </React.StrictMode>
);
```

- [ ] **Step 11: Verify the full build and existing app still render correctly**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

Run: `cd web && npm run dev`, open `http://localhost:5173` in a browser (or devtools), and eyeball
every existing tab (Dashboard through Config Snapshots). `CssBaseline` resets default browser
margins/fonts globally — confirm the still-vanilla-CSS screens look unchanged (background stays
`#0f172a`, font stays the system-ui stack, no new margins/scrollbars appeared). This is the one
step in this task that must be done by a human/live check, not just the test suite.

- [ ] **Step 12: Commit**

```bash
git add web/package.json web/package-lock.json web/vite.config.ts web/src/setupTests.ts web/src/theme.ts web/src/theme.test.ts web/src/main.tsx
git commit -m "Add MUI theme, ThemeProvider, and first frontend test infrastructure"
```

---

### Task 2: `useIsPhone` and `useAsyncAction` hooks

**Files:**
- Create: `web/src/hooks/useIsPhone.ts`
- Create: `web/src/hooks/useIsPhone.test.tsx`
- Create: `web/src/hooks/useAsyncAction.ts`
- Create: `web/src/hooks/useAsyncAction.test.ts`

**Interfaces:**
- Consumes: `theme` from `web/src/theme.ts` (Task 1).
- Produces: `useIsPhone(): boolean` — used by Task 3 (`ConfirmDialogProvider`'s `fullScreen`) and
  Task 5 (`AppShell`'s Tabs-vs-Drawer switch).
- Produces: `useAsyncAction<Args extends unknown[], T>(action: (...args: Args) => Promise<T>)` →
  `{ status: "idle" | "busy" | "error" | "success", busy: boolean, error: string | null, result: T
  | null, run: (...args: Args) => Promise<T | undefined> }` — used by Tasks 6, 7 (Login, Register).

- [ ] **Step 1: Write the failing test for `useIsPhone` — `web/src/hooks/useIsPhone.test.tsx`**

```tsx
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
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/hooks/useIsPhone.test.tsx`
Expected: FAIL — `Cannot find module './useIsPhone'`.

- [ ] **Step 3: Create `web/src/hooks/useIsPhone.ts`**

```ts
import { useMediaQuery, useTheme } from "@mui/material";

export function useIsPhone(): boolean {
  const theme = useTheme();
  return useMediaQuery(theme.breakpoints.down("sm"));
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/hooks/useIsPhone.test.tsx`
Expected: PASS (2 tests).

- [ ] **Step 5: Write the failing test for `useAsyncAction` — `web/src/hooks/useAsyncAction.test.ts`**

```ts
import { describe, expect, it } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { useAsyncAction } from "./useAsyncAction";

describe("useAsyncAction", () => {
  it("starts idle", () => {
    const { result } = renderHook(() => useAsyncAction(async () => "ok"));
    expect(result.current.status).toBe("idle");
    expect(result.current.busy).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it("goes busy then success on a resolving action, returning the result", async () => {
    const { result } = renderHook(() => useAsyncAction((x: number) => Promise.resolve(x * 2)));

    let pending: Promise<number | undefined>;
    act(() => { pending = result.current.run(21); });
    expect(result.current.busy).toBe(true);

    const returned = await pending!;
    expect(returned).toBe(42);
    await waitFor(() => expect(result.current.status).toBe("success"));
    expect(result.current.result).toBe(42);
    expect(result.current.error).toBeNull();
  });

  it("goes busy then error on a rejecting action, exposing the message", async () => {
    const { result } = renderHook(() => useAsyncAction(async () => { throw new Error("boom"); }));
    await act(async () => { await result.current.run(); });
    expect(result.current.status).toBe("error");
    expect(result.current.error).toBe("boom");
    expect(result.current.result).toBeNull();
  });

  it("stringifies a non-Error throw", async () => {
    // eslint-disable-next-line @typescript-eslint/no-throw-literal
    const { result } = renderHook(() => useAsyncAction(async () => { throw "plain string"; }));
    await act(async () => { await result.current.run(); });
    expect(result.current.error).toBe("plain string");
  });
});
```

- [ ] **Step 6: Run it, verify it fails**

Run: `cd web && npx vitest run src/hooks/useAsyncAction.test.ts`
Expected: FAIL — `Cannot find module './useAsyncAction'`.

- [ ] **Step 7: Create `web/src/hooks/useAsyncAction.ts`**

```ts
import { useCallback, useState } from "react";

export type AsyncActionState<T> =
  | { status: "idle"; error: null; result: null }
  | { status: "busy"; error: null; result: null }
  | { status: "error"; error: string; result: null }
  | { status: "success"; error: null; result: T };

export function useAsyncAction<Args extends unknown[], T>(action: (...args: Args) => Promise<T>) {
  const [state, setState] = useState<AsyncActionState<T>>({ status: "idle", error: null, result: null });

  const run = useCallback(
    async (...args: Args): Promise<T | undefined> => {
      setState({ status: "busy", error: null, result: null });
      try {
        const result = await action(...args);
        setState({ status: "success", error: null, result });
        return result;
      } catch (e) {
        setState({ status: "error", error: e instanceof Error ? e.message : String(e), result: null });
        return undefined;
      }
    },
    [action]
  );

  return { ...state, busy: state.status === "busy", run };
}
```

- [ ] **Step 8: Run it, verify it passes**

Run: `cd web && npx vitest run src/hooks/useAsyncAction.test.ts`
Expected: PASS (4 tests).

- [ ] **Step 9: Commit**

```bash
git add web/src/hooks/useIsPhone.ts web/src/hooks/useIsPhone.test.tsx web/src/hooks/useAsyncAction.ts web/src/hooks/useAsyncAction.test.ts
git commit -m "Add useIsPhone and useAsyncAction shared hooks"
```

---

### Task 3: `useConfirm` hook + `ConfirmDialogProvider`

**Files:**
- Create: `web/src/hooks/useConfirm.tsx`
- Create: `web/src/hooks/useConfirm.test.tsx`
- Modify: `web/src/main.tsx`

**Interfaces:**
- Consumes: `useIsPhone` (Task 2), `theme` (Task 1).
- Produces: `ConfirmDialogProvider` (component, mounted once in `main.tsx`) and `useConfirm():
  (options: ConfirmOptions) => Promise<boolean>` where `ConfirmOptions = { title: string; message:
  string; confirmLabel?: string; cancelLabel?: string; destructive?: boolean }`. Every workstream-2
  destructive action will call `if (!(await confirm({...}))) return;` — not consumed by any task in
  *this* plan, but the contract other work depends on.

- [ ] **Step 1: Write the failing test — `web/src/hooks/useConfirm.test.tsx`**

```tsx
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider, useConfirm } from "./useConfirm";

function Harness({ onResult }: { onResult: (v: boolean) => void }) {
  const confirm = useConfirm();
  return (
    <button
      onClick={async () =>
        onResult(await confirm({ title: "Remove passkey?", message: "This cannot be undone.", destructive: true }))
      }
    >
      Ask
    </button>
  );
}

function renderHarness(onResult: (v: boolean) => void) {
  return render(
    <ThemeProvider theme={theme}>
      <ConfirmDialogProvider>
        <Harness onResult={onResult} />
      </ConfirmDialogProvider>
    </ThemeProvider>
  );
}

describe("useConfirm / ConfirmDialogProvider", () => {
  it("resolves true when the confirm button is clicked", async () => {
    const user = userEvent.setup();
    const results: boolean[] = [];
    renderHarness((v) => results.push(v));

    await user.click(screen.getByRole("button", { name: "Ask" }));
    expect(await screen.findByText("Remove passkey?")).toBeInTheDocument();
    expect(screen.getByText("This cannot be undone.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Confirm" }));
    expect(results).toEqual([true]);
  });

  it("resolves false when cancelled, and custom labels render", async () => {
    const user = userEvent.setup();
    const results: boolean[] = [];

    function CustomHarness() {
      const confirm = useConfirm();
      return (
        <button
          onClick={async () =>
            results.push(
              await confirm({ title: "Deploy?", message: "To 4 tenants.", confirmLabel: "Deploy", cancelLabel: "Not now" })
            )
          }
        >
          Ask
        </button>
      );
    }

    render(
      <ThemeProvider theme={theme}>
        <ConfirmDialogProvider>
          <CustomHarness />
        </ConfirmDialogProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "Ask" }));
    await screen.findByText("Deploy?");
    await user.click(screen.getByRole("button", { name: "Not now" }));
    expect(results).toEqual([false]);
  });

  it("throws when used outside the provider", () => {
    function Bare() {
      useConfirm();
      return null;
    }
    expect(() => render(<Bare />)).toThrow("useConfirm must be used within a ConfirmDialogProvider");
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/hooks/useConfirm.test.tsx`
Expected: FAIL — `Cannot find module './useConfirm'`.

- [ ] **Step 3: Create `web/src/hooks/useConfirm.tsx`**

```tsx
import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogTitle from "@mui/material/DialogTitle";
import { useIsPhone } from "./useIsPhone";

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
}

type ConfirmFn = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<ConfirmFn | null>(null);

interface PendingConfirm {
  options: ConfirmOptions;
  resolve: (value: boolean) => void;
}

export function ConfirmDialogProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<PendingConfirm | null>(null);
  const isPhone = useIsPhone();

  const confirm = useCallback<ConfirmFn>(
    (options) => new Promise<boolean>((resolve) => setPending({ options, resolve })),
    []
  );

  const close = (value: boolean) => {
    pending?.resolve(value);
    setPending(null);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog open={pending !== null} onClose={() => close(false)} fullScreen={isPhone}>
        <DialogTitle>{pending?.options.title}</DialogTitle>
        <DialogContent>
          <DialogContentText>{pending?.options.message}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => close(false)}>{pending?.options.cancelLabel ?? "Cancel"}</Button>
          <Button
            onClick={() => close(true)}
            color={pending?.options.destructive ? "error" : "primary"}
            variant="contained"
            autoFocus
          >
            {pending?.options.confirmLabel ?? "Confirm"}
          </Button>
        </DialogActions>
      </Dialog>
    </ConfirmContext.Provider>
  );
}

export function useConfirm(): ConfirmFn {
  const ctx = useContext(ConfirmContext);
  if (!ctx) throw new Error("useConfirm must be used within a ConfirmDialogProvider");
  return ctx;
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/hooks/useConfirm.test.tsx`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire `ConfirmDialogProvider` into `web/src/main.tsx`**

```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { ThemeProvider } from "@mui/material/styles";
import CssBaseline from "@mui/material/CssBaseline";
import { App } from "./App";
import { theme } from "./theme";
import { ConfirmDialogProvider } from "./hooks/useConfirm";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <ConfirmDialogProvider>
        <App />
      </ConfirmDialogProvider>
    </ThemeProvider>
  </React.StrictMode>
);
```

- [ ] **Step 6: Run the full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

- [ ] **Step 7: Commit**

```bash
git add web/src/hooks/useConfirm.tsx web/src/hooks/useConfirm.test.tsx web/src/main.tsx
git commit -m "Add useConfirm hook and ConfirmDialogProvider"
```

---

### Task 4: `useToast` hook + `ToastProvider`

**Files:**
- Create: `web/src/hooks/useToast.tsx`
- Create: `web/src/hooks/useToast.test.tsx`
- Modify: `web/src/main.tsx`

**Interfaces:**
- Produces: `ToastProvider` (component, mounted once in `main.tsx`) and `useToast():
  (message: string, severity?: "success" | "error" | "warning" | "info") => void`. Not consumed by
  any task in this plan — the contract workstream-2 close-out states (New Hire, Offboard, Deploy)
  will use.

- [ ] **Step 1: Write the failing test — `web/src/hooks/useToast.test.tsx`**

```tsx
import { describe, expect, it } from "vitest";
import { render, screen, waitForElementToBeRemoved } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider, useToast } from "./useToast";

function Harness() {
  const toast = useToast();
  return <button onClick={() => toast("Deployment succeeded", "success")}>Notify</button>;
}

describe("useToast / ToastProvider", () => {
  it("shows a toast and dismisses it on close", async () => {
    const user = userEvent.setup();
    render(
      <ThemeProvider theme={theme}>
        <ToastProvider>
          <Harness />
        </ToastProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "Notify" }));
    expect(await screen.findByText("Deployment succeeded")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /close/i }));
    await waitForElementToBeRemoved(() => screen.queryByText("Deployment succeeded"));
  });

  it("stacks two independent toasts", async () => {
    const user = userEvent.setup();

    function TwoHarness() {
      const toast = useToast();
      return (
        <>
          <button onClick={() => toast("First", "info")}>One</button>
          <button onClick={() => toast("Second", "error")}>Two</button>
        </>
      );
    }

    render(
      <ThemeProvider theme={theme}>
        <ToastProvider>
          <TwoHarness />
        </ToastProvider>
      </ThemeProvider>
    );

    await user.click(screen.getByRole("button", { name: "One" }));
    await user.click(screen.getByRole("button", { name: "Two" }));
    expect(await screen.findByText("First")).toBeInTheDocument();
    expect(await screen.findByText("Second")).toBeInTheDocument();
  });

  it("throws when used outside the provider", () => {
    function Bare() {
      useToast();
      return null;
    }
    expect(() => render(<Bare />)).toThrow("useToast must be used within a ToastProvider");
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/hooks/useToast.test.tsx`
Expected: FAIL — `Cannot find module './useToast'`.

- [ ] **Step 3: Create `web/src/hooks/useToast.tsx`**

```tsx
import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import Alert from "@mui/material/Alert";
import Snackbar from "@mui/material/Snackbar";

export type ToastSeverity = "success" | "error" | "warning" | "info";
interface ToastMessage {
  id: number;
  message: string;
  severity: ToastSeverity;
}
type ToastFn = (message: string, severity?: ToastSeverity) => void;

const ToastContext = createContext<ToastFn | null>(null);

let nextToastId = 1;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastMessage[]>([]);

  const showToast = useCallback<ToastFn>((message, severity = "info") => {
    const id = nextToastId++;
    setToasts((prev) => [...prev, { id, message, severity }]);
  }, []);

  const close = (id: number) => setToasts((prev) => prev.filter((t) => t.id !== id));

  return (
    <ToastContext.Provider value={showToast}>
      {children}
      {toasts.map((t, i) => (
        <Snackbar
          key={t.id}
          open
          autoHideDuration={5000}
          onClose={() => close(t.id)}
          anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
          sx={{ bottom: `${i * 56 + 16}px !important` }}
        >
          <Alert onClose={() => close(t.id)} severity={t.severity} variant="filled" sx={{ width: "100%" }}>
            {t.message}
          </Alert>
        </Snackbar>
      ))}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastFn {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within a ToastProvider");
  return ctx;
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/hooks/useToast.test.tsx`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire `ToastProvider` into `web/src/main.tsx`** (outermost, so both `App` and the
  confirm dialog's own actions could eventually toast)

```tsx
import React from "react";
import ReactDOM from "react-dom/client";
import { ThemeProvider } from "@mui/material/styles";
import CssBaseline from "@mui/material/CssBaseline";
import { App } from "./App";
import { theme } from "./theme";
import { ConfirmDialogProvider } from "./hooks/useConfirm";
import { ToastProvider } from "./hooks/useToast";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <ToastProvider>
        <ConfirmDialogProvider>
          <App />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  </React.StrictMode>
);
```

- [ ] **Step 6: Run the full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

- [ ] **Step 7: Commit**

```bash
git add web/src/hooks/useToast.tsx web/src/hooks/useToast.test.tsx web/src/main.tsx
git commit -m "Add useToast hook and ToastProvider"
```

---

### Task 5: Responsive app shell (`AppShell`) replacing the flat nav

**Files:**
- Create: `web/src/components/AppShell.tsx`
- Create: `web/src/components/AppShell.test.tsx`
- Modify: `web/src/App.tsx:96-133` (the authenticated-app return block)

**Interfaces:**
- Consumes: `useIsPhone` (Task 2).
- Produces: `AppShell` component: `{ tabs: { key: string; label: string }[]; activeTab: string;
  onSelectTab: (key: string) => void; displayName: string | null; onSignOut?: () => void; children:
  ReactNode }`. `App.tsx` becomes the only consumer.

- [ ] **Step 1: Write the failing test — `web/src/components/AppShell.test.tsx`**

```tsx
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { AppShell } from "./AppShell";

const tabs = [
  { key: "dashboard", label: "Dashboard" },
  { key: "tenants", label: "Tenants" }
];

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

function renderShell(props: Partial<React.ComponentProps<typeof AppShell>> = {}) {
  const onSelectTab = vi.fn();
  const onSignOut = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <AppShell
        tabs={tabs}
        activeTab="dashboard"
        onSelectTab={onSelectTab}
        displayName="jspillers"
        onSignOut={onSignOut}
        {...props}
      >
        <div>page content</div>
      </AppShell>
    </ThemeProvider>
  );
  return { onSelectTab, onSignOut };
}

describe("AppShell", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows scrollable Tabs (not a hamburger) at desktop width, and switching tabs calls onSelectTab", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    const { onSelectTab } = renderShell();

    expect(screen.queryByLabelText("Open navigation")).not.toBeInTheDocument();
    await user.click(screen.getByRole("tab", { name: "Tenants" }));
    expect(onSelectTab).toHaveBeenCalledWith("tenants");
  });

  it("shows a hamburger + Drawer at phone width, and picking an item calls onSelectTab", async () => {
    mockMatchMedia(true);
    const user = userEvent.setup();
    const { onSelectTab } = renderShell();

    expect(screen.queryByRole("tab", { name: "Tenants" })).not.toBeInTheDocument();
    await user.click(screen.getByLabelText("Open navigation"));
    await user.click(await screen.findByRole("button", { name: "Tenants" }));
    expect(onSelectTab).toHaveBeenCalledWith("tenants");
  });

  it("shows the display name and triggers onSignOut from the account menu", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    const { onSignOut } = renderShell();

    await user.click(screen.getByLabelText("Account menu"));
    expect(await screen.findByText("jspillers")).toBeInTheDocument();
    await user.click(screen.getByRole("menuitem", { name: "Sign out" }));
    expect(onSignOut).toHaveBeenCalled();
  });

  it("omits the Sign out menu item when onSignOut is not provided", async () => {
    mockMatchMedia(false);
    const user = userEvent.setup();
    renderShell({ onSignOut: undefined });

    await user.click(screen.getByLabelText("Account menu"));
    expect(screen.queryByRole("menuitem", { name: "Sign out" })).not.toBeInTheDocument();
  });

  it("renders children in the main content area", () => {
    mockMatchMedia(false);
    renderShell();
    expect(screen.getByText("page content")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/components/AppShell.test.tsx`
Expected: FAIL — `Cannot find module './AppShell'`.

- [ ] **Step 3: Create `web/src/components/AppShell.tsx`**

```tsx
import { useState, type ReactNode } from "react";
import AppBar from "@mui/material/AppBar";
import Box from "@mui/material/Box";
import Drawer from "@mui/material/Drawer";
import IconButton from "@mui/material/IconButton";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemText from "@mui/material/ListItemText";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Tab from "@mui/material/Tab";
import Tabs from "@mui/material/Tabs";
import Toolbar from "@mui/material/Toolbar";
import Typography from "@mui/material/Typography";
import AccountCircle from "@mui/icons-material/AccountCircle";
import MenuIcon from "@mui/icons-material/Menu";
import { useIsPhone } from "../hooks/useIsPhone";

export interface ShellTab {
  key: string;
  label: string;
}

export function AppShell({
  tabs,
  activeTab,
  onSelectTab,
  displayName,
  onSignOut,
  children
}: {
  tabs: ShellTab[];
  activeTab: string;
  onSelectTab: (key: string) => void;
  displayName: string | null;
  onSignOut?: () => void;
  children: ReactNode;
}) {
  const isPhone = useIsPhone();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);

  const selectTab = (key: string) => {
    onSelectTab(key);
    setDrawerOpen(false);
  };

  return (
    <Box sx={{ display: "flex", flexDirection: "column", minHeight: "100vh" }}>
      <AppBar position="static" color="default" enableColorOnDark>
        <Toolbar>
          {isPhone && (
            <IconButton aria-label="Open navigation" edge="start" onClick={() => setDrawerOpen(true)} sx={{ mr: 1 }}>
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" component="h1" sx={{ flexGrow: isPhone ? 1 : 0, mr: 3, whiteSpace: "nowrap" }}>
            Partner Center Bridge
          </Typography>
          {!isPhone && (
            <Tabs
              value={activeTab}
              onChange={(_, key: string) => selectTab(key)}
              variant="scrollable"
              scrollButtons="auto"
              sx={{ flexGrow: 1, minWidth: 0 }}
            >
              {tabs.map((t) => (
                <Tab key={t.key} value={t.key} label={t.label} />
              ))}
            </Tabs>
          )}
          <IconButton aria-label="Account menu" onClick={(e) => setMenuAnchor(e.currentTarget)} sx={{ ml: 1 }}>
            <AccountCircle />
          </IconButton>
          <Menu anchorEl={menuAnchor} open={menuAnchor !== null} onClose={() => setMenuAnchor(null)}>
            <MenuItem disabled>{displayName}</MenuItem>
            {onSignOut && (
              <MenuItem
                onClick={() => {
                  setMenuAnchor(null);
                  onSignOut();
                }}
              >
                Sign out
              </MenuItem>
            )}
          </Menu>
        </Toolbar>
      </AppBar>

      <Drawer anchor="left" open={isPhone && drawerOpen} onClose={() => setDrawerOpen(false)}>
        <List sx={{ width: 260 }}>
          {tabs.map((t) => (
            <ListItemButton key={t.key} selected={t.key === activeTab} onClick={() => selectTab(t.key)}>
              <ListItemText primary={t.label} />
            </ListItemButton>
          ))}
        </List>
      </Drawer>

      <Box component="main" sx={{ p: { xs: 1.5, sm: 2, md: 3 }, maxWidth: 1100, mx: "auto", width: "100%" }}>
        {children}
      </Box>
    </Box>
  );
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/components/AppShell.test.tsx`
Expected: PASS (5 tests).

- [ ] **Step 5: Read the current authenticated-render block in `web/src/App.tsx`**

The block at `App.tsx:99-133` currently reads:

```tsx
  return (
    <div className="app">
      <header>
        <h1>Partner Center Bridge</h1>
        <nav>
          {allTabs.map((t) => (
            <button key={t.key} className={tab === t.key ? "active" : ""} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </nav>
        <div className="user">
          <span>{displayName}</span>
          {authMode === "Local" && <button onClick={signOutLocal}>Sign out</button>}
          {authMode === "Oidc" && authEnabled && <button onClick={logout}>Sign out</button>}
        </div>
      </header>
      <main>
        {tab === "dashboard" && <Dashboard />}
        {tab === "finduser" && <UserSearch onLaunch={launchWorkflow} />}
        {tab === "tenants" && <Tenants me={me} onProfileChanged={refreshMe} />}
        {tab === "contracts" && <Contracts />}
        {tab === "templates" && <AppTemplates me={me} />}
        {tab === "deploy" && <DeployWizard />}
        {tab === "history" && <Deployments />}
        {tab === "newhire" && <NewHire />}
        {tab === "offboard" && <Offboard />}
        {tab === "workflows" && <Workflows prefill={wfLaunch} />}
        {tab === "approvals" && <Approvals />}
        {tab === "snapshots" && <ConfigSnapshots me={me} />}
        {tab === "security" && me && <Security me={me} onProfileChanged={refreshMe} />}
      </main>
    </div>
  );
```

Replace it with (preserving every existing conditional exactly — only the chrome changes):

```tsx
  const showSignOut = authMode === "Local" || (authMode === "Oidc" && authEnabled);
  const handleSignOut = authMode === "Local" ? signOutLocal : logout;

  return (
    <AppShell
      tabs={allTabs}
      activeTab={tab}
      onSelectTab={(key) => setTab(key as Tab)}
      displayName={displayName ?? null}
      onSignOut={showSignOut ? handleSignOut : undefined}
    >
      {tab === "dashboard" && <Dashboard />}
      {tab === "finduser" && <UserSearch onLaunch={launchWorkflow} />}
      {tab === "tenants" && <Tenants me={me} onProfileChanged={refreshMe} />}
      {tab === "contracts" && <Contracts />}
      {tab === "templates" && <AppTemplates me={me} />}
      {tab === "deploy" && <DeployWizard />}
      {tab === "history" && <Deployments />}
      {tab === "newhire" && <NewHire />}
      {tab === "offboard" && <Offboard />}
      {tab === "workflows" && <Workflows prefill={wfLaunch} />}
      {tab === "approvals" && <Approvals />}
      {tab === "snapshots" && <ConfigSnapshots me={me} />}
      {tab === "security" && me && <Security me={me} onProfileChanged={refreshMe} />}
    </AppShell>
  );
```

Add the import near the top of `App.tsx` (alongside the other component imports):

```tsx
import { AppShell } from "./components/AppShell";
```

- [ ] **Step 6: Run the full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

- [ ] **Step 7: Manual check — narrow-viewport nav**

Run `cd web && npm run dev`, open devtools, set viewport to 375px wide. Confirm: the tab row is
replaced by a hamburger icon; clicking it opens a left `Drawer` listing every tab; picking one
switches the visible content and closes the drawer; no horizontal page scroll appears anywhere in
the header. Then widen to 1280px and confirm the scrollable `Tabs` row is back and the drawer is
gone.

- [ ] **Step 8: Commit**

```bash
git add web/src/components/AppShell.tsx web/src/components/AppShell.test.tsx web/src/App.tsx
git commit -m "Replace flat nav with a responsive AppShell (Tabs + phone Drawer)"
```

---

### Task 6: Migrate `Login.tsx` to MUI

**Files:**
- Modify: `web/src/components/Login.tsx` (full rewrite)
- Create: `web/src/components/Login.test.tsx`

**Interfaces:**
- Consumes: `useAsyncAction` (Task 2). Unchanged: `api`, `setLocalToken`, `getPasskey`,
  `passkeysSupported`, `isMfaChallenge`, `AuthResponse` (all pre-existing, unmodified).

- [ ] **Step 1: Write the failing test — `web/src/components/Login.test.tsx`**

```tsx
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Login } from "./Login";

vi.mock("../api", () => ({
  api: {
    auth: { login: vi.fn() },
    totp: { challenge: vi.fn() },
    passkey: { loginOptions: vi.fn(), loginVerify: vi.fn() }
  }
}));
vi.mock("../webauthn", () => ({ passkeysSupported: false, getPasskey: vi.fn() }));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";

function renderLogin() {
  const onAuthenticated = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <Login onAuthenticated={onAuthenticated} onGoRegister={vi.fn()} />
    </ThemeProvider>
  );
  return onAuthenticated;
}

describe("Login", () => {
  beforeEach(() => vi.clearAllMocks());

  it("signs in with email/password and calls onAuthenticated", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok", user: { id: "u1" } });
  });

  it("shows an error alert when sign-in fails", async () => {
    vi.mocked(api.auth.login).mockRejectedValue(new Error("bad credentials"));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("bad credentials")).toBeInTheDocument();
  });

  it("moves to the MFA step on a challenge response", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ mfaTicket: "ticket-1" });
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByLabelText("6-digit code (or a recovery code)")).toBeInTheDocument();
  });

  it("completes MFA and calls onAuthenticated", async () => {
    vi.mocked(api.auth.login).mockResolvedValue({ mfaTicket: "ticket-1" });
    vi.mocked(api.totp.challenge).mockResolvedValue({ accessToken: "tok2", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderLogin();

    await user.type(screen.getByLabelText("Email"), "a@b.com");
    await user.type(screen.getByLabelText("Password"), "hunter2222222");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    await user.type(await screen.findByLabelText("6-digit code (or a recovery code)"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok2", user: { id: "u1" } });
    expect(api.totp.challenge).toHaveBeenCalledWith("ticket-1", "123456");
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/components/Login.test.tsx`
Expected: FAIL — the current `Login.tsx` renders plain `<input>`s with no `<label>` association, so
`getByLabelText("Email")` etc. cannot find them.

- [ ] **Step 3: Rewrite `web/src/components/Login.tsx`**

```tsx
import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import { getPasskey, passkeysSupported, type LoginOptionsWire } from "../webauthn";
import { isMfaChallenge } from "../types";
import type { AuthResponse } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";

type Step = "start" | "password" | "mfa";

export function Login({ onAuthenticated, onGoRegister }: { onAuthenticated: (r: AuthResponse) => void; onGoRegister: () => void }) {
  const [step, setStep] = useState<Step>("start");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [mfaTicket, setMfaTicket] = useState("");

  const finish = (r: AuthResponse) => {
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  };

  const passkeyAction = useAsyncAction(async () => {
    const { challengeKey, options } = await api.passkey.loginOptions();
    const assertionResponse = await getPasskey(options as LoginOptionsWire);
    finish(await api.passkey.loginVerify(challengeKey, assertionResponse));
  });

  const passwordAction = useAsyncAction(async () => {
    const r = await api.auth.login(email, password);
    if (isMfaChallenge(r)) {
      setMfaTicket(r.mfaTicket);
      setStep("mfa");
    } else {
      finish(r);
    }
  });

  const mfaAction = useAsyncAction(async () => {
    finish(await api.totp.challenge(mfaTicket, code));
  });

  const busy = passkeyAction.busy || passwordAction.busy || mfaAction.busy;
  const error = passkeyAction.error ?? passwordAction.error ?? mfaAction.error;

  if (step === "mfa") {
    return (
      <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
        <Stack
          component="form"
          spacing={2}
          sx={{ width: "100%", maxWidth: 360 }}
          onSubmit={(ev) => {
            ev.preventDefault();
            void mfaAction.run();
          }}
        >
          <Typography variant="h5" component="h1">
            Partner Center Bridge
          </Typography>
          <TextField
            autoFocus
            label="6-digit code (or a recovery code)"
            placeholder="123456"
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={busy}>
            {mfaAction.busy ? "Verifying…" : "Verify"}
          </Button>
          {error && <Alert severity="error">{error}</Alert>}
        </Stack>
      </Box>
    );
  }

  return (
    <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
      <Stack spacing={2} sx={{ width: "100%", maxWidth: 360 }}>
        <Typography variant="h5" component="h1">
          Partner Center Bridge
        </Typography>

        {passkeysSupported && (
          <>
            <Button variant="contained" onClick={() => void passkeyAction.run()} disabled={busy}>
              {passkeyAction.busy ? "Waiting for passkey…" : "Sign in with a passkey"}
            </Button>
            <Typography variant="body2" color="text.secondary">
              or use your password
            </Typography>
          </>
        )}

        <Stack
          component="form"
          spacing={2}
          onSubmit={(ev) => {
            ev.preventDefault();
            void passwordAction.run();
          }}
        >
          <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
          <TextField
            label="Password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={busy}>
            {passwordAction.busy ? "Signing in…" : "Sign in"}
          </Button>
        </Stack>

        <Typography variant="body2" color="text.secondary">
          No account yet? <Button size="small" onClick={onGoRegister}>Register</Button>
        </Typography>

        {error && <Alert severity="error">{error}</Alert>}
      </Stack>
    </Box>
  );
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/components/Login.test.tsx`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

- [ ] **Step 6: Commit**

```bash
git add web/src/components/Login.tsx web/src/components/Login.test.tsx
git commit -m "Migrate Login to MUI, using useAsyncAction"
```

---

### Task 7: Migrate `Register.tsx` to MUI

**Files:**
- Modify: `web/src/components/Register.tsx` (full rewrite)
- Create: `web/src/components/Register.test.tsx`

**Interfaces:**
- Consumes: `useAsyncAction` (Task 2). Unchanged: `api`, `setLocalToken`, `AuthResponse`.

- [ ] **Step 1: Write the failing test — `web/src/components/Register.test.tsx`**

```tsx
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Register } from "./Register";

vi.mock("../api", () => ({ api: { auth: { register: vi.fn() } } }));
vi.mock("../session", () => ({ setLocalToken: vi.fn() }));

import { api } from "../api";

function renderRegister() {
  const onAuthenticated = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <Register onAuthenticated={onAuthenticated} onGoLogin={vi.fn()} />
    </ThemeProvider>
  );
  return onAuthenticated;
}

describe("Register", () => {
  beforeEach(() => vi.clearAllMocks());

  it("registers and calls onAuthenticated", async () => {
    vi.mocked(api.auth.register).mockResolvedValue({ accessToken: "tok", user: { id: "u1" } as never });
    const user = userEvent.setup();
    const onAuthenticated = renderRegister();

    await user.type(screen.getByLabelText("Display name"), "Maya Chen");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Create account" }));

    expect(api.auth.register).toHaveBeenCalledWith("maya@contoso.com", "correct-horse-battery", "Maya Chen");
    expect(onAuthenticated).toHaveBeenCalledWith({ accessToken: "tok", user: { id: "u1" } });
  });

  it("shows an error alert when registration fails", async () => {
    vi.mocked(api.auth.register).mockRejectedValue(new Error("email already registered"));
    const user = userEvent.setup();
    renderRegister();

    await user.type(screen.getByLabelText("Display name"), "Maya Chen");
    await user.type(screen.getByLabelText("Email"), "maya@contoso.com");
    await user.type(screen.getByLabelText("Password (12+ characters)"), "correct-horse-battery");
    await user.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByText("email already registered")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/components/Register.test.tsx`
Expected: FAIL — current `Register.tsx` has unlabeled `<input>`s.

- [ ] **Step 3: Rewrite `web/src/components/Register.tsx`**

```tsx
import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import { setLocalToken } from "../session";
import type { AuthResponse } from "../types";
import { useAsyncAction } from "../hooks/useAsyncAction";

export function Register({ onAuthenticated, onGoLogin }: { onAuthenticated: (r: AuthResponse) => void; onGoLogin: () => void }) {
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");

  const registerAction = useAsyncAction(async () => {
    const r = await api.auth.register(email, password, displayName);
    setLocalToken(r.accessToken);
    onAuthenticated(r);
  });

  return (
    <Box sx={{ display: "grid", placeItems: "center", minHeight: "100vh", p: 2 }}>
      <Stack spacing={2} sx={{ width: "100%", maxWidth: 400 }}>
        <Typography variant="h5" component="h1">
          Create an account
        </Typography>
        <Typography variant="body2" color="text.secondary">
          Registration is open -- your new account starts with no tenant access. Someone who
          already has access to a customer tenant can share it with you afterward, from Tenants.
        </Typography>

        <Stack
          component="form"
          spacing={2}
          onSubmit={(ev) => {
            ev.preventDefault();
            void registerAction.run();
          }}
        >
          <TextField autoFocus label="Display name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          <TextField label="Email" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
          <TextField
            label="Password (12+ characters)"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <Button type="submit" variant="contained" disabled={registerAction.busy}>
            {registerAction.busy ? "Creating account…" : "Create account"}
          </Button>
        </Stack>

        {registerAction.error && <Alert severity="error">{registerAction.error}</Alert>}

        <Typography variant="body2" color="text.secondary">
          Already registered? <Button size="small" onClick={onGoLogin}>Sign in</Button>
        </Typography>
        <Typography variant="body2" color="text.secondary">
          You can add a passkey and enable two-factor authentication afterward, from Security.
        </Typography>
      </Stack>
    </Box>
  );
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/components/Register.test.tsx`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors.

- [ ] **Step 6: Commit**

```bash
git add web/src/components/Register.tsx web/src/components/Register.test.tsx
git commit -m "Migrate Register to MUI, using useAsyncAction"
```

---

### Task 8: Migrate `Dashboard.tsx` to MUI

**Files:**
- Modify: `web/src/components/Dashboard.tsx` (full rewrite)
- Create: `web/src/components/Dashboard.test.tsx`

**Interfaces:**
- Consumes: nothing new from earlier tasks (uses `theme`'s status colors implicitly through MUI's
  `color` props, e.g. `color="warning.main"`, `Chip color="error"`). Unchanged: `api.dashboard()`,
  `Dashboard` type from `../types`.

- [ ] **Step 1: Write the failing test — `web/src/components/Dashboard.test.tsx`**

```tsx
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { Dashboard } from "./Dashboard";

vi.mock("../api", () => ({ api: { dashboard: vi.fn() } }));

import { api } from "../api";

function renderDashboard() {
  return render(
    <ThemeProvider theme={theme}>
      <Dashboard />
    </ThemeProvider>
  );
}

describe("Dashboard", () => {
  it("renders stats and tables once data loads", async () => {
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: {
        tenants: 5, tenantsNoDelegation: 1, deployments: 8, deploymentsFailed: 1,
        deploymentsUpdateAvailable: 2, runsLast24h: 6, runsFailedLast7d: 1
      },
      needsAttention: [
        { kind: "Deployment failed", tenantId: "t1", tenantName: "Fabrikam Inc", subject: "Company Portal branding", detail: "409 conflict", when: "2026-08-19T10:00:00Z" }
      ],
      recentRuns: [
        { id: "r1", workflowId: "mailbox-archive", workflowName: "Mailbox archive repair", tenantId: "t1", tenantName: "Contoso Ltd", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, startedAt: "2026-08-19T09:00:00Z", durationMs: 5210 }
      ]
    });

    renderDashboard();

    expect(await screen.findByText("5")).toBeInTheDocument();
    expect(screen.getByText("Fabrikam Inc")).toBeInTheDocument();
    expect(screen.getByText("Deployment failed")).toBeInTheDocument();
    expect(screen.getByText("Mailbox archive repair")).toBeInTheDocument();
    expect(screen.getByText("ok")).toBeInTheDocument();
  });

  it("shows the all-quiet message when nothing needs attention", async () => {
    vi.mocked(api.dashboard).mockResolvedValue({
      stats: { tenants: 2, tenantsNoDelegation: 0, deployments: 2, deploymentsFailed: 0, deploymentsUpdateAvailable: 0, runsLast24h: 0, runsFailedLast7d: 0 },
      needsAttention: [],
      recentRuns: []
    });

    renderDashboard();

    expect(await screen.findByText("Nothing - all quiet.")).toBeInTheDocument();
    expect(screen.getByText("No runs recorded yet.")).toBeInTheDocument();
  });

  it("shows an error alert with the bare message, no 'Error:' prefix", async () => {
    vi.mocked(api.dashboard).mockRejectedValue(new Error("500 Internal Server Error"));
    renderDashboard();
    // Exact match on purpose: the current Dashboard does setError(String(e)), which stringifies to
    // "Error: 500 Internal Server Error" (with the prefix) -- this must NOT match until the
    // rewrite switches to `e instanceof Error ? e.message : String(e)`.
    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run it, verify it fails**

Run: `cd web && npx vitest run src/components/Dashboard.test.tsx`
Expected: FAIL — this task modifies an existing file rather than creating a new one, so the
concrete failure is the "shows an error alert with the bare message" test: the current
`Dashboard.tsx` does `setError(String(e))`, which renders `"Error: 500 Internal Server Error"` (with
the prefix), so the exact-text query for `"500 Internal Server Error"` finds nothing. The other two
tests may already pass against the old plain-`<table>` markup — that's fine, the file-level run
still reports FAIL as long as one test fails.

- [ ] **Step 3: Rewrite `web/src/components/Dashboard.tsx`**

```tsx
import { useEffect, useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Chip from "@mui/material/Chip";
import Skeleton from "@mui/material/Skeleton";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Typography from "@mui/material/Typography";
import { api } from "../api";
import type { Dashboard as DashboardData } from "../types";

type Tone = "default" | "success" | "warning" | "error";

function Stat({ label, value, tone = "default" }: { label: string; value: number; tone?: Tone }) {
  return (
    <Card variant="outlined" sx={{ minWidth: 140, flex: "1 1 140px" }}>
      <CardContent>
        <Typography variant="h4" color={tone === "default" ? "text.primary" : `${tone}.main`}>
          {value}
        </Typography>
        <Typography variant="body2" color="text.secondary">
          {label}
        </Typography>
      </CardContent>
    </Card>
  );
}

export function Dashboard() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.dashboard().then(setData).catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  if (error) {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Dashboard
        </Typography>
        <Alert severity="error">{error}</Alert>
      </Box>
    );
  }

  if (!data) {
    return (
      <Box>
        <Typography variant="h5" component="h2" gutterBottom>
          Dashboard
        </Typography>
        <Skeleton variant="rounded" height={96} sx={{ mb: 2 }} />
        <Skeleton variant="rounded" height={200} />
      </Box>
    );
  }

  const s = data.stats;
  return (
    <Box>
      <Typography variant="h5" component="h2" gutterBottom>
        Dashboard
      </Typography>

      <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1.5, mb: 3 }}>
        <Stat label="Tenants" value={s.tenants} />
        <Stat label="No delegation" value={s.tenantsNoDelegation} tone={s.tenantsNoDelegation > 0 ? "warning" : "success"} />
        <Stat label="Deployments" value={s.deployments} />
        <Stat label="Failed deployments" value={s.deploymentsFailed} tone={s.deploymentsFailed > 0 ? "error" : "success"} />
        <Stat label="Updates available" value={s.deploymentsUpdateAvailable} tone={s.deploymentsUpdateAvailable > 0 ? "warning" : "success"} />
        <Stat label="Runs (24h)" value={s.runsLast24h} />
        <Stat label="Failed runs (7d)" value={s.runsFailedLast7d} tone={s.runsFailedLast7d > 0 ? "error" : "success"} />
      </Box>

      <Typography variant="h6" component="h3" gutterBottom>
        Needs attention
      </Typography>
      {data.needsAttention.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
          Nothing - all quiet.
        </Typography>
      ) : (
        <TableContainer sx={{ mb: 3, overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>What</TableCell>
                <TableCell>Tenant</TableCell>
                <TableCell>Subject</TableCell>
                <TableCell>Detail</TableCell>
                <TableCell>When</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {data.needsAttention.map((a, i) => (
                <TableRow key={i}>
                  <TableCell>
                    <Chip size="small" label={a.kind} color={a.kind === "No delegation" ? "warning" : "error"} />
                  </TableCell>
                  <TableCell>{a.tenantName}</TableCell>
                  <TableCell>{a.subject}</TableCell>
                  <TableCell sx={{ color: "text.secondary" }}>{a.detail}</TableCell>
                  <TableCell>{a.when ? new Date(a.when).toLocaleString() : ""}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      <Typography variant="h6" component="h3" gutterBottom>
        Recent workflow runs
      </Typography>
      {data.recentRuns.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          No runs recorded yet.
        </Typography>
      ) : (
        <TableContainer sx={{ overflowX: "auto" }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>When</TableCell>
                <TableCell>Workflow</TableCell>
                <TableCell>Tenant</TableCell>
                <TableCell>Kind</TableCell>
                <TableCell>Operator</TableCell>
                <TableCell>Result</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {data.recentRuns.map((r) => (
                <TableRow key={r.id} title={r.error ?? undefined}>
                  <TableCell>{new Date(r.startedAt).toLocaleString()}</TableCell>
                  <TableCell>{r.workflowName}</TableCell>
                  <TableCell>{r.tenantName}</TableCell>
                  <TableCell>{r.kind}</TableCell>
                  <TableCell>{r.operator}</TableCell>
                  <TableCell>
                    <Chip size="small" label={r.succeeded ? "ok" : "failed"} color={r.succeeded ? "success" : "error"} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
```

- [ ] **Step 4: Run it, verify it passes**

Run: `cd web && npx vitest run src/components/Dashboard.test.tsx`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full test suite and build**

Run: `cd web && npm test && npm run build`
Expected: all tests across every task PASS, build PASS.

- [ ] **Step 6: Commit**

```bash
git add web/src/components/Dashboard.tsx web/src/components/Dashboard.test.tsx
git commit -m "Migrate Dashboard to MUI with status-color stat tiles"
```

---

### Task 9: Final verification pass

**Files:** none (verification only).

- [ ] **Step 1: Full automated suite**

Run: `cd web && npm test`
Expected: every test file from Tasks 1-8 passes (theme, useIsPhone, useAsyncAction, useConfirm,
useToast, AppShell, Login, Register, Dashboard).

- [ ] **Step 2: Full build**

Run: `cd web && npm run build`
Expected: PASS, no TypeScript errors, no unused-import/unused-param violations (`noUnusedLocals`/
`noUnusedParameters` are both on in `tsconfig.json`).

- [ ] **Step 3: Manual 375px devtools pass across all three migrated screens plus the shell**

With `npm run dev` running, at a 375px-wide viewport: confirm the app shell hamburger/Drawer works
(already checked in Task 5); confirm Login (both the password form and the MFA step) has no
horizontal scroll and every field is reachable and legible; confirm Register the same; confirm
Dashboard's stat tiles wrap to fewer per row without overflowing and both tables scroll inside
their own `TableContainer` rather than widening the page.

- [ ] **Step 4: Confirm the not-yet-migrated screens are unaffected**

Spot-check two or three of the still-vanilla-CSS tabs (e.g. Tenants, Workflows) at both desktop and
375px width. They should look exactly as they did before this plan (still no responsive handling —
that's workstream 2's job) and must not have picked up any MUI styling bleed from `CssBaseline` or
the theme.

- [ ] **Step 5: Note the open ROADMAP.md item**

`ROADMAP.md`'s "Mobile UX testing" entry (an automated Playwright device-emulation matrix) is
**not** closed by this plan — that capture matrix is workstream 2's deliverable, tested against
real migrated views. Leave `ROADMAP.md` unchanged; do not check it off here.

- [ ] **Step 6: Update version and changelog-equivalent, if the user asks for a release**

Not part of this plan's scope — per `CLAUDE.md`'s release checklist, version bump and docs-site
updates happen at release time, decided separately from implementation.
