#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs (or removes) the Wings Notification Hub worker as a Windows service.

.DESCRIPTION
  - Copies a published worker (dotnet publish src\Wings.NotificationHub.Worker -c Release -r win-x64 --self-contained false -o <dir>)
    to -InstallPath (optional) and registers it with New-Service.
  - Service name defaults to WingsNotificationHubWorker, the name the worker passes to AddWindowsService.
  - Runs as a virtual service account (NT SERVICE\<name>) by default, or as a gMSA/domain account (-Account / -Credential).
  - Delayed automatic start; recovery: restart after 5 s, 15 s, 60 s; failure counter reset after 1 day; recovery also
    applies when the process exits with an error code (failureflag).
  - Settings/secrets from -EnvironmentFile (KEY=VALUE lines, e.g. ConnectionStrings__NotificationHub=...) are stored in the
    service's registry Environment value, readable only by administrators and SYSTEM.
  - Grants the service account Modify on -DataDirectory (Hub:DataDirectory: attachments, generated files).

.EXAMPLE
  .\install-worker.ps1 -SourcePath C:\build\worker -EnvironmentFile C:\secure\worker.env -Start

.EXAMPLE
  .\install-worker.ps1 -Account 'CONTOSO\svc-wnh-worker$' -EnvironmentFile .\worker.env -Start   # gMSA (note the trailing $)

.EXAMPLE
  .\install-worker.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ServiceName = 'WingsNotificationHubWorker',
    [string] $DisplayName = 'Wings Notification Hub Worker',
    [string] $Description = 'Dispatches notifications, retries, schedules, reconciles provider status and delivers webhooks for Wings Notification Hub.',
    [string] $InstallPath = 'C:\Program Files\Wings\NotificationHub\Worker',
    [string] $SourcePath,
    [string] $DataDirectory = 'C:\ProgramData\WingsNotificationHub',
    [string] $EnvironmentFile,
    [ValidateSet('Production', 'Staging', 'Development')]
    [string] $Environment = 'Production',
    # 'NT SERVICE\<ServiceName>' (virtual account, default), 'DOMAIN\gmsa$' or 'NT AUTHORITY\NetworkService'.
    [string] $Account,
    # A regular domain/local account with a password (alternative to -Account).
    [pscredential] $Credential,
    [switch] $Start,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

function Invoke-Sc([string[]] $Arguments) {
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed ($LASTEXITCODE): $output" }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($Uninstall) {
    if (-not $existing) { Write-Host "Service $ServiceName is not installed."; return }
    if ($existing.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    Invoke-Sc @('delete', $ServiceName)
    Write-Host "Removed service $ServiceName. Files in $InstallPath and $DataDirectory were left in place."
    return
}

# -- files --
if ($SourcePath) {
    if ($existing -and $existing.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName to update files..."
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}
$exe = Join-Path $InstallPath 'Wings.NotificationHub.Worker.exe'
$dll = Join-Path $InstallPath 'Wings.NotificationHub.Worker.dll'
if (Test-Path $exe) {
    $binaryPath = "`"$exe`""
}
elseif (Test-Path $dll) {
    # Published with UseAppHost=false: run through the shared dotnet host.
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $binaryPath = "`"$dotnet`" `"$dll`""
}
else {
    throw "No published worker found in $InstallPath. Publish with: dotnet publish src\Wings.NotificationHub.Worker -c Release -r win-x64 --self-contained false -o <dir>, then pass -SourcePath <dir>."
}

# -- service --
if ($existing) {
    Write-Host "Updating existing service $ServiceName."
    Invoke-Sc @('config', $ServiceName, 'binPath=', $binaryPath, 'DisplayName=', $DisplayName)
}
else {
    $params = @{ Name = $ServiceName; BinaryPathName = $binaryPath; DisplayName = $DisplayName; Description = $Description; StartupType = 'Automatic' }
    if ($Credential) { $params.Credential = $Credential }
    New-Service @params | Out-Null
    Write-Host "Created service $ServiceName."
}
Invoke-Sc @('description', $ServiceName, $Description)
Invoke-Sc @('config', $ServiceName, 'start=', 'delayed-auto')
# Recovery: restart on the 1st, 2nd and subsequent failures; reset the failure count after 24 h.
Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
# Also treat "stopped with a non-zero exit code" (e.g. unhandled startup exception) as a failure.
Invoke-Sc @('failureflag', $ServiceName, '1')

# -- account --
if (-not $Credential) {
    $serviceAccount = if ($Account) { $Account } else { "NT SERVICE\$ServiceName" }
    # Virtual accounts, gMSAs and built-in accounts take no password.
    Invoke-Sc @('config', $ServiceName, 'obj=', $serviceAccount)
}
else {
    $serviceAccount = $Credential.UserName
}
Write-Host "Service account: $serviceAccount"

# -- environment (settings and secrets) --
$envLines = [System.Collections.Generic.List[string]]::new()
$envLines.Add("DOTNET_ENVIRONMENT=$Environment")
$envLines.Add("Hub__DataDirectory=$DataDirectory")
if ($EnvironmentFile) {
    foreach ($line in Get-Content -Path $EnvironmentFile) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        if ($t -notmatch '^[A-Za-z_][A-Za-z0-9_]*=') { throw "Invalid line in ${EnvironmentFile}: expected KEY=VALUE, got '$t'" }
        $envLines.Add($t)
    }
}
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $regPath -Name 'Environment' -PropertyType MultiString -Value $envLines.ToArray() -Force | Out-Null
# Only administrators and SYSTEM may read the service key (it now holds secrets).
$acl = Get-Acl -Path $regPath
$acl.SetAccessRuleProtection($true, $false)
foreach ($id in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
    $acl.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new($id, 'FullControl', 'ContainerInherit', 'None', 'Allow'))
}
Set-Acl -Path $regPath -AclObject $acl
Write-Host "Stored $($envLines.Count) environment variable(s) for the service (values not shown)."

# -- data directory --
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
& icacls.exe $DataDirectory /grant "${serviceAccount}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not grant $serviceAccount Modify on $DataDirectory; grant it manually." }

if ($Start) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Host "$ServiceName is running. Logs: Windows Event Log (Application) and console output; configure Serilog sinks for files."
}
else {
    Write-Host "Installed. Start with: Start-Service $ServiceName"
}
