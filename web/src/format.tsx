// Shared display-formatting helpers -- humanizing raw API enum values, formatting timestamps
// consistently, and pluralizing counts -- so every screen that shows this kind of data reads the
// same way instead of each component growing its own one-off formatting.

/**
 * Turn a PascalCase API enum value (e.g. DeploymentStatus "UpdateAvailable", TenantStatus
 * "NoDelegation") into human-readable text ("Update available", "No delegation"). Purely a display
 * transform -- the underlying API value is never changed, so this must only be used at render time.
 */
export function humanizeEnum(value: string): string {
  if (!value) return value;
  const spaced = value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** "1 tenant" / "3 tenants" -- avoids the raw, unpluralized "N tenant(s)" placeholder text. */
export function pluralize(count: number, noun: string, plural = `${noun}s`): string {
  return `${count} ${count === 1 ? noun : plural}`;
}

/**
 * A compact display string for a timestamp (no seconds, e.g. "Sep 17, 12:04 AM"), including the
 * year only when it differs from the current year, plus the full locale timestamp for a title
 * attribute/tooltip so nothing is lost -- just not shown by default in narrow table columns.
 */
export function formatTimestamp(iso: string): { text: string; full: string } {
  const d = new Date(iso);
  const now = new Date();
  const month = d.toLocaleDateString(undefined, { month: "short" });
  const time = d.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
  const text = d.getFullYear() !== now.getFullYear()
    ? `${month} ${d.getDate()}, ${d.getFullYear()}, ${time}`
    : `${month} ${d.getDate()}, ${time}`;
  return { text, full: d.toLocaleString() };
}

/**
 * Renders a timestamp using formatTimestamp(), with the full value available as a tooltip/title.
 * `fallback` covers the optional-timestamp case (e.g. a deployment that hasn't synced yet).
 */
export function Timestamp({ value, fallback = "-" }: { value?: string | null; fallback?: string }) {
  if (!value) return <>{fallback}</>;
  const { text, full } = formatTimestamp(value);
  return <span title={full}>{text}</span>;
}
