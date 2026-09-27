$ErrorActionPreference = 'Stop'
$containerName = 'roombooking-sql'
$project = Join-Path $PSScriptRoot '..\src\Server\RoomBooking.Server.csproj'
$project = (Resolve-Path $project).Path
$image = 'mcr.microsoft.com/mssql/server:2022-latest'

function Invoke-Docker([string[]]$DockerArgs) {
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: docker $($DockerArgs[0])" }
}

$exists = $false
$PSNativeCommandUseErrorActionPreference = $false
$existingContainers = & docker ps -a --format '{{.Names}}'
if ($LASTEXITCODE -ne 0) { throw 'Could not list Docker containers.' }
$exists = $existingContainers -contains $containerName
if ($exists) {
    $running = (& docker inspect --format '{{.State.Running}}' $containerName).Trim()
    if ($running -ne 'true') { Invoke-Docker @('start', $containerName) | Out-Null }
    $environment = & docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' $containerName
    $passwordLine = $environment | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1
    $sqlPassword = if ($passwordLine) { $passwordLine.Substring('MSSQL_SA_PASSWORD='.Length) } else { $null }
    if (-not $sqlPassword) { throw "Container '$containerName' exists but its SQL password could not be recovered. Keep it and configure the connection string manually." }
} else {
    $published = & docker ps --filter 'publish=1433' --format '{{.Names}}'
    if ($published) { throw "Port 1433 is already used by container(s): $($published -join ', '). Stop or reconfigure them before seeding." }
    $randomBytes = New-Object byte[] 24
    $randomGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $randomGenerator.GetBytes($randomBytes)
    $randomGenerator.Dispose()
    $sqlPassword = ([BitConverter]::ToString($randomBytes) -replace '-', '') + 'Aa1!'
    Invoke-Docker @('run', '--detach', '--name', $containerName, '--restart', 'unless-stopped', '--publish', '1433:1433', '--env', 'ACCEPT_EULA=Y', '--env', 'MSSQL_PID=Developer', '--env', "MSSQL_SA_PASSWORD=$sqlPassword", '--mount', 'type=volume,source=roombooking-sql-data,target=/var/opt/mssql', $image) | Out-Null
}

$connectionString = "Server=localhost,1433;Database=RoomBooking;User Id=sa;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
& dotnet user-secrets set 'ConnectionStrings:DefaultConnection' $connectionString --project $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not save the SQL connection string to .NET User Secrets.' }
& dotnet user-secrets set 'Seed:DemoData' 'true' --project $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable the demo-data seed.' }

$ready = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $sqlPassword -Q 'SELECT 1' -b *> $null
    $sqlExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($sqlExitCode -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw 'SQL Server did not become ready within 120 seconds.' }

Write-Host 'SQL Server is ready. Starting the app; Seed:DemoData imports users and rooms from wwwroot/data/directory.json.'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project $project --no-launch-profile --urls 'http://127.0.0.1:5155'
