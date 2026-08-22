import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ToastProvider } from "../hooks/useToast";
import { Contracts } from "./Contracts";
import type { Contract, AppTemplate, MeProfile } from "../types";

vi.mock("../api", () => ({
  api: {
    contracts: {
      list: vi.fn(),
      create: vi.fn(),
      plan: vi.fn(),
      addDesiredApp: vi.fn(),
      removeDesiredApp: vi.fn()
    },
    templates: {
      list: vi.fn(),
      uploadPackage: vi.fn()
    }
  }
}));

import { api } from "../api";

const admin: MeProfile = {
  id: "u1", email: "admin@contoso.com", displayName: "Admin", isSystemAdmin: true,
  totpEnabled: false, tenantAccess: []
};
const nonAdmin: MeProfile = { ...admin, id: "u2", isSystemAdmin: false };

const contract: Contract = {
  id: "c1",
  name: "Contoso baseline",
  notes: "Standard apps",
  tenantCount: 2,
  desiredAppCount: 1,
  desiredAppIds: ["t1"]
};

const readyTemplate: AppTemplate = {
  id: "t1", displayName: "Defender", installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe", contentVersion: 1, hasPackage: true,
  detectionRules: [], assignments: []
};
const noPackageTemplate: AppTemplate = {
  id: "t2", displayName: "Zoom", installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe", contentVersion: 0, hasPackage: false,
  detectionRules: [], assignments: []
};

