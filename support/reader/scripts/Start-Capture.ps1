[CmdletBinding()]
param([switch]$CheckOnly,[switch]$NexusOnly)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
function Find-Python {
    foreach ($candidate in @('py.exe','python.exe',"$env:LOCALAPPDATA\Programs\Python\Python313\python.exe")) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if (!$cmd -or $cmd.Source -like '*\Microsoft\WindowsApps\python.exe') { continue }
        $flags = @()
        if ($cmd.Name -eq 'py.exe') { $flags = @('-3') }
        $path = & $cmd.Source @flags -c 'import sys; print(sys.executable) if sys.version_info >= (3,10) else sys.exit(1)' 2>$null
        if ($LASTEXITCODE -eq 0 -and $path -and (Test-Path -LiteralPath $path)) { return $path }
    }
}
function Find-Runtime {
    foreach ($path in @("$env:ProgramFiles\dotnet\dotnet.exe",(Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source)) {
        if ($path -and (Test-Path -LiteralPath $path) -and ((& $path --list-runtimes) -match '^Microsoft\.NETCore\.App 10\.0\.\d+ ')) { return $path }
    }
}
function Install-Dependency($label,$id,$source) {
    if ($CheckOnly) { throw "$label is missing." }
    $answer = Read-Host "$label is required. Download and install it using Windows Package Manager? [y/N]"
    if ($answer -notin @('y','yes')) { throw "Setup stopped. $label is required." }
    if (!(Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw 'Install App Installer from Microsoft Store, then rerun Capture.cmd.' }
    & winget.exe install --exact --id $id --source $source --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "$label installation did not complete (exit $LASTEXITCODE)." }
}
function Find-Nexus {
    Get-StartApps | Where-Object AppID -eq 'GuangzhouChickenRunNetwor.16460D2A87234_0zmrycy25r4k8!App' | Select-Object -First 1
}
try {
    Write-Host 'GameSir Controller Calibration Capture' -ForegroundColor Cyan
    Write-Host 'Made by u/peoplehurt6666 - Reddit' -ForegroundColor Magenta
    $python = Find-Python
    if (!$python) { Install-Dependency 'Python 3' 'Python.Python.3.13' 'winget'; $python = Find-Python }
    $runtime = Find-Runtime
    if (!$runtime) { Install-Dependency '.NET 10 Runtime' 'Microsoft.DotNet.Runtime.10' 'winget'; $runtime = Find-Runtime }
    if (!$python -or !$runtime) { throw 'Dependencies have not registered yet. Reopen Capture.cmd.' }
    if ($NexusOnly) {
        $nexus = Find-Nexus
        if (!$nexus) { Install-Dependency 'GameSir Nexus' '9PMJKF5NSTDR' 'msstore'; $nexus = Find-Nexus }
        if (!$nexus) { throw 'GameSir Nexus has not registered yet.' }
        Start-Process explorer.exe -ArgumentList ('shell:AppsFolder\' + $nexus.AppID)
        exit 0
    }
    Write-Host 'Ready: Python and .NET 10 Runtime. Controller detection selects the reader.' -ForegroundColor Green
    if ($CheckOnly) { exit 0 }
    $env:PATH = (Split-Path $runtime) + ';' + $env:PATH
    & $python -u -B scripts\release_console.py
    exit $LASTEXITCODE
} catch { Write-Host $_.Exception.Message -ForegroundColor Yellow; exit 1 }
