import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { Offboard } from "./Offboard";
import type { DirectoryObject, OperationPlan, ProvisioningResult, Tenant } from "../types";
import { renderWithRouter } from "../test/renderWithRouter";
import { makeEvidence } from "../test/fixtures";

vi.mock("../api", () => ({
  api: {
    tenants: { list: vi.fn() },
    directory: { users: vi.fn() },
    provisioning: { terminate: vi.fn(), terminatePlan: vi.fn() },
    contracts: { getOffboardingPolicy: vi.fn() },
    workflows: { evidenceMarkdown: vi.fn(), evidenceJson: vi.fn() }
  }
}));

import { api } from "../api";

const tenant: Tenant = { id: "t1", tenantId: "guid-1", displayName: "Contoso", defaultDomain: "contoso.com", status: "Active" };
const directoryUser: DirectoryObject = { id: "u1", displayName: "Ada Lovelace", userPrincipalName: "ada@contoso.com" };
const result: ProvisioningResult = {
  userId: "u1",
  userPrincipalName: "ada@contoso.com",
  steps: [{ name: "Block sign-in", success: true, detail: "Done" }],
  succeeded: true
};

function renderOffboard() {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <Offboard />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

async function selectTenantAndUser(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Tenant"));
  await user.click(screen.getByRole("option", { name: "Contoso" }));
  await user.type(screen.getByLabelText("Search name or UPN"), "Ada");
  await user.click(screen.getByRole("button", { name: "Search users" }));
  await user.click(await screen.findByLabelText("User"));
  await user.click(screen.getByRole("option", { name: "Ada Lovelace (ada@contoso.com)" }));
}

describe("Offboard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.directory.users).mockResolvedValue([directoryUser]);
    vi.mocked(api.provisioning.terminate).mockResolvedValue(result);
  });

  it("searches users after selecting a tenant", async () => {
    const user = userEvent.setup();
    renderOffboard();

    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));
    await user.type(screen.getByLabelText("Search name or UPN"), "Ada");
    await user.click(screen.getByRole("button", { name: "Search users" }));

    expect(await screen.findByLabelText("User")).toBeInTheDocument();
    expect(api.directory.users).toHaveBeenCalledWith("t1", "Ada");
  });

  it("gates offboarding behind confirm: cancel does not call the API, confirm does", async () => {
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const cancelDialog = await screen.findByRole("dialog");
    expect(within(cancelDialog).getByText(/Ada Lovelace \(ada@contoso.com\) in Contoso will be offboarded/)).toBeInTheDocument();
    await user.click(within(cancelDialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(api.provisioning.terminate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const confirmDialog = await screen.findByRole("dialog");
    await user.click(within(confirmDialog).getByRole("button", { name: "Offboard" }));

    await waitFor(() => expect(api.provisioning.terminate).toHaveBeenCalledWith("t1", {
      userId: "u1",
      blockSignIn: true,
      revokeSessions: true,
      removeLicenses: true,
      removeFromGroups: true,
      convertMailboxToShared: false,
      forwardingSmtpAddress: undefined
    }));
    expect(await screen.findByText("Ada Lovelace offboarded")).toBeInTheDocument();
    expect(await screen.findByText("Done")).toBeInTheDocument();
  });

  it("shows a search error", async () => {
    vi.mocked(api.directory.users).mockRejectedValue(new Error("search failed"));
    const user = userEvent.setup();
    renderOffboard();

    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));
    await user.click(screen.getByRole("button", { name: "Search users" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("search failed");
  });

  it("includes the forwarding address when converting the mailbox to shared", async () => {
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByLabelText("Convert mailbox to shared (Exchange Online)"));
    await user.type(screen.getByLabelText("Forward mailbox to (optional SMTP)"), "manager@contoso.com");

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Offboard" }));

    await waitFor(() => expect(api.provisioning.terminate).toHaveBeenCalledWith("t1", {
      userId: "u1",
      blockSignIn: true,
      revokeSessions: true,
      removeLicenses: true,
      removeFromGroups: true,
      convertMailboxToShared: true,
      forwardingSmtpAddress: "manager@contoso.com"
    }));
  });

  it("shows an error alert when the terminate call fails", async () => {
    vi.mocked(api.provisioning.terminate).mockRejectedValue(new Error("terminate failed"));
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Offboard" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("terminate failed");
  });

  it("does not leave a previously selected user submittable after a new search replaces the results", async () => {
    // Regression guard: selecting a user, then re-searching, used to leave the old userId set
    // (and the Offboard button enabled) even though that person no longer appears anywhere in the
    // UI -- a wrong-person-offboarded risk. A new search must clear the prior selection.
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    expect(screen.getByRole("button", { name: "Offboard user" })).toBeEnabled();

    vi.mocked(api.directory.users).mockResolvedValue([]);
    await user.clear(screen.getByLabelText("Search name or UPN"));
    await user.type(screen.getByLabelText("Search name or UPN"), "Bob");
    await user.click(screen.getByRole("button", { name: "Search users" }));

    await waitFor(() => expect(api.directory.users).toHaveBeenCalledWith("t1", "Bob"));
    expect(screen.queryByLabelText("User")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Offboard user" })).toBeDisabled();
  });

  it("clears the forwarding address when a new search or user selection replaces the prior target", async () => {
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    await user.click(screen.getByLabelText("Convert mailbox to shared (Exchange Online)"));
    await user.type(screen.getByLabelText("Forward mailbox to (optional SMTP)"), "old-manager@contoso.com");

    vi.mocked(api.directory.users).mockResolvedValue([directoryUser]);
    await user.clear(screen.getByLabelText("Search name or UPN"));
    await user.type(screen.getByLabelText("Search name or UPN"), "Ada");
    await user.click(screen.getByRole("button", { name: "Search users" }));

    await waitFor(() => expect(screen.getByLabelText("User")).toBeInTheDocument());
    // The mailbox-forwarding field only renders while convertMailboxToShared is checked, which the
    // new search does not reset -- so it's still visible, but its value must not have survived.
    expect(screen.getByLabelText("Forward mailbox to (optional SMTP)")).toHaveValue("");
  });

  it("disables the offboard button while a confirm is pending, before the API call itself starts", async () => {
    // Regression guard: useAsyncAction's busy flag only turns on once the terminate call starts,
    // which used to leave a window open (while confirm() is awaited) where a second click could
    // queue a second confirm request. The button must disable as soon as the first click starts
    // the confirm flow, not just once the destructive call itself begins.
    const user = userEvent.setup();
    renderOffboard();
    await selectTenantAndUser(user);

    const offboardButton = screen.getByRole("button", { name: "Offboard user" });
    await user.click(offboardButton);
    await screen.findByRole("dialog");

    expect(offboardButton).toBeDisabled();
  });
});


const offboardPlan: OperationPlan = {
  operationId: "offboarding",
  operationName: "Offboarding",
  tenantId: "t1",
  target: { kind: "user", id: "u1", displayName: "Ada Lovelace" },
  preflight: [{ name: "Directory sync", status: "Warning", detail: "Synced from on-premises AD." }],
  items: [
    { id: "block-sign-in", action: "BlockSignIn", objectType: "user", objectId: "u1", objectName: "Ada Lovelace", destructive: false, eligible: false, category: "SignIn", reason: "Account is synced from on-premises AD; disable it in on-premises AD." },
    { id: "revoke-sessions", action: "RevokeSessions", objectType: "user", objectId: "u1", objectName: "Ada Lovelace", destructive: false, eligible: true, category: "SignIn" },
    { id: "hide-from-gal", action: "HideFromGal", objectType: "mailbox", objectId: "ada@contoso.com", objectName: "Ada Lovelace", destructive: false, eligible: false, category: "Mailbox", reason: "Not executable yet: PCB has no Exchange operation for this." },
    { id: "group:g1", action: "RemoveMember", objectType: "group", objectId: "g1", objectName: "Finance Team", destructive: true, eligible: true, category: "Group" },
    { id: "license:sku-e3", action: "RemoveLicense", objectType: "license", objectId: "sku-e3", objectName: "SPE_E3", destructive: true, eligible: true, category: "License" }
  ],
  warnings: ["Hybrid account: disable the account in on-premises AD as well."],
  limitations: []
};

describe("Offboard plan and evidence (0.9.0)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.tenants.list).mockResolvedValue([tenant]);
    vi.mocked(api.directory.users).mockResolvedValue([directoryUser]);
    vi.mocked(api.provisioning.terminatePlan).mockResolvedValue(offboardPlan);
    vi.mocked(api.provisioning.terminate).mockResolvedValue({
      ...result,
      evidence: makeEvidence({ operationName: "Offboarding", target: { kind: "user", id: "u1", displayName: "Ada Lovelace" }, outcome: "PartiallySucceeded" })
    });
  });

  it("previews the ordered plan with destructive and ineligible steps, then confirms the exact steps and shows the evidence", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Offboard />);
    await selectTenantAndUser(user);

    await user.click(screen.getByRole("button", { name: "Preview plan" }));
    const plan = await screen.findByRole("region", { name: "Offboarding plan" });
    expect(api.provisioning.terminatePlan).toHaveBeenCalledWith("t1", expect.objectContaining({ userId: "u1", removeLicenses: true }));
    const steps = within(plan).getByRole("list", { name: "Plan steps" });
    const items = within(steps).getAllByRole("listitem");
    expect(items[0]).toHaveTextContent("Block sign-in: Ada Lovelace");
    expect(items[0]).toHaveTextContent("Won't run");
    expect(items[0]).toHaveTextContent("disable it in on-premises AD");
    expect(items[2]).toHaveTextContent("Not executable yet");
    expect(items[3]).toHaveTextContent("Remove from: Finance Team");
    expect(items[3]).toHaveTextContent("Destructive");
    expect(within(plan).getByRole("list", { name: "Plan warnings" })).toHaveTextContent("Hybrid account");
    expect(within(plan).getByText(/3 of 5 steps will run/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Offboard user" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Ada Lovelace (ada@contoso.com) in Contoso will be offboarded");
    expect(dialog).toHaveTextContent("2 of them are destructive");
    const listed = within(within(dialog).getByRole("list", { name: "Steps to run" })).getAllByRole("listitem").map((li) => li.textContent);
    expect(listed).toEqual(["Revoke sessions: Ada Lovelace", "Remove from: Finance Team (destructive)", "Remove license: SPE_E3 (destructive)"]);
    await user.click(within(dialog).getByRole("button", { name: "Offboard" }));

    expect(await screen.findByTestId("evidence-outcome")).toHaveTextContent("Offboarding: Partially succeeded");
    expect(await screen.findByText(/Offboarding Ada Lovelace finished: partially succeeded/)).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Offboarding plan" })).not.toBeInTheDocument();
  });

  it("drops a plan that no longer matches the options", async () => {
    const user = userEvent.setup();
    renderWithRouter(<Offboard />);
    await selectTenantAndUser(user);
    await user.click(screen.getByRole("button", { name: "Preview plan" }));
    await screen.findByRole("region", { name: "Offboarding plan" });
    await user.click(screen.getByLabelText("Remove licenses"));
    expect(screen.queryByRole("region", { name: "Offboarding plan" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview plan" })).toBeEnabled();
  });

  it("starts from the tenant's contract offboarding policy", async () => {
    vi.mocked(api.tenants.list).mockResolvedValue([{ ...tenant, contractId: "c1" }]);
    vi.mocked(api.contracts.getOffboardingPolicy).mockResolvedValue({
      blockSignIn: true, revokeSessions: true, groupCleanup: "RemoveAssignable", convertMailboxToShared: true,
      removeLicenses: false, hideFromGal: false, forwardTo: "manager@contoso.com", managerAccess: "None", wipeDevices: "Retire", followUpDays: 30
    });
    const user = userEvent.setup();
    renderWithRouter(<Offboard />);
    await user.click(await screen.findByLabelText("Tenant"));
    await user.click(screen.getByRole("option", { name: "Contoso" }));

    await waitFor(() => expect(api.contracts.getOffboardingPolicy).toHaveBeenCalledWith("c1"));
    await waitFor(() => expect(screen.getByLabelText("Remove licenses")).not.toBeChecked());
    expect(screen.getByLabelText("Convert mailbox to shared (Exchange Online)")).toBeChecked();
    expect(screen.getByText(/retire managed devices; follow-up reminder after 30 days/)).toBeInTheDocument();
    expect(screen.getByLabelText("Forward mailbox to (optional SMTP)")).toHaveAttribute("placeholder", "Policy default: manager@contoso.com");
  });
});
