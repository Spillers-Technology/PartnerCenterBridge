#requires -Version 7.0
<#
  Runs a single Exchange Online operation using app-only certificate auth and emits a JSON result
  object on stdout: { success, steps: [{name,success,detail}], data, notFound, tenantMismatch }. notFound is true
  only when the mailbox lookup itself reported that no such mailbox exists (getMailbox); every
  other failure (module, connect, auth, throttling) is a failed step with notFound false.
  Invoked as:  pwsh -NoProfile -NonInteractive -File exo-op.ps1   with the JSON payload written to
  standard input (UTF-8) and stdin then closed. The payload is never passed as a file or argument.
  Before any operation it checks that the connection's tenant id equals payload.expectedTenantId.
#>
param()

$ErrorActionPreference = 'Stop'
$steps = [System.Collections.Generic.List[object]]::new()
$data = $null
$notFound = $false
$tenantMismatch = $false
function Add-Step($name, $ok, $detail) { $steps.Add([ordered]@{ name = $name; success = $ok; detail = $detail }) }

# Run one remediation action, recording success/failure without aborting the remaining steps.
function Invoke-Step($name, [scriptblock]$action) {
    try { $detail = & $action; Add-Step $name $true $detail }
    catch { Add-Step $name $false $_.Exception.Message }
}

$EmptyGuid = '00000000-0000-0000-0000-000000000000'

# Tenant id(s) of the live Exchange connection. EXO V3 reports it as Get-ConnectionInformation's
# TenantID (checked against ExchangeOnlineManagement 3.10.1); Get-OrganizationConfig's
# ExternalDirectoryOrganizationId is the fallback if no connection reports one.
function Get-ConnectedTenantIds {
    $ids = @()
    if (Get-Command Get-ConnectionInformation -ErrorAction SilentlyContinue) {
        $ids = @(Get-ConnectionInformation | ForEach-Object { "$($_.TenantID)" } | Where-Object { $_ })
    }
    if ($ids.Count -eq 0 -and (Get-Command Get-OrganizationConfig -ErrorAction SilentlyContinue)) {
        $ids = @("$((Get-OrganizationConfig).ExternalDirectoryOrganizationId)" | Where-Object { $_ })
    }
    return $ids
}

function Test-SameTenant($a, $b) {
    $ga = [guid]::Empty; $gb = [guid]::Empty
    if ([guid]::TryParse("$a", [ref]$ga) -and [guid]::TryParse("$b", [ref]$gb)) { return $ga -eq $gb }
    return ("$a" -ieq "$b") -and "$a" -ne ""
}

# True when an error record from a recipient lookup means "no such object". Checked structurally
# first (error category, exception type anywhere in the chain, error id); the message fallback is
# only ever applied to the lookup's own error, never to connect/auth failures.
function Test-ObjectNotFound($err) {
    if ("$($err.CategoryInfo.Category)" -eq 'ObjectNotFound') { return $true }
    if ("$($err.FullyQualifiedErrorId)" -match 'ManagementObjectNotFound') { return $true }
    $e = $err.Exception
    while ($e) {
        if ($e.GetType().Name -match 'ObjectNotFound') { return $true }
        $e = $e.InnerException
    }
    $msg = "$($err.Exception.Message)"
    return ($msg -match "couldn't be found|could not be found|ManagementObjectNotFound")
}

# Build the archive-posture snapshot shared by the diagnose / remediate / nudge operations.
function Get-ArchiveStateData($id) {
    $mbx = Get-Mailbox -Identity $id
    $primary = Get-MailboxStatistics -Identity $id -ErrorAction SilentlyContinue
    $archiveEnabled = "$($mbx.ArchiveGuid)" -ne $EmptyGuid
    $archiveStats = if ($archiveEnabled) { Get-MailboxStatistics -Identity $id -Archive -ErrorAction SilentlyContinue } else { $null }
    [ordered]@{
        userPrincipalName           = $mbx.UserPrincipalName
        primarySize                 = "$($primary.TotalItemSize)"
        primaryItemCount            = [int64]$primary.ItemCount
        prohibitSendReceiveQuota    = "$($mbx.ProhibitSendReceiveQuota)"
        archiveEnabled              = [bool]$archiveEnabled
        archiveStatus               = "$($mbx.ArchiveStatus)"
        autoExpandingArchiveEnabled = [bool]$mbx.AutoExpandingArchiveEnabled
        archiveQuota                = "$($mbx.ArchiveQuota)"
        archiveWarningQuota         = "$($mbx.ArchiveWarningQuota)"
        archiveSize                 = if ($archiveStats) { "$($archiveStats.TotalItemSize)" } else { $null }
        archiveItemCount            = if ($archiveStats) { [int64]$archiveStats.ItemCount } else { 0 }
        retentionPolicy             = "$($mbx.RetentionPolicy)"
        retentionHoldEnabled        = [bool]$mbx.RetentionHoldEnabled
        elcProcessingDisabled       = [bool]$mbx.ElcProcessingDisabled
    }
}

