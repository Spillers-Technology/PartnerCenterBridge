import type { ComponentProps } from "react";
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

function renderShell(props: Partial<ComponentProps<typeof AppShell>> = {}) {
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
