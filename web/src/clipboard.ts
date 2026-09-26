/**
 * Copies text to the clipboard. Resolves false instead of throwing when the Clipboard API is
 * missing (non-secure origin, some embeddings) or the write is refused, so callers can fall back
 * to "select it and copy manually".
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (!navigator.clipboard?.writeText) return false;
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** Selects an element's text so the user can copy it by hand (the clipboard fallback). */
export function selectContents(element: HTMLElement | null) {
  if (!element) return;
  try {
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(element);
    selection?.removeAllRanges();
    selection?.addRange(range);
  } catch {
    // Selection is a convenience; nothing else to do if the browser refuses.
  }
}
