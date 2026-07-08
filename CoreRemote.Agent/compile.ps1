$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) {
    Write-Error "C# Compiler (csc.exe) not found at $csc!"
    exit 1
}

Write-Host "Compiling CoreRemote Agent..." -ForegroundColor Cyan

# Compile Agent.cs as winexe (Windows Forms application) to run fully in the background
& $csc /target:winexe /out:CoreRemoteAgent.exe /reference:System.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.dll,System.Core.dll Agent.cs

if ($LASTEXITCODE -eq 0) {
    Write-Host "Successfully compiled CoreRemoteAgent.exe!" -ForegroundColor Green
} else {
    Write-Error "Compilation failed!"
}
