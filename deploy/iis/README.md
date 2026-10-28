# Hosting the API on IIS

The API (REST, SignalR hubs, admin UI) runs in-process in IIS through the ASP.NET Core Module V2. Background
processing runs either inside the API (`Workers__RunInApi=true`, the default) or in the separate worker Windows
service (`deploy/windows-service/install-worker.ps1`, then set `Workers__RunInApi=false` on the site). For more than
one server, prefer the separate worker.

> These steps are written for Windows Server 2019/2022/2025 with IIS 10. They were **not executed** in the
> environment where they were written (macOS, no IIS). The `web.config` is the standard ANCM v2 schema.

## 1. Prerequisites (once per server, elevated PowerShell)

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-AppInit, Web-Http-Redirect, Web-Mgmt-Console
# ASP.NET Core 10.0 Hosting Bundle (runtime + ASP.NET Core Module V2). Download it from https://dotnet.microsoft.com/download/dotnet/10.0
.\dotnet-hosting-10.0.x-win.exe /quiet /norestart
net stop was /y; net start w3svc        # IIS picks up the new module
```

- **WebSocket Protocol** (`Web-WebSockets`) is required by SignalR. Without it, clients fall back to
  SSE/long polling, which is slower and uses more connections.
- **Application Initialization** (`Web-AppInit`) is used with *AlwaysRunning* and *preload* so the app starts
  without waiting for a request. Background workers, the realtime relay and scheduled sends only run while the
  process is up.

## 2. Publish and copy

```powershell
dotnet publish src\Wings.NotificationHub.Api -c Release -o C:\inetpub\WingsNotificationHub
# The admin UI is served from wwwroot: build web/ first (npm ci && npm run build writes into src/Wings.NotificationHub.Api/wwwroot)
Copy-Item deploy\iis\web.config C:\inetpub\WingsNotificationHub\web.config -Force
New-Item -ItemType Directory -Force C:\ProgramData\WingsNotificationHub, C:\inetpub\WingsNotificationHub\logs | Out-Null
```

## 3. Application pool and site

```powershell
Import-Module WebAdministration
$pool = 'WingsNotificationHub'
New-WebAppPool -Name $pool
Set-ItemProperty IIS:\AppPools\$pool -Name managedRuntimeVersion -Value ''          # "No Managed Code"
Set-ItemProperty IIS:\AppPools\$pool -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\$pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)   # never idle out
Set-ItemProperty IIS:\AppPools\$pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\AppPools\$pool -Name processModel.identityType -Value ApplicationPoolIdentity
# In-process hosting allows one app per pool, so do not share this pool.

New-Website -Name 'WingsNotificationHub' -PhysicalPath C:\inetpub\WingsNotificationHub -ApplicationPool $pool -Port 443 -Ssl -HostHeader notifications.example.com
Set-ItemProperty 'IIS:\Sites\WingsNotificationHub' -Name applicationDefaults.preloadEnabled -Value $true
# Bind the TLS certificate (thumbprint of a cert in LocalMachine\My):
(Get-WebBinding -Name 'WingsNotificationHub' -Protocol https).AddSslCertificate('<thumbprint>', 'My')

# Data directory (attachments, generated files): the pool identity needs Modify.
icacls C:\ProgramData\WingsNotificationHub /grant "IIS AppPool\${pool}:(OI)(CI)M"
icacls C:\inetpub\WingsNotificationHub\logs  /grant "IIS AppPool\${pool}:(OI)(CI)M"
```

If SQL Server uses Windows authentication, run the pool as a gMSA or domain account instead of
ApplicationPoolIdentity, or grant `IIS AppPool\WingsNotificationHub` (the machine account) a login.

## 4. Configuration through environment variables

Secrets go into the **application pool's environment** in `applicationHost.config`, which is outside the site
folder and survives redeployments. They do not go into `web.config` or `appsettings.json`.

```powershell
function Set-PoolEnv($name, $value) {
  & "$env:windir\system32\inetsrv\appcmd.exe" set config -section:system.applicationHost/applicationPools `
    "/-[name='WingsNotificationHub'].environmentVariables.[name='$name']" /commit:apphost 2>$null | Out-Null
  & "$env:windir\system32\inetsrv\appcmd.exe" set config -section:system.applicationHost/applicationPools `
    "/+[name='WingsNotificationHub'].environmentVariables.[name='$name',value='$value']" /commit:apphost | Out-Null
}
$key = { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }

Set-PoolEnv 'Database__Provider' 'sqlserver'
Set-PoolEnv 'ConnectionStrings__NotificationHub' 'Server=sql01;Database=WingsNotificationHub;Integrated Security=True;Encrypt=True;TrustServerCertificate=False'
Set-PoolEnv 'Security__EncryptionKeys__k1' (& $key)      # generate ONCE and store it in your vault: losing it loses stored secrets
Set-PoolEnv 'Auth__SigningKey' (& $key)
Set-PoolEnv 'Hub__PublicBaseUrl' 'https://notifications.example.com'
Set-PoolEnv 'Hub__MessageTransport' 'database'            # or rabbitmq + Messaging__RabbitMq__Uri
Set-PoolEnv 'Redis__Connection' 'redis01:6379,password=…,ssl=True,abortConnect=false'   # needed for SignalR with >1 server
Set-PoolEnv 'Workers__RunInApi' 'false'                   # when the worker Windows service is installed
Set-PoolEnv 'Cors__Origins__0' 'https://erp.example.com'  # apps embedding the inbox
Set-PoolEnv 'Bootstrap__AdminEmail' 'admin@example.com'
Set-PoolEnv 'Bootstrap__AdminPassword' '<strong password, first start only>'
Restart-WebAppPool WingsNotificationHub
```

The keys must be identical on every API server and on the worker. Generate them once.

## 5. Verify

```powershell
Invoke-RestMethod https://notifications.example.com/health/ready      # {"status":"Ready"}
Invoke-RestMethod https://notifications.example.com/openapi/v1.json | Select-Object -ExpandProperty info
```

In the browser dev tools, the admin console's SignalR connection to `/hubs/admin` should show `101 Switching Protocols`
(WebSockets).

## Troubleshooting

- **500.30 / 500.31**: the app failed to start or the runtime is missing. Set `stdoutLogEnabled="true"` temporarily,
  recycle the pool, and read `logs\stdout_*.log`. Typical causes are a missing `Security__EncryptionKeys__k1` or
  `Auth__SigningKey` (both are required outside Development) and an unreachable database.
- **SignalR falls back to long polling**: the WebSocket Protocol feature is not installed, or a proxy in front of IIS
  strips the `Upgrade` header.
- **Scheduled notifications are late after quiet periods**: the pool idled out. Check `startMode=AlwaysRunning`,
  `idleTimeout=0` and `preloadEnabled=true`, or run the worker as a Windows service.
- **Behind a load balancer**: configure `ReverseProxy__KnownProxies__0=<LB address>` so `X-Forwarded-*` is trusted.
  Enable sticky sessions, or use Redis plus WebSockets only, for SignalR.