try {
    # The payload (which can hold the PFX password) arrives on stdin as UTF-8 and is never on disk.
    $stdin = [Console]::OpenStandardInput()
    $buffer = [System.IO.MemoryStream]::new()
    $stdin.CopyTo($buffer)
    $raw = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
    $buffer.Dispose()
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'No payload was supplied on standard input.' }
    $payload = $raw | ConvertFrom-Json
    $raw = $null
    $c = $payload.connect
    $p = $payload.params
    $id = $p.identity

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $connectArgs = @{ AppId = $c.appId; Organization = $c.organization; ShowBanner = $false }
    if ($c.certificateThumbprint) {
        # Windows certificate store: no certificate secret involved.
        $connectArgs.CertificateThumbprint = $c.certificateThumbprint
    }
    elseif ($c.certificatePath) {
        $connectArgs.CertificateFilePath = $c.certificatePath
        if ($c.certificatePassword) {
            $connectArgs.CertificatePassword = (ConvertTo-SecureString $c.certificatePassword -AsPlainText -Force)
        }
    }
    Connect-ExchangeOnline @connectArgs | Out-Null
    Add-Step 'Connect' $true $c.organization

    # Bind the connection to the Entra tenant before touching anything: an organization name that
    # resolves to some other directory the shared app can reach must not receive this operation.
    $expected = "$($payload.expectedTenantId)"
    $connected = @(Get-ConnectedTenantIds)
    $mismatch = if (-not $expected) { 'no expected tenant id was supplied' }
        elseif ($connected.Count -eq 0) { 'the connected tenant id could not be determined' }
        elseif (@($connected | Where-Object { -not (Test-SameTenant $_ $expected) }).Count -gt 0) {
            "connected to tenant $($connected -join ', '), expected $expected" }
        else { $null }
    if ($mismatch) {
        $tenantMismatch = $true
        Add-Step 'Tenant check' $false $mismatch
        throw "Tenant check failed: $mismatch"
    }
    Add-Step 'Tenant check' $true $expected

    switch ($payload.operation) {
        'getMailbox' {
            $mbx = $null
            try {
                $mbx = Get-EXOMailbox -Identity $id -Properties ForwardingSmtpAddress, DeliverToMailboxAndForward
            }
            catch {
                if (-not (Test-ObjectNotFound $_)) { throw }
                $notFound = $true
                Add-Step 'Mailbox not found' $true "Exchange Online has no mailbox '$id'."
            }
            if (-not $notFound) {
                if (-not $mbx) { throw "Get-EXOMailbox returned nothing for '$id' without reporting it missing." }
                $data = [ordered]@{
                    userPrincipalName          = $mbx.UserPrincipalName
                    displayName                = $mbx.DisplayName
                    recipientTypeDetails       = "$($mbx.RecipientTypeDetails)"
                    forwardingSmtpAddress      = $mbx.ForwardingSmtpAddress
                    deliverToMailboxAndForward = [bool]$mbx.DeliverToMailboxAndForward
                }
                Add-Step 'Get mailbox' $true $mbx.UserPrincipalName
            }
        }
        'convertToShared' {
            Set-Mailbox -Identity $id -Type Shared
            Add-Step 'Convert to shared' $true $id
            if ($p.forwardingSmtpAddress) {
                Set-Mailbox -Identity $id -ForwardingSmtpAddress $p.forwardingSmtpAddress `
                    -DeliverToMailboxAndForward ([bool]$p.deliverToMailboxAndForward)
                Add-Step 'Set forwarding' $true $p.forwardingSmtpAddress
            }
        }
        'listShared' {
            $list = Get-EXOMailbox -RecipientTypeDetails SharedMailbox -ResultSize 500 -Properties ForwardingSmtpAddress, DeliverToMailboxAndForward
            $data = @($list | ForEach-Object {
                [ordered]@{
                    userPrincipalName          = $_.UserPrincipalName
                    displayName                = $_.DisplayName
                    recipientTypeDetails       = "$($_.RecipientTypeDetails)"
                    forwardingSmtpAddress      = $_.ForwardingSmtpAddress
                    deliverToMailboxAndForward = [bool]$_.DeliverToMailboxAndForward
                }
            })
            Add-Step 'List shared mailboxes' $true "$($data.Count) mailbox(es)"
        }
        'getArchiveState' {
            $data = Get-ArchiveStateData $id
            Add-Step 'Get archive state' $true $data.userPrincipalName
        }
        'remediateArchive' {
            $mbx = Get-Mailbox -Identity $id
            $archiveEnabled = "$($mbx.ArchiveGuid)" -ne $EmptyGuid

            Invoke-Step 'Enable archive' {
                if (-not $archiveEnabled) { Enable-Mailbox -Identity $id -Archive | Out-Null; 'archive enabled' }
                else { 'already enabled' }
            }
            if ($p.enableAutoExpandingArchive) {
                Invoke-Step 'Enable auto-expanding archive' {
                    if (-not $mbx.AutoExpandingArchiveEnabled) { Enable-Mailbox -Identity $id -AutoExpandingArchive | Out-Null; 'enabled' }
                    else { 'already enabled' }
                }
            }
            # Detail tells an assignment ("assigned: X") apart from a policy that was already there
            # ("already assigned: X"), so only an actual assignment is verified as a change.
            Invoke-Step 'Assign retention policy' {
                if (-not [string]::IsNullOrWhiteSpace("$($mbx.RetentionPolicy)")) { "already assigned: $($mbx.RetentionPolicy)" }
                elseif ($p.retentionPolicyName) {
                    Set-Mailbox -Identity $id -RetentionPolicy $p.retentionPolicyName; "assigned: $($p.retentionPolicyName)"
                }
                else { 'not set' }
            }
            if ($p.clearProcessingBlocks) {
                Invoke-Step 'Clear retention hold' {
                    if ($mbx.RetentionHoldEnabled) { Set-Mailbox -Identity $id -RetentionHoldEnabled $false; 'disabled' } else { 'not set' }
                }
                Invoke-Step 'Enable ELC processing' {
                    if ($mbx.ElcProcessingDisabled) { Set-Mailbox -Identity $id -ElcProcessingDisabled $false; 'enabled' } else { 'already enabled' }
                }
            }
            if ($p.triggerProcessing) {
                Invoke-Step 'Trigger Managed Folder Assistant' { Start-ManagedFolderAssistant -Identity $id; 'processing started' }
            }
            try { $data = Get-ArchiveStateData $id } catch { Add-Step 'Refresh state' $false $_.Exception.Message }
        }
        'nudgeArchive' {
            Invoke-Step 'Trigger Managed Folder Assistant' { Start-ManagedFolderAssistant -Identity $id; $id }
            try { $data = Get-ArchiveStateData $id } catch { Add-Step 'Refresh state' $false $_.Exception.Message }
        }
        'auditMailboxes' {
            # READ-ONLY (tenant audits): Get-* cmdlets only. Each part records its own failure so
            # one denied read (for example SendAs) does not hide the rest.
            $maxFullAccess = if ($p.maxFullAccessMailboxes) { [int]$p.maxFullAccessMailboxes } else { 200 }
            $fullAccessSeconds = if ($p.fullAccessSeconds) { [int]$p.fullAccessSeconds } else { 90 }
            $partial = [System.Collections.Generic.List[string]]::new()

            $mailboxes = $null
            try {
                $mailboxes = @(Get-EXOMailbox -ResultSize Unlimited -PropertySets Minimum, Delivery, Archive, Hold)
            }
            catch {
                # Older module versions or roles without the property sets: fall back to the classic cmdlet.
                $mailboxes = @(Get-Mailbox -ResultSize Unlimited)
            }

            $recipientCache = @{}
            function Resolve-RecipientSmtp($identity) {
                if (-not $identity) { return $null }
                $key = "$identity"
                if (-not $recipientCache.ContainsKey($key)) {
                    try { $recipientCache[$key] = "$((Get-EXORecipient -Identity $key -ErrorAction Stop).PrimarySmtpAddress)" }
                    catch { $recipientCache[$key] = $null }
                }
                return $recipientCache[$key]
            }

            $mbxData = @($mailboxes | ForEach-Object {
                [ordered]@{
                    objectId                    = "$($_.ExternalDirectoryObjectId)"
                    userPrincipalName           = $_.UserPrincipalName
                    displayName                 = $_.DisplayName
                    primarySmtpAddress          = "$($_.PrimarySmtpAddress)"
                    recipientTypeDetails        = "$($_.RecipientTypeDetails)"
                    forwardingSmtpAddress       = if ($_.ForwardingSmtpAddress) { "$($_.ForwardingSmtpAddress)" } else { $null }
                    forwardingAddress           = if ($_.ForwardingAddress) { "$($_.ForwardingAddress)" } else { $null }
                    forwardingAddressSmtp       = Resolve-RecipientSmtp $_.ForwardingAddress
                    deliverToMailboxAndForward  = [bool]$_.DeliverToMailboxAndForward
                    archiveEnabled              = ("$($_.ArchiveGuid)" -ne '' -and "$($_.ArchiveGuid)" -ne $EmptyGuid) -or ("$($_.ArchiveStatus)" -eq 'Active')
                    archiveStatus               = "$($_.ArchiveStatus)"
                    autoExpandingArchiveEnabled = [bool]$_.AutoExpandingArchiveEnabled
                    litigationHoldEnabled       = [bool]$_.LitigationHoldEnabled
                    grantSendOnBehalfTo         = @($_.GrantSendOnBehalfTo | ForEach-Object { "$_" })
                }
            })
            Add-Step 'List mailboxes' $true "$($mbxData.Count) mailbox(es)"

            $domains = @()
            try { $domains = @(Get-AcceptedDomain | ForEach-Object { "$($_.DomainName)" }) }
            catch { $partial.Add("Accepted domains: $($_.Exception.Message)") }

            $autoForwarding = $null
            try {
                $policy = Get-HostedOutboundSpamFilterPolicy | Where-Object { $_.IsDefault } | Select-Object -First 1
                if ($policy) { $autoForwarding = "$($policy.AutoForwardingMode)" }
            }
            catch { $partial.Add("Outbound spam policy: $($_.Exception.Message)") }

            $permissions = [System.Collections.Generic.List[object]]::new()
            try {
                Get-EXORecipientPermission -ResultSize Unlimited -ErrorAction Stop |
                    Where-Object { $_.Trustee -ne 'NT AUTHORITY\SELF' -and -not $_.IsInherited -and "$($_.AccessControlType)" -eq 'Allow' -and $_.AccessRights -contains 'SendAs' } |
                    ForEach-Object { $permissions.Add([ordered]@{ mailbox = "$($_.Identity)"; trustee = "$($_.Trustee)"; right = 'SendAs' }) }
            }
            catch { $partial.Add("Send As permissions: $($_.Exception.Message)") }

            # Full Access is per mailbox: bounded by count and time so a large tenant cannot hang the audit.
            $evaluated = 0
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            foreach ($m in $mailboxes) {
                if ($evaluated -ge $maxFullAccess -or $clock.Elapsed.TotalSeconds -ge $fullAccessSeconds) { break }
                try {
                    Get-EXOMailboxPermission -Identity $m.UserPrincipalName -ErrorAction Stop |
                        Where-Object { $_.User -ne 'NT AUTHORITY\SELF' -and -not $_.IsInherited -and -not $_.Deny -and $_.AccessRights -contains 'FullAccess' } |
                        ForEach-Object { $permissions.Add([ordered]@{ mailbox = "$($m.UserPrincipalName)"; trustee = "$($_.User)"; right = 'FullAccess' }) }
                }
                catch { $partial.Add("Full Access for $($m.UserPrincipalName): $($_.Exception.Message)") }
                $evaluated++
            }

            $data = [ordered]@{
                mailboxes           = $mbxData
                acceptedDomains     = $domains
                autoForwardingMode  = $autoForwarding
                permissions         = $permissions
                fullAccessEvaluated = $evaluated
                fullAccessComplete  = ($evaluated -ge $mailboxes.Count)
                partialErrors       = $partial
            }
            Add-Step 'Read mailbox audit data' $true "$($permissions.Count) delegation(s)"
        }
        default { throw "Unknown operation '$($payload.operation)'." }
    }
}
catch {
    # A failed tenant check already recorded why; nothing ran after it.
    if (-not $tenantMismatch) { Add-Step 'Error' $false $_.Exception.Message }
}
finally {
    try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch {}
}

$result = [ordered]@{
    success = -not ($steps | Where-Object { -not $_.success })
    steps   = $steps
    data    = $data
    notFound = [bool]$notFound
    tenantMismatch = [bool]$tenantMismatch
}
$result | ConvertTo-Json -Depth 6 -Compress
