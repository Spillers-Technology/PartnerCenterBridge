PartnerCenterBridge - Local Workbench
======================================

This is a self-contained build of PartnerCenterBridge: one exe, no separate
install, no server to stand up.

Running it
----------
Double-click PartnerCenterBridge.exe, or from a terminal:

    PartnerCenterBridge.exe

It opens a desktop window containing the same PartnerCenterBridge web app.
The local API uses an automatically selected loopback port and closes with
the window. Microsoft Edge WebView2 Runtime is required; Windows 11 normally
includes it. Install the Evergreen Runtime if the app asks for it.

For the classic browser and tray mode, run PartnerCenterBridge.exe --browser.
Pass --port <N> --no-browser for a headless local API, or run doctor/help from
a terminal. These modes use the same data folder and application services.

Microsoft tenant sign-in
------------------------
Settings -> Microsoft connections -> Add tenant with Microsoft connects any
Microsoft 365 organization with its own admin account. Microsoft handles the
username, password, MFA and consent. Repeat to connect more tenants.
Release builds include PCB's public application ID; technicians do not need
to register an application in every customer tenant. No client secret is used.
The optional Partner Center/GDAP integration is separate from direct sign-in.

Tokens are encrypted per local operator and tenant and renewed silently.
If Microsoft requires another sign-in, use Reconnect beside the remembered
username. Exchange Online still uses its separate certificate configuration.

Data folder
-----------
Everything PartnerCenterBridge stores (its SQLite database, Data Protection
keys, logs, uploaded packages, and any generated config) lives under:

    %LOCALAPPDATA%\PartnerCenterBridge

Delete that folder to start over from a clean slate. Use --data-dir <path>
to point PartnerCenterBridge at a different folder instead (e.g. to run
more than one instance, or to keep the data somewhere else).

Diagnostics
-----------
Run the built-in doctor to check your environment before connecting real
tenants:

    PartnerCenterBridge.exe doctor

It reports what is and is not configured (Exchange Online PowerShell, SAM
integration, ...) -- nothing here is fatal by itself; it tells you what
each missing piece means. The same checks are available at
GET /api/system/diagnostics while PartnerCenterBridge is running.

Verifying this download
------------------------
This exe is Authenticode-signed. Right-click it, choose Properties ->
Digital Signatures, or run:

    Get-AuthenticodeSignature .\PartnerCenterBridge.exe | Format-List Status, SignerCertificate, TimeStamperCertificate

Check the zip you downloaded against its published .sha256 file:

    Get-FileHash <zip> -Algorithm SHA256
    Get-Content <zip>.sha256

(or `sha256sum -c <zip>.sha256`). Both should say Valid / match.

Documentation
-------------
Full docs, including Local Workbench setup and diagnostics:
https://spillerstech.us/PartnerCenterBridge/local-workbench.html
