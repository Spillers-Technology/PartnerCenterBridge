# PartnerCenterBridge brand assets

`app-icon.png` follows the repository's `image.png` branding suggestion.
The matching source lives in `web/public/brand/app-icon.png`.
`scripts/build-brand-icon.ps1` deterministically creates the multi-resolution ICO files for
the executable, native tray, SPA favicon and documentation favicon from that PNG.

Generation prompt:

> Extract the app icon in the top left of this branding board as one standalone square app icon.
> Preserve the exact deep navy rounded square and cyan blue bridge/workbench symbol design.
> No text, no surrounding board, no additional symbols. Fill image with the icon, transparent
> outside its rounded corners. This is the PartnerCenterBridge production app icon.

Palette: deep navy `#0B1E3B`, azure `#00C2FF`, slate `#64748B`, mist `#E9EDF2`.
Light surfaces use a darker azure for readable text and controls.
