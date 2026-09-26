import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { localMe, renderWithRouter, testSession } from "../test/renderWithRouter";
import { makeEvidence, makeParityPlan } from "../test/fixtures";
import { AccessParity, type AccessParityParams } from "./AccessParity";
import type { DirectoryObject, Tenant } from "../types";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api")>();
  return {
    ...actual,
    api: {
      tenants: { list: vi.fn() },
      directory: { users: vi.fn() },
      accessParity: { plan: vi.fn(), apply: vi.fn() },
      workflows: { evidenceMarkdown: vi.fn(), evidenceJson: vi.fn() }
    }
  };
});

import { api, ApiError } from "../api";

const TENANTS: Tenant[] = [{ id: "t1", tenantId: "aaaa", displayName: "Contoso Ltd", status: "Active" }];
const ADA: DirectoryObject = { id: "u1", displayName: "Ada Lovelace", userPrincipalName: "ada@contoso.com" };
const GRACE: DirectoryObject = { id: "u2", displayName: "Grace Hopper", userPrincipalName: "grace@contoso.com" };

function renderParity(params: AccessParityParams = { tenant: "t1", source: "ada@contoso.com", target: "grace@contoso.com" }, me = testSession()) {
  const onParamsChange = vi.fn();
  renderWithRouter(<AccessParity params={params} onParamsChange={onParamsChange} />, { session: me });
  return { onParamsChange };
}

async function comparePlan(user: ReturnType<typeof userEvent.setup>) {
  // The URL names both people; wait until the directory lookup has resolved their names.
  await waitFor(() => expect(screen.getByLabelText("Copy access from")).toHaveValue("Ada Lovelace (ada@contoso.com)"));
  await user.click(screen.getByRole("button", { name: "Compare" }));
  return screen.findByRole("region", { name: "Access parity plan" });
}

describe("AccessParity", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue(TENANTS);
    vi.mocked(api.directory.users).mockImplementation(async (_t, q) => [ADA, GRACE].filter((u) => u.userPrincipalName === q || u.displayName.toLowerCase().includes((q ?? "").toLowerCase())));
    vi.mocked(api.accessParity.plan).mockResolvedValue(makeParityPlan());
    vi.mocked(api.accessParity.apply).mockResolvedValue(makeEvidence());
  });

  it("does not plan until asked, then plans with the chosen people", async () => {
    const user = userEvent.setup();
    renderParity();
    await comparePlan(user);
    expect(api.accessParity.plan).toHaveBeenCalledTimes(1);
    expect(api.accessParity.plan).toHaveBeenCalledWith("t1", "u1", "u2");
  });

  it("separates eligible additions from what is not copied, with reasons, and states the limits", async () => {
    const user = userEvent.setup();
    renderParity();
    const plan = await comparePlan(user);

    const eligible = within(plan).getByRole("list", { name: "Eligible groups" });
    expect(within(eligible).getByRole("checkbox", { name: "Finance Team" })).toBeChecked();
    expect(within(eligible).getByRole("checkbox", { name: "Project Apollo" })).toBeChecked();
    expect(within(eligible).queryByText("All Finance (dynamic)")).not.toBeInTheDocument();
    expect(within(plan).getByText("2 of 2 selected")).toBeInTheDocument();

    expect(within(plan).getByRole("region", { name: "Dynamic groups" })).toHaveTextContent("Dynamic membership rule");
    expect(within(plan).getByRole("region", { name: "Role-assignable groups" })).toHaveTextContent("Helpdesk Admins");
    expect(within(plan).getByRole("region", { name: "Mail-enabled groups and distribution lists" })).toHaveTextContent("finance-dl@contoso.com");
    expect(within(plan).getByRole("region", { name: "Directory roles" })).toHaveTextContent("Billing Administrator");
    expect(within(plan).getByText(/already a member of 1 other group/)).toBeInTheDocument();

    expect(within(plan).getByText("Additive only -- nothing will be removed")).toBeInTheDocument();
    expect(within(plan).getByRole("list", { name: "Limitations" })).toHaveTextContent("SharePoint direct permissions");
  });

  it("confirms the exact groups and applies only the selected item ids, then shows the evidence", async () => {
    const user = userEvent.setup();
    renderParity();
    const plan = await comparePlan(user);
    await user.click(within(plan).getByRole("checkbox", { name: "Project Apollo" }));
    await user.click(within(plan).getByRole("button", { name: "Add Grace Hopper to 1 group" }));

    const dialog = await screen.findByRole("dialog");
    const groups = within(dialog).getByRole("list", { name: "Groups to add" });
    expect(within(groups).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["Finance Team"]);
    expect(dialog).toHaveTextContent("Nothing is removed");
    await user.click(within(dialog).getByRole("button", { name: "Add to 1 group" }));

    await waitFor(() => expect(api.accessParity.apply).toHaveBeenCalledWith("t1", "u1", "u2", ["group:g1"]));
    expect(await screen.findByTestId("evidence-outcome")).toHaveTextContent("Succeeded");
    expect(screen.getByRole("button", { name: "Copy ticket notes" })).toBeInTheDocument();
  });

  it("cancelling the confirmation applies nothing", async () => {
    const user = userEvent.setup();
    renderParity();
    const plan = await comparePlan(user);
    await user.click(within(plan).getByRole("button", { name: "Add Grace Hopper to 2 groups" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(api.accessParity.apply).not.toHaveBeenCalled();
  });

  it("lets a Viewer plan but not apply, and says why", async () => {
    const user = userEvent.setup();
    renderParity(undefined, testSession(localMe({ t1: "Viewer" })));
    const plan = await comparePlan(user);
    expect(within(plan).getByRole("button", { name: "Add Grace Hopper to 2 groups" })).toBeDisabled();
    expect(screen.getByText("You have Viewer access to Contoso Ltd")).toBeInTheDocument();
    expect(within(plan).getByText("Adding groups needs Operator on Contoso Ltd.")).toBeInTheDocument();
  });

  it("surfaces the server's reason when the plan is rejected", async () => {
    vi.mocked(api.accessParity.plan).mockRejectedValue(new ApiError(400, "400 Bad Request: Source and target must be different users.", "Source and target must be different users."));
    const user = userEvent.setup();
    renderParity();
    await waitFor(() => expect(screen.getByLabelText("Copy access from")).toHaveValue("Ada Lovelace (ada@contoso.com)"));
    await user.click(screen.getByRole("button", { name: "Compare" }));
    expect(await screen.findByText("Source and target must be different users.")).toBeInTheDocument();
  });

  it("keeps the chosen tenant and people in the URL", async () => {
    const user = userEvent.setup();
    const { onParamsChange } = renderParity({ tenant: "t1", target: "grace@contoso.com" });
    await user.type(screen.getByLabelText("Copy access from"), "Ada");
    await user.click(await screen.findByRole("option", { name: /Ada Lovelace/ }, { timeout: 3000 }));
    expect(onParamsChange).toHaveBeenLastCalledWith({ tenant: "t1", source: "ada@contoso.com", target: "grace@contoso.com" });
  });
});
