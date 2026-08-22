import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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
    vi.clearAllMocks();
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

  it("clicking the quest chip uploads a package and moves the template into the normal list", async () => {
    vi.mocked(api.contracts.list).mockResolvedValue([contract]);
    vi.mocked(api.templates.list)
      .mockResolvedValueOnce([readyTemplate, noPackageTemplate])
      .mockResolvedValueOnce([readyTemplate, { ...noPackageTemplate, hasPackage: true }]);
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
