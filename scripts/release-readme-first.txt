PartnerCenterBridge - Local Workbench
======================================

This is a self-contained build of PartnerCenterBridge: one exe, no separate
install, no server to stand up.

Running it
----------
Double-click PartnerCenterBridge.exe, or from a terminal:

    PartnerCenterBridge.exe

It opens your browser to http://localhost:5080 once it's healthy. Pass
--port <N> to use a different port, or --no-browser to skip opening one.

The bridge icon stays in the Windows notification area. Double-click to
reopen the workbench; right-click to open logs or exit. Closing the browser
does not stop it. Explorer launches close their console once the tray is
ready. Use --Hosting:Tray=false to keep console operation.

Microsoft tenant sign-in
------------------------
Tenants -> Add tenant with Microsoft connects a tenant with its own admin
account. Configure MicrosoftSignIn:ClientId once in pcb.local.json using
a multitenant desktop app registration and http://localhost redirect.
The app needs delegated Graph permissions (including Organization.Read.All)
and tenant consent. No client secret is used. See the documentation for setup.

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
