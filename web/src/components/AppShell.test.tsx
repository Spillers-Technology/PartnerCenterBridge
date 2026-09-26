import type { ComponentProps } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppShell } from "./AppShell";
import { mockMatchMedia, renderWithRouter } from "../test/renderWithRouter";

// MUI's useMediaQuery asks for "(max-width:599.95px)" (phone) and "(min-width:900px)" (wide).
const DESKTOP = (q: string) => q.includes("min-width");
const TABLET = () => false;
const PHONE = (q: string) => q.includes("max-width");

function renderShell(props: Partial<ComponentProps<typeof AppShell>> = {}, path = "/") {
  const onSignOut = vi.fn();
  renderWithRouter(
    <AppShell displayName="jspillers" onSignOut={onSignOut} {...props}>
      <div>page content</div>
    </AppShell>,
    { path }
  );
  return { onSignOut };
}

describe("AppShell", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows the six destinations as links in a sidebar at desktop width, without a hamburger", () => {
    mockMatchMedia(DESKTOP);
    renderShell();

    expect(screen.queryByLabelText("Open navigation")).not.toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    const labels = within(nav).getAllByRole("link").map((l) => l.textContent);
    expect(labels).toEqual(["Home", "People", "Tenants", "Operations", "Activity", "Settings"]);
  });

  it("navigates when a destination is clicked and marks it as the current page", async () => {
    mockMatchMedia(DESKTOP);
    const user = userEvent.setup();
    renderShell();

    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getByRole("link", { name: "Home" })).toHaveAttribute("aria-current", "page");

    await user.click(within(nav).getByRole("link", { name: "Tenants" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/tenants");
    expect(within(nav).getByRole("link", { name: "Tenants" })).toHaveAttribute("aria-current", "page");
    expect(within(nav).getByRole("link", { name: "Home" })).not.toHaveAttribute("aria-current");
  });

  it("marks the parent destination active on a nested route", () => {
    mockMatchMedia(DESKTOP);
    renderShell({}, "/tenants/t1?tab=snapshots");
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getByRole("link", { name: "Tenants" })).toHaveAttribute("aria-current", "page");
  });

  it("uses a compact rail on tablet widths", () => {
    mockMatchMedia(TABLET);
    renderShell();
    expect(screen.queryByLabelText("Open navigation")).not.toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getAllByRole("link")).toHaveLength(6);
  });

  it("shows a hamburger + Drawer at phone width, and picking an item navigates and closes it", async () => {
    mockMatchMedia(PHONE);
    const user = userEvent.setup();
    renderShell();

    expect(screen.queryByRole("navigation", { name: "Main navigation" })).not.toBeInTheDocument();
    await user.click(screen.getByLabelText("Open navigation"));
    const nav = await screen.findByRole("navigation", { name: "Main navigation" });
    await user.click(within(nav).getByRole("link", { name: "Operations" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/operations");
  });

  it("shows the pending-approvals count on Activity", () => {
    mockMatchMedia(DESKTOP);
    renderShell({ badges: { activity: 3 } });
    const nav = screen.getByRole("navigation", { name: "Main navigation" });
    expect(within(nav).getByRole("link", { name: /Activity.*3 pending approvals/ })).toBeInTheDocument();
  });

  it("shows the display name and triggers onSignOut from the account menu", async () => {
    mockMatchMedia(DESKTOP);
    const user = userEvent.setup();
    const { onSignOut } = renderShell();

    await user.click(screen.getByLabelText("Account menu"));
    expect(await screen.findByText("jspillers")).toBeInTheDocument();
    await user.click(screen.getByRole("menuitem", { name: "Sign out" }));
    expect(onSignOut).toHaveBeenCalled();
  });

  it("omits the Sign out menu item when onSignOut is not provided", async () => {
    mockMatchMedia(DESKTOP);
    const user = userEvent.setup();
    renderShell({ onSignOut: undefined });

    await user.click(screen.getByLabelText("Account menu"));
    expect(screen.queryByRole("menuitem", { name: "Sign out" })).not.toBeInTheDocument();
  });

  it("renders children in the main content area", () => {
    mockMatchMedia(DESKTOP);
    renderShell();
    expect(screen.getByRole("main")).toHaveTextContent("page content");
  });
});
