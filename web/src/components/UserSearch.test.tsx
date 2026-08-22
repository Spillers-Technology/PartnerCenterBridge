import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { UserSearch } from "./UserSearch";
import type { GlobalSearchResult } from "../types";

vi.mock("../api", () => ({
  api: {
    search: { users: vi.fn() }
  }
}));

import { api } from "../api";

function renderUserSearch() {
  const onLaunch = vi.fn();
  render(
    <ThemeProvider theme={theme}>
      <UserSearch onLaunch={onLaunch} />
    </ThemeProvider>
  );
  return onLaunch;
}

const RESULT: GlobalSearchResult = {
  hits: [
    { tenantId: "t1", tenantName: "Contoso Ltd", id: "u1", displayName: "Ada Lovelace", userPrincipalName: "ada@contoso.com" }
  ],
  errors: [],
  tenantsSearched: 3
};

describe("UserSearch", () => {
  beforeEach(() => vi.clearAllMocks());

  it("searches and shows results", async () => {
    vi.mocked(api.search.users).mockResolvedValue(RESULT);
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("Ada Lovelace")).toBeInTheDocument();
    expect(screen.getByText("ada@contoso.com")).toBeInTheDocument();
    expect(screen.getByText("Contoso Ltd")).toBeInTheDocument();
    expect(screen.getByText("1 match(es) across 3 tenant(s)")).toBeInTheDocument();
    expect(api.search.users).toHaveBeenCalledWith("ada");
  });

  it("blocks the search call and shows a message when the query is under 3 characters", async () => {
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ab");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("Type at least 3 characters.")).toBeInTheDocument();
    expect(api.search.users).not.toHaveBeenCalled();
  });

  it("shows an error alert when the search fails", async () => {
    vi.mocked(api.search.users).mockRejectedValue(new Error("503 Service Unavailable"));
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("503 Service Unavailable")).toBeInTheDocument();
  });

  it("calls onLaunch with the right shape when a per-row action is clicked", async () => {
    vi.mocked(api.search.users).mockResolvedValue(RESULT);
    const user = userEvent.setup();
    const onLaunch = renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));
    await screen.findByText("Ada Lovelace");
    await user.click(screen.getByRole("button", { name: "MFA reset" }));

    expect(onLaunch).toHaveBeenCalledWith({
      workflowId: "mfa-reset",
      tenantId: "t1",
      inputs: { userUpn: "ada@contoso.com" }
    });
  });

  it("hides the previous results and their action buttons as soon as the query is edited", async () => {
    vi.mocked(api.search.users).mockResolvedValue(RESULT);
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));
    expect(await screen.findByText("Ada Lovelace")).toBeInTheDocument();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "m");

    expect(screen.queryByText("Ada Lovelace")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "MFA reset" })).not.toBeInTheDocument();
  });

  it("does not resurrect a stale response that resolves after the query has since been edited", async () => {
    // Regression test: the Search button disables while a search is in flight, but the text field
    // itself stays editable -- an old query's response landing late used to unconditionally
    // repopulate the results (and their workflow-launch buttons) under a query box that no longer
    // matches what they came from.
    let resolveSearch!: (r: typeof RESULT) => void;
    vi.mocked(api.search.users).mockReturnValue(new Promise((resolve) => { resolveSearch = resolve; }));
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "m");
    resolveSearch(RESULT);

    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByText("Ada Lovelace")).not.toBeInTheDocument();
  });

  it("hides stale results once a re-search fails, instead of leaving them next to the new error", async () => {
    vi.mocked(api.search.users).mockResolvedValueOnce(RESULT);
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));
    expect(await screen.findByText("Ada Lovelace")).toBeInTheDocument();

    vi.mocked(api.search.users).mockRejectedValueOnce(new Error("503 Service Unavailable"));
    await user.clear(screen.getByLabelText("Name or UPN (min 3 chars)"));
    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "bob");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("503 Service Unavailable")).toBeInTheDocument();
    expect(screen.queryByText("Ada Lovelace")).not.toBeInTheDocument();
  });

  it("renders the unreachable-tenants table when errors are present", async () => {
    vi.mocked(api.search.users).mockResolvedValue({
      hits: [],
      errors: [{ tenantId: "t2", tenantName: "Fabrikam Inc", message: "timeout" }],
      tenantsSearched: 2
    });
    const user = userEvent.setup();
    renderUserSearch();

    await user.type(screen.getByLabelText("Name or UPN (min 3 chars)"), "ada");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("Unreachable tenants")).toBeInTheDocument();
    expect(screen.getByText("Fabrikam Inc")).toBeInTheDocument();
    expect(screen.getByText("timeout")).toBeInTheDocument();
  });
});
