#requires -Version 7.0
<#
  Runs a single Exchange Online operation using app-only certificate auth and emits a JSON result
  object on stdout: { success, steps: [{name,success,detail}], data }.
  Invoked as:  pwsh -NoProfile -NonInteractive -File exo-op.ps1 -PayloadPath <json-file>
#>
param([Parameter(Mandatory)][string]$PayloadPath)

$ErrorActionPreference = 'Stop'
$steps = [System.Collections.Generic.List[object]]::new()
$data = $null
function Add-Step($name, $ok, $detail) { $steps.Add([ordered]@{ name = $name; success = $ok; detail = $detail }) }

# Run one remediation action, recording success/failure without aborting the remaining steps.
function Invoke-Step($name, [scriptblock]$action) {
    try { $detail = & $action; Add-Step $name $true $detail }
    catch { Add-Step $name $false $_.Exception.Message }
}

$EmptyGuid = '00000000-0000-0000-0000-000000000000'

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
    $payload = Get-Content -Raw -Path $PayloadPath | ConvertFrom-Json
    $c = $payload.connect
    $p = $payload.params
    $id = $p.identity

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $connectArgs = @{ AppId = $c.appId; Organization = $c.organization; ShowBanner = $false }
    if ($c.certificatePath) {
        $connectArgs.CertificateFilePath = $c.certificatePath
        if ($c.certificatePassword) {
            $connectArgs.CertificatePassword = (ConvertTo-SecureString $c.certificatePassword -AsPlainText -Force)
        }
    }
    Connect-ExchangeOnline @connectArgs | Out-Null
    Add-Step 'Connect' $true $c.organization

    switch ($payload.operation) {
        'getMailbox' {
            $mbx = Get-EXOMailbox -Identity $id -Properties ForwardingSmtpAddress, DeliverToMailboxAndForward
            $data = [ordered]@{
                userPrincipalName          = $mbx.UserPrincipalName
                displayName                = $mbx.DisplayName
                recipientTypeDetails       = "$($mbx.RecipientTypeDetails)"
                forwardingSmtpAddress      = $mbx.ForwardingSmtpAddress
                deliverToMailboxAndForward = [bool]$mbx.DeliverToMailboxAndForward
            }
            Add-Step 'Get mailbox' $true $mbx.UserPrincipalName
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
            Invoke-Step 'Assign retention policy' {
                if ([string]::IsNullOrWhiteSpace("$($mbx.RetentionPolicy)") -and $p.retentionPolicyName) {
                    Set-Mailbox -Identity $id -RetentionPolicy $p.retentionPolicyName; $p.retentionPolicyName
                } else { "$($mbx.RetentionPolicy)" }
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
        default { throw "Unknown operation '$($payload.operation)'." }
    }
}
catch {
    Add-Step 'Error' $false $_.Exception.Message
}
finally {
    try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch {}
}

$result = [ordered]@{
    success = -not ($steps | Where-Object { -not $_.success })
    steps   = $steps
    data    = $data
}
$result | ConvertTo-Json -Depth 6 -Compress
