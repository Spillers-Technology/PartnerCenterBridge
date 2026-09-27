#!/usr/bin/env bash
# Builds the public docs/ site into a standalone directory: the plain-HTML pages, their
# stylesheet, and the assets they reference -- nothing else. Used by both
# .github/workflows/release.yml's pages job and .github/workflows/pages.yml (the doc-only
# redeploy path), so the two never drift on what "the public site" means.
#
# Deliberately excludes docs/*.md (dev-process.md, mobile.md, standards.md), docs/specs,
# docs/plans, docs/scripts, and docs/assets/screenshots/mobile/ (mobile capture-matrix working
# artifacts, not part of the shipped site) -- none of that is meant to be publicly served.
#
# Usage: ./scripts/build-site.sh [output-dir]   (default: ./site-out)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$repo_root/site-out}"

rm -rf "$out"
mkdir -p "$out"

cp "$repo_root"/docs/*.html "$out"/
cp "$repo_root"/docs/styles.css "$out"/
if [ -f "$repo_root/docs/.nojekyll" ]; then
  cp "$repo_root/docs/.nojekyll" "$out"/
else
  touch "$out/.nojekyll"
fi

# docs/assets/**, excluding the mobile capture-matrix working directory.
mkdir -p "$out/assets"
(cd "$repo_root/docs/assets" && find . -type f ! -path './screenshots/mobile/*' -print0) |
  while IFS= read -r -d '' f; do
    mkdir -p "$out/assets/$(dirname "$f")"
    cp "$repo_root/docs/assets/$f" "$out/assets/$f"
  done

echo "Site built at $out"

# Link check: every local href/src in the bundled HTML must resolve within the bundle. A
# reference to something deliberately excluded (e.g. docs/index.html's link to
# docs/scripts/capture-product-media.mjs, a "view the capture tool" link, not a page asset) is
# reported as a note rather than failing the build -- it 404s on the live site same as before,
# which is a docs-content issue for whoever edits docs/index.html, not something this script can
# fix by bundling excluded directories.
broken_log="$(mktemp)"
note_log="$(mktemp)"

while IFS= read -r html; do
  dir="$(dirname "$html")"
  while IFS= read -r ref; do
    case "$ref" in
      http://*|https://*|mailto:*|data:*|\#*|//*) continue ;;
    esac
    path="${ref%%#*}"
    [ -z "$path" ] && continue
    target="$dir/$path"
    if [ ! -f "$target" ]; then
      ext="${path##*.}"
      case "$ext" in
        css|js|jpg|jpeg|png|svg|ico|html)
          echo "$html -> $ref" >> "$broken_log" ;;
        *)
          echo "$html -> $ref" >> "$note_log" ;;
      esac
    fi
  done < <(grep -ohE '(href|src)="[^"]+"' "$html" | sed -E 's/^(href|src)="//; s/"$//')
done < <(find "$out" -maxdepth 1 -name '*.html')

if [ -s "$note_log" ]; then
  echo ""
  echo "Notes (references outside the public site bundle by design, not failing the build):"
  cat "$note_log"
fi

if [ -s "$broken_log" ]; then
  echo ""
  echo "Broken references (missing from the site bundle):" >&2
  cat "$broken_log" >&2
  rm -f "$broken_log" "$note_log"
  exit 1
fi

rm -f "$broken_log" "$note_log"
echo ""
echo "Link check passed."
