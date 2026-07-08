# CoreRemote - Git-Based Update Checker (Check Only)
$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

try {
    # 1. Fetch latest changes from GitHub
    git fetch origin main 2>&1 | Out-Null

    # 2. Get commit hashes
    $localHash = (git rev-parse HEAD).Trim()
    $remoteHash = (git rev-parse origin/main).Trim()

    if ($localHash -eq $remoteHash) {
        Write-Output "ALREADY_UP_TO_DATE"
    } else {
        Write-Output "UPDATE_AVAILABLE"
    }
}
catch {
    Write-Output "ERROR: $($_.Exception.Message)"
}
