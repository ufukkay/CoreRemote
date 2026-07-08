# CoreRemote - Git-Based Update Checker (Check Only)
$ErrorActionPreference = "Continue" # Do not treat console stderr as script termination

Set-Location $PSScriptRoot

try {
    # 1. Fetch latest changes from GitHub (redirect stderr to null to prevent PowerShell parsing it as an error)
    git fetch origin main 2>$null

    # 2. Get commit hashes
    $localHash = (git rev-parse HEAD 2>$null).Trim()
    $remoteHash = (git rev-parse origin/main 2>$null).Trim()

    if ($localHash -eq $remoteHash) {
        Write-Output "ALREADY_UP_TO_DATE"
    } else {
        Write-Output "UPDATE_AVAILABLE"
    }
}
catch {
    Write-Output "ERROR: $($_.Exception.Message)"
}
