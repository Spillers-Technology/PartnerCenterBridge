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

try {
    $payload = Get-Content -Raw -Path $PayloadPath | ConvertFrom-Json
    $c = $payload.connect

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $connectArgs = @{
        AppId        = $c.appId
        Organization = $c.organization
        ShowBanner   = $false
    }
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
            $mbx = Get-EXOMailbox -Identity $payload.params.identity -Properties ForwardingSmtpAddress, DeliverToMailboxAndForward
            $data = [ordered]@{
                userPrincipalName        = $mbx.UserPrincipalName
                displayName              = $mbx.DisplayName
                recipientTypeDetails     = "$($mbx.RecipientTypeDetails)"
                forwardingSmtpAddress    = $mbx.ForwardingSmtpAddress
                deliverToMailboxAndForward = [bool]$mbx.DeliverToMailboxAndForward
            }
            Add-Step 'Get mailbox' $true $mbx.UserPrincipalName
        }
        'convertToShared' {
            Set-Mailbox -Identity $payload.params.identity -Type Shared
            Add-Step 'Convert to shared' $true $payload.params.identity
            if ($payload.params.forwardingSmtpAddress) {
                Set-Mailbox -Identity $payload.params.identity `
                    -ForwardingSmtpAddress $payload.params.forwardingSmtpAddress `
                    -DeliverToMailboxAndForward ([bool]$payload.params.deliverToMailboxAndForward)
                Add-Step 'Set forwarding' $true $payload.params.forwardingSmtpAddress
            }
        }
        'listShared' {
            $list = Get-EXOMailbox -RecipientTypeDetails SharedMailbox -ResultSize 500 -Properties ForwardingSmtpAddress, DeliverToMailboxAndForward
            $data = @($list | ForEach-Object {
                [ordered]@{
                    userPrincipalName        = $_.UserPrincipalName
                    displayName              = $_.DisplayName
                    recipientTypeDetails     = "$($_.RecipientTypeDetails)"
                    forwardingSmtpAddress    = $_.ForwardingSmtpAddress
                    deliverToMailboxAndForward = [bool]$_.DeliverToMailboxAndForward
                }
            })
            Add-Step 'List shared mailboxes' $true "$($data.Count) mailbox(es)"
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
