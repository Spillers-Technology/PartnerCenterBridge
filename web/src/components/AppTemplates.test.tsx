import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "@mui/material/styles";
import { theme } from "../theme";
import { ConfirmDialogProvider } from "../hooks/useConfirm";
import { ToastProvider } from "../hooks/useToast";
import { AppTemplates } from "./AppTemplates";
import type { AppTemplate, MeProfile } from "../types";

vi.mock("../api", () => ({
  api: {
    templates: {
      list: vi.fn(),
      create: vi.fn(),
      update: vi.fn(),
      remove: vi.fn(),
      uploadPackage: vi.fn()
    }
  }
}));

import { api } from "../api";

const admin: MeProfile = {
  id: "u1", email: "a@b.com", displayName: "Admin", isSystemAdmin: true, totpEnabled: false, tenantAccess: []
};

const template: AppTemplate = {
  id: "t1",
  displayName: "Company Portal",
  description: "Line of business app",
  publisher: "Contoso",
  installCommandLine: "install.exe",
  uninstallCommandLine: "uninstall.exe",
  contentVersion: 3,
  hasPackage: true,
  detectionRules: [],
  assignments: []
};

function renderComponent(me: MeProfile | null = admin) {
  return render(
    <ThemeProvider theme={theme}>
      <ToastProvider>
        <ConfirmDialogProvider>
          <AppTemplates me={me} />
        </ConfirmDialogProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

describe("AppTemplates", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows the empty state when there are no templates", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([]);
    renderComponent();
    expect(await screen.findByText("No templates yet.")).toBeInTheDocument();
  });

  it("shows an error alert when the initial load fails, instead of loading forever", async () => {
    vi.mocked(api.templates.list).mockRejectedValue(new Error("500 Internal Server Error"));
    renderComponent();
    expect(await screen.findByText("500 Internal Server Error")).toBeInTheDocument();
  });

  it("renders the template list once loaded", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    renderComponent();

    expect(await screen.findByText("Company Portal")).toBeInTheDocument();
    expect(screen.getByText("Contoso")).toBeInTheDocument();
    expect(screen.getByText("v3")).toBeInTheDocument();
    expect(screen.getByText("Uploaded")).toBeInTheDocument();
  });

  it("creates a template and shows a success toast", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([]);
    vi.mocked(api.templates.create).mockResolvedValue(template);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    vi.mocked(api.templates.list).mockResolvedValue([template]);

    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    await user.click(screen.getByRole("button", { name: "Create template" }));

    expect(await screen.findByText("Template created.")).toBeInTheDocument();
    expect(api.templates.create).toHaveBeenCalledWith(
      expect.objectContaining({ displayName: "Company Portal", installCommandLine: "install.exe", uninstallCommandLine: "uninstall.exe" })
    );
  });

  it("surfaces an error alert when create fails, instead of failing silently", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([]);
    vi.mocked(api.templates.create).mockRejectedValue(new Error("409 conflict"));
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    await user.click(screen.getByRole("button", { name: "Create template" }));

    expect(await screen.findByText("409 conflict")).toBeInTheDocument();
  });

  it("edits a template via the dialog", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    const updated = { ...template, displayName: "Company Portal v2" };
    vi.mocked(api.templates.update).mockResolvedValue(updated);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("Company Portal");
    await user.click(screen.getByRole("button", { name: "Edit Company Portal" }));

    const dialog = await screen.findByRole("dialog");
    const nameField = within(dialog).getByLabelText("Display name");
    await user.clear(nameField);
    await user.type(nameField, "Company Portal v2");
    vi.mocked(api.templates.list).mockResolvedValue([updated]);
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Template updated.")).toBeInTheDocument();
    expect(api.templates.update).toHaveBeenCalledWith("t1", expect.objectContaining({ displayName: "Company Portal v2" }));
  });

  it("gates delete behind confirm -- cancel does not call remove, confirm does", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.templates.remove).mockResolvedValue(undefined);
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("Company Portal");
    await user.click(screen.getByRole("button", { name: "Delete Company Portal" }));

    await screen.findByText("Delete template?");
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(api.templates.remove).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("Delete template?")).not.toBeInTheDocument());

    await user.click(await screen.findByRole("button", { name: "Delete Company Portal" }));
    await screen.findByText("Delete template?");
    vi.mocked(api.templates.list).mockResolvedValue([]);
    await user.click(screen.getByRole("button", { name: "Delete" }));

    expect(api.templates.remove).toHaveBeenCalledWith("t1");
    expect(await screen.findByText("Template deleted.")).toBeInTheDocument();
  });

  it("uploads a package and shows a success toast", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...template, hasPackage: true });
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("Company Portal");
    const fileInput = screen.getByLabelText("Upload package for Company Portal") as HTMLInputElement;
    const file = new File(["binary"], "package.intunewin");
    await user.upload(fileInput, file);

    expect(api.templates.uploadPackage).toHaveBeenCalledWith("t1", file);
    expect(await screen.findByText("Package uploaded.")).toBeInTheDocument();
  });

  it("creates a template with an attached package and uploads it in the same step", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([]);
    vi.mocked(api.templates.create).mockResolvedValue(template);
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...template, hasPackage: true });
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    vi.mocked(api.templates.list).mockResolvedValue([{ ...template, hasPackage: true }]);

    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    const file = new File(["binary"], "package.intunewin");
    await user.upload(screen.getByLabelText("Attach package (.intunewin, optional)"), file);
    expect(await screen.findByText("package.intunewin")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Create template" }));

    expect(api.templates.create).toHaveBeenCalledWith(
      expect.objectContaining({ displayName: "Company Portal" })
    );
    expect(api.templates.uploadPackage).toHaveBeenCalledWith("t1", file);
    expect(await screen.findByText("Template created and package uploaded.")).toBeInTheDocument();
  });

  it("refreshes the list a second time after a successful upload, so the new row stops showing 'No package'", async () => {
    // Regression test: the list was only refreshed once, right after create() and before
    // uploadPackage() -- a successful upload never triggered a second refresh, so the new
    // template's row kept showing "No package" until the user manually reloaded the page.
    vi.mocked(api.templates.list).mockResolvedValueOnce([]);
    vi.mocked(api.templates.create).mockResolvedValue(template);
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...template, hasPackage: true });
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    // First refresh (right after create, before upload) still shows no package -- the real
    // server state at that point in time.
    vi.mocked(api.templates.list).mockResolvedValueOnce([{ ...template, hasPackage: false }]);
    // Second refresh (after the upload succeeds) reflects the now-attached package.
    vi.mocked(api.templates.list).mockResolvedValueOnce([{ ...template, hasPackage: true }]);

    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    const file = new File(["binary"], "package.intunewin");
    await user.upload(screen.getByLabelText("Attach package (.intunewin, optional)"), file);
    await user.click(screen.getByRole("button", { name: "Create template" }));

    expect(await screen.findByText("Template created and package uploaded.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText("Uploaded")).toBeInTheDocument());
    expect(screen.queryByText("No package")).not.toBeInTheDocument();
  });

  it("uploads the file that was attached when Create was clicked, not one picked afterward while the create call is still in flight", async () => {
    // Regression test: newPackage wasn't captured/cleared until after create() resolved, so a
    // file picked in the still-enabled attach-package input during that window could silently
    // replace what actually gets uploaded for this create.
    vi.mocked(api.templates.list).mockResolvedValue([]);
    let resolveCreate!: (t: AppTemplate) => void;
    vi.mocked(api.templates.create).mockReturnValue(new Promise((resolve) => { resolveCreate = resolve; }));
    vi.mocked(api.templates.uploadPackage).mockResolvedValue({ ...template, hasPackage: true });
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    const firstFile = new File(["first"], "first.intunewin");
    await user.upload(screen.getByLabelText("Attach package (.intunewin, optional)"), firstFile);
    await user.click(screen.getByRole("button", { name: "Create template" }));

    // The attach-package input resets to its placeholder label as soon as Create is clicked
    // (newPackage is captured and cleared synchronously, before create() is even awaited) -- it's
    // still enabled while create() is pending, and picking a different file here must not change
    // what this in-flight create ends up uploading.
    await waitFor(() => expect(screen.getByText("Attach package (.intunewin, optional)")).toBeInTheDocument());
    const secondFile = new File(["second"], "second.intunewin");
    await user.upload(screen.getByLabelText("Attach package (.intunewin, optional)"), secondFile);

    resolveCreate(template);
    await waitFor(() => expect(api.templates.uploadPackage).toHaveBeenCalled());

    expect(api.templates.uploadPackage).toHaveBeenCalledWith("t1", firstFile);
  });

  it("resets the create form after a successful create even if the follow-up package upload fails, instead of inviting a duplicate", async () => {
    // Regression test: create() succeeding but uploadPackage() then failing used to leave the
    // create form still filled in with the same values -- clicking "Create template" again would
    // create a second, duplicate template, since the first one was already created server-side.
    vi.mocked(api.templates.list).mockResolvedValue([]);
    vi.mocked(api.templates.create).mockResolvedValue(template);
    vi.mocked(api.templates.uploadPackage).mockRejectedValue(new Error("blob storage unavailable"));
    const user = userEvent.setup();
    renderComponent();

    await screen.findByText("No templates yet.");
    vi.mocked(api.templates.list).mockResolvedValue([template]);

    await user.type(screen.getByLabelText("Display name"), "Company Portal");
    await user.type(screen.getByLabelText("Install command line"), "install.exe");
    await user.type(screen.getByLabelText("Uninstall command line"), "uninstall.exe");
    const file = new File(["binary"], "package.intunewin");
    await user.upload(screen.getByLabelText("Attach package (.intunewin, optional)"), file);
    await user.click(screen.getByRole("button", { name: "Create template" }));

    expect(api.templates.create).toHaveBeenCalledTimes(1);
    expect(await screen.findByText(/was created, but the package upload failed/)).toBeInTheDocument();
    // The form is reset -- the display name field is empty again, and the attach-package button
    // shows its placeholder label rather than the previously selected file's name.
    expect(screen.getByLabelText("Display name")).toHaveValue("");
    expect(screen.queryByText("package.intunewin")).not.toBeInTheDocument();
    expect(screen.getByText("Attach package (.intunewin, optional)")).toBeInTheDocument();
    // createAction genuinely succeeded (the template itself was created) -- no spurious inline
    // "creation failed" error should appear next to the form.
    expect(screen.queryByText("blob storage unavailable")).not.toBeInTheDocument();

    // Clicking "Create template" again (form is empty, so this simulates the user not noticing
    // and pressing it again) must not be possible without re-filling the form -- disabled while
    // required fields are blank, so it can't silently fire a duplicate create.
    expect(screen.getByRole("button", { name: "Create template" })).toBeDisabled();
  });

  it("hides the create form and actions column for non-admin users", async () => {
    vi.mocked(api.templates.list).mockResolvedValue([template]);
    const viewer: MeProfile = { ...admin, isSystemAdmin: false };
    renderComponent(viewer);

    await screen.findByText("Company Portal");
    expect(screen.queryByLabelText("Display name")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit Company Portal" })).not.toBeInTheDocument();
  });
});
