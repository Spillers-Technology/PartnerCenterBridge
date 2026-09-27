import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithRouter } from "../test/renderWithRouter";
import { EvidencePanel } from "./EvidencePanel";
import type { Outcome } from "../types";
import { makeEvidence } from "../test/fixtures";

vi.mock("../api", () => ({
  api: { workflows: { evidenceMarkdown: vi.fn(), evidenceJson: vi.fn() } }
}));

import { api } from "../api";

describe("EvidencePanel", () => {
  const originalClipboard = navigator.clipboard;

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.workflows.evidenceMarkdown).mockResolvedValue(undefined);
    vi.mocked(api.workflows.evidenceJson).mockResolvedValue(undefined);
  });

  afterEach(() => {
    Object.defineProperty(navigator, "clipboard", { value: originalClipboard, configurable: true });
  });

  it.each<[Outcome, RegExp]>([
    ["Succeeded", /Every requested change was made and a re-read of the tenant confirmed it/],
    ["PartiallySucceeded", /Some changes were made and verified; others were not/],
    ["VerificationFailed", /re-reading the tenant did not confirm them/],
    ["Failed", /No requested change was verified/],
    ["NoChangeNeeded", /already in place\. PCB changed nothing/],
    ["Planned", /A plan was produced\. Nothing was changed/]
  ])("describes a %s outcome honestly", (outcome, wording) => {
    renderWithRouter(<EvidencePanel evidence={makeEvidence({ outcome })} />);
    const banner = screen.getByTestId("evidence-outcome");
    expect(banner).toHaveTextContent(wording);
    if (outcome !== "Succeeded") expect(banner).not.toHaveTextContent(/^Succeeded/);
  });

  it("shows who, what, where and links the target to their workspace", () => {
    renderWithRouter(<EvidencePanel evidence={makeEvidence()} />);
    expect(screen.getByRole("link", { name: "Grace Hopper" })).toHaveAttribute("href", "/people/t1/u2");
    expect(screen.getByRole("link", { name: "Contoso Ltd" })).toHaveAttribute("href", "/tenants/t1");
    expect(screen.getByText("tech@example.com")).toBeInTheDocument();
  });

  it("counts attempted, completed and skipped changes and labels each one", () => {
    renderWithRouter(<EvidencePanel evidence={makeEvidence()} />);
    expect(screen.getByText("2 attempted, 1 completed, 1 skipped")).toBeInTheDocument();
    const list = screen.getByRole("list", { name: "Changes" });
    expect(within(list).getByText("Done")).toBeInTheDocument();
    expect(within(list).getByText("Failed")).toBeInTheDocument();
    expect(within(list).getByText("Already in place")).toBeInTheDocument();
    expect(within(list).getByText("403 Forbidden")).toBeInTheDocument();
  });

  it("shows warnings, limitations and the ticket notes text", () => {
    renderWithRouter(<EvidencePanel evidence={makeEvidence({ failures: ["Could not add VPN Users."] })} />);
    expect(screen.getByRole("list", { name: "Warnings" })).toHaveTextContent("synced from on-premises");
    expect(screen.getByRole("list", { name: "Limitations" })).toHaveTextContent("SharePoint direct permissions");
    expect(screen.getByRole("list", { name: "Failures" })).toHaveTextContent("Could not add VPN Users.");
    expect(screen.getByLabelText("Ticket notes text")).toHaveTextContent("Added to Finance Team (verified)");
  });

  it("copies the ticket notes and confirms with a toast", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const user = userEvent.setup();
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderWithRouter(<EvidencePanel evidence={makeEvidence()} />);
    await user.click(screen.getByRole("button", { name: "Copy ticket notes" }));
    expect(writeText).toHaveBeenCalledWith("Access parity for Grace Hopper\n- Added to Finance Team (verified)");
    expect(await screen.findByText("Ticket notes copied")).toBeInTheDocument();
  });

  it("falls back to selecting the notes when the clipboard API is unavailable", async () => {
    const user = userEvent.setup();
    renderWithRouter(<EvidencePanel evidence={makeEvidence()} />);
    Object.defineProperty(navigator, "clipboard", { value: undefined, configurable: true });
    await user.click(screen.getByRole("button", { name: "Copy ticket notes" }));
    expect(await screen.findByText(/Couldn't copy automatically/)).toBeInTheDocument();
  });

  it("downloads the Markdown and JSON evidence for the run", async () => {
    const user = userEvent.setup();
    renderWithRouter(<EvidencePanel evidence={makeEvidence()} />);
    await user.click(screen.getByRole("button", { name: "Download Markdown" }));
    expect(api.workflows.evidenceMarkdown).toHaveBeenCalledWith("run-1");
    await user.click(screen.getByRole("button", { name: "Download JSON" }));
    expect(api.workflows.evidenceJson).toHaveBeenCalledWith("run-1");
  });
});