function renderContracts(me: MeProfile | null = admin) {
  render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <Contracts me={me} />
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("Contracts", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(api.contracts.list).mockResolvedValue([]);
    vi.mocked(api.contracts.create).mockResolvedValue(contract);
    vi.mocked(api.contracts.plan).mockResolvedValue([]);
    vi.mocked(api.templates.list).mockResolvedValue([]);
  });

  it("renders the contracts list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts();

    expect(await screen.findByText("Contoso baseline")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("creates a contract and shows a success toast", async () => {
    const user = userEvent.setup();
    renderContracts();

    await user.type(await screen.findByLabelText("Contract name"), "Fabrikam baseline");
    await user.type(screen.getByLabelText("Notes (optional)"), "Pilot apps");
    await user.click(screen.getByRole("button", { name: "Add contract" }));

    await waitFor(() => expect(api.contracts.create).toHaveBeenCalledWith("Fabrikam baseline", "Pilot apps"));
    expect(await screen.findByText("Fabrikam baseline added")).toBeInTheDocument();
  });

  it("shows an error alert when loading contracts fails", async () => {
    vi.mocked(api.contracts.list).mockRejectedValue(new Error("boom"));
    renderContracts();

    expect(await screen.findByText("boom")).toBeInTheDocument();
  });

  it("renders a preview plan", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockResolvedValue([
      { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
    ]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Reconcile plan (dry run)")).toBeInTheDocument();
    expect(screen.getByText("Contoso")).toBeInTheDocument();
    expect(screen.getByText("Defender")).toBeInTheDocument();
    expect(screen.getByText("Install")).toBeInTheDocument();
  });

  it("shows the empty preview plan state", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("Nothing to do.")).toBeInTheDocument();
  });

  it("shows an error when the plan preview fails", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockRejectedValue(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));

    expect(await screen.findByText("plan boom")).toBeInTheDocument();
  });

  it("clears a previously shown plan once a new preview attempt fails", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan)
      .mockResolvedValueOnce([
        { tenantId: "t1", tenantName: "Contoso", templateId: "a1", templateName: "Defender", action: "Install" }
      ])
      .mockRejectedValueOnce(new Error("plan boom"));
    const user = userEvent.setup();
    renderContracts();

    const previewButton = await screen.findByRole("button", { name: "Preview plan" });
    await user.click(previewButton);
    expect(await screen.findByText("Defender")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview plan" }));
    expect(await screen.findByText("plan boom")).toBeInTheDocument();
    expect(screen.queryByText("Defender")).not.toBeInTheDocument();
  });

  it("does not render Manage apps for a non-admin", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts(nonAdmin);

    await screen.findByText("Contoso baseline");
    expect(screen.queryByRole("button", { name: "Manage apps" })).not.toBeInTheDocument();
  });

  it("renders Manage apps when me is null", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    renderContracts(null);

    expect(await screen.findByRole("button", { name: "Manage apps" })).toBeInTheDocument();
  });

  it("shows only package-ready templates by default, and reveals the rest via the switch", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));

    expect(await screen.findByText("Defender")).toBeInTheDocument();
    expect(screen.queryByText("Zoom")).not.toBeInTheDocument();

    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));

    expect(await screen.findByText("Zoom")).toBeInTheDocument();
    expect(screen.getByText("So close! Attach a package to unlock →")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Zoom" })).toBeDisabled();
  });

  it("shows a template-loading failure instead of an empty-catalog message", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockRejectedValue(new Error("template load failed"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));

    expect(await screen.findByText("template load failed")).toBeInTheDocument();
    expect(screen.queryByText("No templates yet.")).not.toBeInTheDocument();
  });

  it("shows an already-desired package-less template as checked", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([{
      ...contract, desiredAppIds: ["t1", "t2"], desiredAppCount: 2
    }]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));

    expect(await screen.findByRole("checkbox", { name: "Zoom" })).toBeChecked();
  });

  it("checking a box calls addDesiredApp and reflects the response", async () => {
    // Defender must start un-desired here so clicking its checkbox is genuinely a "check"
    // (the shared `contract` fixture has t1 already desired, which is what the sibling
    // "unchecking a box" test below relies on).
    vi.mocked(api.contracts.list).mockResolvedValue([{ ...contract, desiredAppIds: [], desiredAppCount: 0 }]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    const updated: Contract = { ...contract, desiredAppIds: ["t1", "t2"], desiredAppCount: 2 };
    vi.mocked(api.contracts.addDesiredApp).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));
    await user.click(screen.getByRole("checkbox", { name: "Defender" }));

    await waitFor(() => expect(api.contracts.addDesiredApp).toHaveBeenCalledWith("c1", "t1"));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Defender" })).toBeChecked());
  });

  it("unchecking a box calls removeDesiredApp and reflects the response", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    const updated: Contract = { ...contract, desiredAppIds: [], desiredAppCount: 0 };
    vi.mocked(api.contracts.removeDesiredApp).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    expect(checkbox).toBeChecked();
    await user.click(checkbox);

    await waitFor(() => expect(api.contracts.removeDesiredApp).toHaveBeenCalledWith("c1", "t1"));
    await waitFor(() => expect(checkbox).not.toBeChecked());
  });

  it("two different templates toggled close together resolve independently", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    const secondReady: AppTemplate = { ...readyTemplate, id: "t3", displayName: "Zoom Ready" };
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, secondReady]);

    let resolveFirst!: (c: Contract) => void;
    const firstCall = new Promise<Contract>((resolve) => { resolveFirst = resolve; });
    vi.mocked(api.contracts.removeDesiredApp).mockImplementation((_cid, tid) =>
      tid === "t1" ? firstCall : Promise.resolve({ ...contract, desiredAppIds: [], desiredAppCount: 0 })
    );
    vi.mocked(api.contracts.addDesiredApp).mockResolvedValue({ ...contract, desiredAppIds: ["t1", "t3"], desiredAppCount: 2 });

    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const defenderBox = await screen.findByRole("checkbox", { name: "Defender" });
    const zoomReadyBox = screen.getByRole("checkbox", { name: "Zoom Ready" });

    await user.click(defenderBox);
    expect(defenderBox).toBeDisabled();
    await user.click(zoomReadyBox);

    await waitFor(() => expect(api.contracts.addDesiredApp).toHaveBeenCalledWith("c1", "t3"));
    expect(zoomReadyBox).not.toBeDisabled();
    expect(defenderBox).toBeDisabled();

    resolveFirst({ ...contract, desiredAppIds: [], desiredAppCount: 0 });
    await waitFor(() => expect(defenderBox).not.toBeDisabled());
    expect(defenderBox).not.toBeChecked();
    expect(zoomReadyBox).toBeChecked();
  });

  it("ignores an older full-list response after a toggle completes", async () => {
    const addedContract: Contract = {
      ...contract, id: "c2", name: "Fabrikam baseline", desiredAppIds: [], desiredAppCount: 0
    };
    let resolveReload!: (contracts: Contract[]) => void;
    const staleReload = new Promise<Contract[]>((resolve) => { resolveReload = resolve; });
    vi.mocked(api.contracts.list)
      .mockResolvedValueOnce([contract])
      .mockReturnValueOnce(staleReload);
    vi.mocked(api.contracts.create).mockResolvedValue(addedContract);
    vi.mocked(api.contracts.removeDesiredApp).mockResolvedValue({
      ...contract, desiredAppIds: [], desiredAppCount: 0
    });
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.type(await screen.findByLabelText("Contract name"), "Fabrikam baseline");
    await user.click(screen.getByRole("button", { name: "Add contract" }));
    await waitFor(() => expect(api.contracts.list).toHaveBeenCalledTimes(2));
    await user.click(screen.getAllByRole("button", { name: "Manage apps" })[0]);
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    await user.click(checkbox);
    await waitFor(() => expect(checkbox).not.toBeChecked());

    resolveReload([contract, addedContract]);

    await waitFor(() => expect(screen.getByText("Fabrikam baseline added")).toBeInTheDocument());
    expect(screen.getByText("Fabrikam baseline")).toBeInTheDocument();
    expect(checkbox).not.toBeChecked();
  });

  it("does not show a plan response that became stale during a toggle", async () => {
    let resolvePlan!: (items: Awaited<ReturnType<typeof api.contracts.plan>>) => void;
    const pendingPlan = new Promise<Awaited<ReturnType<typeof api.contracts.plan>>>((resolve) => {
      resolvePlan = resolve;
    });
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.contracts.plan).mockReturnValue(pendingPlan);
    let resolveToggle!: (contract: Contract) => void;
    const pendingToggle = new Promise<Contract>((resolve) => { resolveToggle = resolve; });
    vi.mocked(api.contracts.removeDesiredApp).mockReturnValue(pendingToggle);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Preview plan" }));
    await user.click(screen.getByRole("button", { name: "Manage apps" }));
    await user.click(await screen.findByRole("checkbox", { name: "Defender" }));
    await waitFor(() => expect(api.contracts.removeDesiredApp).toHaveBeenCalled());

    await act(async () => {
      resolvePlan([{
        tenantId: "tenant-1", tenantName: "Contoso", templateId: "t1",
        templateName: "Defender", action: "Install"
      }]);
      await pendingPlan;
    });

    expect(screen.queryByText("Reconcile plan (dry run)")).not.toBeInTheDocument();
    await act(async () => {
      resolveToggle({ ...contract, desiredAppIds: [], desiredAppCount: 0 });
      await pendingToggle;
    });
  });

  it("lets an admin remove an already-desired package-less template", async () => {
    const legacyContract = { ...contract, desiredAppIds: ["t2"], desiredAppCount: 1 };
    vi.mocked(api.contracts.list).mockResolvedValue([legacyContract]);
    vi.mocked(api.templates.list).mockResolvedValue([noPackageTemplate]);
    vi.mocked(api.contracts.removeDesiredApp).mockResolvedValue({
      ...legacyContract, desiredAppIds: [], desiredAppCount: 0
    });
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Zoom" });
    expect(checkbox).toBeChecked();
    expect(checkbox).not.toBeDisabled();

    await user.click(checkbox);

    await waitFor(() => expect(api.contracts.removeDesiredApp).toHaveBeenCalledWith("c1", "t2"));
    await waitFor(() => expect(checkbox).not.toBeChecked());
  });

  it("a failed toggle shows the bare error message and leaves the checkbox in its prior state", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    vi.mocked(api.contracts.removeDesiredApp).mockRejectedValue(new Error("network down"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    await user.click(checkbox);

    expect(await screen.findByText("network down")).toBeInTheDocument();
    expect(checkbox).toBeChecked();
  });

  it("reconciles an ambiguous toggle failure from the server", async () => {
    vi.mocked(api.contracts.list)
      .mockResolvedValueOnce([contract])
      .mockResolvedValueOnce([{ ...contract, desiredAppIds: [], desiredAppCount: 0 }]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate]);
    vi.mocked(api.contracts.removeDesiredApp).mockRejectedValue(new Error("connection reset"));
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    const checkbox = await screen.findByRole("checkbox", { name: "Defender" });
    await user.click(checkbox);

    expect(await screen.findByText("connection reset")).toBeInTheDocument();
    await waitFor(() => expect(checkbox).not.toBeChecked());
  });

  it("makes the quest chip keyboard-activatable", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([noPackageTemplate]);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));
    const input = screen.getByLabelText("Upload package for Zoom");
    const clickSpy = vi.spyOn(input, "click");
    const chip = screen.getByRole("button", { name: "So close! Attach a package to unlock \u2192" });
    chip.focus();

    await user.keyboard("{Enter}");

    expect(clickSpy).toHaveBeenCalledOnce();
  });

  it("disables a quest-chip upload while that template is pending", async () => {
    let resolveUpload!: (template: AppTemplate) => void;
    const pendingUpload = new Promise<AppTemplate>((resolve) => { resolveUpload = resolve; });
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([noPackageTemplate]);
    vi.mocked(api.templates.uploadPackage).mockReturnValue(pendingUpload);
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));
    const input = screen.getByLabelText("Upload package for Zoom");
    await user.upload(input, new File(["x"], "zoom.intunewin"));

    expect(await screen.findByRole("button", { name: "Uploading package..." })).toBeDisabled();
    expect(input).toBeDisabled();
    expect(api.templates.uploadPackage).toHaveBeenCalledTimes(1);

    resolveUpload({ ...noPackageTemplate, hasPackage: true });
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Zoom" })).not.toBeDisabled());
  });

  it("clicking the quest chip uploads a package and moves the template into the normal list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list).mockResolvedValue([readyTemplate, noPackageTemplate]);
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...noPackageTemplate, hasPackage: true });
    const user = userEvent.setup();
    renderContracts();

    await user.click(await screen.findByRole("button", { name: "Manage apps" }));
    await user.click(screen.getByRole("switch", { name: "Show templates without a package" }));
    expect(await screen.findByText("Zoom")).toBeInTheDocument();

    const file = new File(["x"], "zoom.intunewin");
    const input = screen.getByLabelText("Upload package for Zoom");
    await user.upload(input, file);

    await waitFor(() => expect(api.templates.uploadPackage).toHaveBeenCalledWith("t2", file));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Zoom" })).not.toBeDisabled());
  });
});
