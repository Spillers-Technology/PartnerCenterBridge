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
