import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { formatTimestamp, humanizeEnum, pluralize, Timestamp } from "./format";

describe("humanizeEnum", () => {
  it("turns PascalCase enum values into human-readable, sentence-cased text", () => {
    expect(humanizeEnum("UpdateAvailable")).toBe("Update available");
    expect(humanizeEnum("NoDelegation")).toBe("No delegation");
  });

  it("leaves already-simple single-word values alone (aside from casing)", () => {
    expect(humanizeEnum("Active")).toBe("Active");
    expect(humanizeEnum("Succeeded")).toBe("Succeeded");
    expect(humanizeEnum("Failed")).toBe("Failed");
    expect(humanizeEnum("Pending")).toBe("Pending");
  });

  it("passes through an empty string", () => {
    expect(humanizeEnum("")).toBe("");
  });
});

describe("pluralize", () => {
  it("uses the singular form for exactly one", () => {
    expect(pluralize(1, "tenant")).toBe("1 tenant");
  });

  it("uses the plural form for zero or many", () => {
    expect(pluralize(0, "tenant")).toBe("0 tenants");
    expect(pluralize(3, "tenant")).toBe("3 tenants");
  });

  it("accepts an irregular plural", () => {
    expect(pluralize(1, "failure")).toBe("1 failure");
    expect(pluralize(2, "failure")).toBe("2 failures");
  });
});

describe("formatTimestamp", () => {
  it("omits the year when it matches the current year", () => {
    const now = new Date();
    const iso = new Date(now.getFullYear(), 8, 17, 0, 4).toISOString();
    const { text } = formatTimestamp(iso);
    expect(text).not.toContain(String(now.getFullYear()));
  });

  it("includes the year when it differs from the current year", () => {
    const { text } = formatTimestamp("2020-01-05T10:00:00Z");
    expect(text).toContain("2020");
  });

  it("has no seconds in the short text, but keeps the full timestamp available", () => {
    const { text, full } = formatTimestamp("2026-08-19T09:00:33Z");
    expect(text).not.toMatch(/:\d{2}:\d{2}/); // no HH:MM:SS anywhere
    expect(full.length).toBeGreaterThan(0);
  });
});

describe("Timestamp", () => {
  it("renders the short text with the full timestamp as a title attribute", () => {
    render(<Timestamp value="2026-08-19T09:00:00Z" />);
    const el = screen.getByText(formatTimestamp("2026-08-19T09:00:00Z").text);
    expect(el).toHaveAttribute("title", formatTimestamp("2026-08-19T09:00:00Z").full);
  });

  it("renders the fallback when there is no value", () => {
    render(<Timestamp value={undefined} fallback="-" />);
    expect(screen.getByText("-")).toBeInTheDocument();
  });
});
