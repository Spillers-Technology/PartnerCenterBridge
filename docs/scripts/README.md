# Capture scripts

The scripts in this directory render the real web client with mocked API responses. They need no
backend, database, or Microsoft tenant.

## `capture-product-media.mjs`

Captures the desktop product screenshots used by the documentation site. Output is written to
`docs/assets/screenshots/` as `pcbridge-*.jpg`.

### Playwright setup

Playwright is intentionally not a `package.json` dependency. Install it in a temporary directory
and point `PLAYWRIGHT_NODE_MODULES` at that directory's `node_modules` folder:

```powershell
npm install --prefix $env:TEMP\pcbridge-playwright playwright
$env:PLAYWRIGHT_NODE_MODULES="$env:TEMP\pcbridge-playwright\node_modules"
```

### Run

```bash
cd web && npm run dev
node docs/scripts/capture-product-media.mjs
```

## `capture-mobile-media.mjs`

Captures all 15 current views across five touch device profiles (75 captures) and checks each
view/device pair for page-level horizontal overflow. Output is written to
`docs/assets/screenshots/mobile/` by default; set `PCBRIDGE_CAPTURE_OUT` to override it.

It shares `mock-api.mjs` with `capture-product-media.mjs`, so both scripts render against the same
mocked API fixtures. Use the [Playwright setup](#playwright-setup) above, then run:

```bash
cd web && npm run dev
node docs/scripts/capture-mobile-media.mjs
```
