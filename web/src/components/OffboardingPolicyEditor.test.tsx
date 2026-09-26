import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithRouter } from "../test/renderWithRouter";
import { OffboardingPolicyEditor } from "./OffboardingPolicyEditor";
import { Contracts } from "./Contracts";
import type { MeProfile, OffboardingPolicy } from "../types";

vi.mock("../api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api")>();
  return {
    ...actual,
    api: {
      contracts: { list: vi.fn(), plan: vi.fn(), getOffboardingPolicy: vi.fn(), putOffboardingPolicy: vi.fn() },
      templates: { list: vi.fn() }
    }
  };
});

import { api, ApiError } from "../api";

const DEFAULTS: OffboardingPolicy = {
  blockSignIn: true, revokeSessions: true, groupCleanup: "RemoveAll", convertMailboxToShared: false,
  removeLicenses: true, hideFromGal: false, forwardTo: null, managerAccess: "None", wipeDevices: "None", followUpDays: 0
};

const catalogManager: MeProfile = {
  id: "m", email: "m@example.com", displayName: "M", isSystemAdmin: false, totpEnabled: false, tenantAccess: [],
  instancePermissions: ["instance.catalog.manage"]
};
const viewer: MeProfile = { ...catalogManager, instancePermissions: [] };

describe("OffboardingPolicyEditor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.contracts.getOffboardingPolicy).mockResolvedValue(DEFAULTS);
    vi.mocked(api.contracts.putOffboardingPolicy).mockImplementation(async (_id, p) => p);
  });

  it("loads the policy and saves the edited fields", async () => {
    const user = userEvent.setup();
    renderWithRouter(<OffboardingPolicyEditor contractId="c1" contractName="Managed" canEdit />);
    const convert = await screen.findByRole("switch", { name: "Convert mailbox to shared" });
    expect(api.contracts.getOffboardingPolicy).toHaveBeenCalledWith("c1");
    expect(screen.getByRole("button", { name: "Save policy" })).toBeDisabled();

    await user.click(convert);
    await user.type(screen.getByLabelText("Forward mail to"), " manager@contoso.com ");
    await user.clear(screen.getByLabelText("Follow-up after (days)"));
    await user.type(screen.getByLabelText("Follow-up after (days)"), "30");
    await user.click(screen.getByRole("combobox", { name: "Managed devices" }));
    await user.click(screen.getByRole("option", { name: "Retire devices" }));
    await user.click(screen.getByRole("button", { name: "Save policy" }));

    await waitFor(() => expect(api.contracts.putOffboardingPolicy).toHaveBeenCalledWith("c1", {
      ...DEFAULTS, convertMailboxToShared: true, forwardTo: "manager@contoso.com", followUpDays: 30, wipeDevices: "Retire"
    }));
    expect(await screen.findByText("Offboarding policy for Managed saved")).toBeInTheDocument();
  });

  it("blocks obviously invalid values before sending them", async () => {
    const user = userEvent.setup();
    renderWithRouter(<OffboardingPolicyEditor contractId="c1" contractName="Managed" canEdit />);
    await user.type(await screen.findByLabelText("Forward mail to"), "not an address");
    expect(screen.getByText("Enter a plain address like manager@contoso.com.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save policy" })).toBeDisabled();
  });

  it("shows the server's validation message when a save is rejected", async () => {
    vi.mocked(api.contracts.putOffboardingPolicy).mockRejectedValue(
      new ApiError(400, "400 Bad Request: forwardTo must be a plain SMTP address (user@domain).", "forwardTo must be a plain SMTP address (user@domain).")
    );
    const user = userEvent.setup();
    renderWithRouter(<OffboardingPolicyEditor contractId="c1" contractName="Managed" canEdit />);
    await user.type(await screen.findByLabelText("Forward mail to"), "m@contoso.com");
    await user.click(screen.getByRole("button", { name: "Save policy" }));
    expect(await screen.findByText("forwardTo must be a plain SMTP address (user@domain).")).toBeInTheDocument();
  });

  it("is read-only without the catalog manager role, and says so", async () => {
    renderWithRouter(<OffboardingPolicyEditor contractId="c1" contractName="Managed" canEdit={false} />);
    expect(await screen.findByText(/needs the catalog manager role/)).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Block sign-in" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Save policy" })).not.toBeInTheDocument();
  });

  it("offers a retry when the policy can't load", async () => {
    vi.mocked(api.contracts.getOffboardingPolicy).mockRejectedValueOnce(new ApiError(500, "500 Internal Server Error: boom", "boom"));
    const user = userEvent.setup();
    renderWithRouter(<OffboardingPolicyEditor contractId="c1" contractName="Managed" canEdit />);
    expect(await screen.findByText("boom")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByRole("switch", { name: "Block sign-in" })).toBeInTheDocument();
  });

  it("opens from each contract in Contracts, editable only for catalog managers", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([{ id: "c1", name: "Managed", tenantCount: 1, desiredAppCount: 0, desiredAppIds: [] }]);
    vi.mocked(api.templates.list).mockResolvedValue([]);
    const user = userEvent.setup();
    const { unmount } = renderWithRouter(<Contracts me={viewer} />);
    await user.click(await screen.findByRole("button", { name: "Offboarding policy" }));
    const section = await screen.findByRole("region", { name: "Offboarding policy for Managed" });
    expect(await within(section).findByText(/needs the catalog manager role/)).toBeInTheDocument();
    unmount();

    renderWithRouter(<Contracts me={catalogManager} />);
    await user.click(await screen.findByRole("button", { name: "Offboarding policy" }));
    expect(await screen.findByRole("button", { name: "Save policy" })).toBeInTheDocument();
  });
});
