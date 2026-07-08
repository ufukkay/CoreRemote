# CoreRemote Windows Server 2025 Otomatik Kurulum Betiği
# Bu betiği Windows Server 2025 sunucunuzda Yönetici (Admin) olarak çalıştırın.

$ErrorActionPreference = "Stop"

Write-Host "==============================================" -ForegroundColor Green
Write-Host "  CoreRemote Windows Server 2025 Kurulumu     " -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green

# ── 1. Yönetici Yetkisi Kontrolü ──
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Lütfen bu betiği yönetici olarak çalıştırın!"
    exit 1
}

# ── 2. Node.js Kontrolü ve Kurulumu ──
Write-Host ">> Node.js sürümü kontrol ediliyor..." -ForegroundColor Cyan
$nodeInstalled = $false
try {
    $nodeVer = node -v
    Write-Host "Node.js zaten kurulu: $nodeVer" -ForegroundColor Green
    $nodeInstalled = $true
} catch {
    Write-Host "Node.js bulunamadı. Kurulum başlatılıyor..." -ForegroundColor Yellow
}

if (!$nodeInstalled) {
    Write-Host ">> Node.js LTS sürümü indiriliyor..." -ForegroundColor Cyan
    $msiUrl = "https://nodejs.org/dist/v20.11.1/node-v20.11.1-x64.msi"
    $msiPath = "$env:TEMP\node-install.msi"
    
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath
    
    Write-Host ">> Sessiz kurulum yapılıyor..." -ForegroundColor Cyan
    Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$msiPath`" /qn /norestart" -Wait
    
    # Path değişkenini güncelle
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
    
    try {
        $nodeVer = node -v
        Write-Host "Node.js başarıyla kuruldu: $nodeVer" -ForegroundColor Green
    } catch {
        Write-Error "Node.js kuruldu ancak PATH değişkeni güncellenemedi. Lütfen sunucuyu yeniden başlatıp betiği tekrar çalıştırın."
        exit 1
    }
}

# ── 3. Kurulum Dizinlerinin Hazırlanması ──
$destDir = "C:\CoreRemote"
Write-Host ">> Kurulum dizini hazırlanıyor: $destDir" -ForegroundColor Cyan
if (!(Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

$serverDest = "$destDir\CoreRemote.Server"
$consoleDest = "$destDir\coreremote-console"

# Dosyaları kopyala
Write-Host ">> Proje dosyaları kopyalanıyor..." -ForegroundColor Cyan
if (Test-Path "$PSScriptRoot\CoreRemote.Server") {
    Copy-Item -Path "$PSScriptRoot\CoreRemote.Server" -Destination $destDir -Recurse -Force
}
if (Test-Path "$PSScriptRoot\coreremote-console") {
    Copy-Item -Path "$PSScriptRoot\coreremote-console" -Destination $destDir -Recurse -Force
}

# ── 4. Sunucu (Server) Bağımlılıklarının Kurulması ──
Write-Host ">> Sinyalizasyon Sunucusu bağımlılıkları kuruluyor..." -ForegroundColor Cyan
cd $serverDest
npm install --no-audit

# ── 5. Operatör Konsolu (Next.js) Bağımlılıkları ve Derleme ──
Write-Host ">> Operatör Paneli bağımlılıkları kuruluyor..." -ForegroundColor Cyan
cd $consoleDest
npm install --no-audit

Write-Host ">> Operatör Paneli derleniyor (Production Build)..." -ForegroundColor Cyan
npm run build

# ── 6. PM2 Kurulumu ve Servis Başlatma ──
Write-Host ">> PM2 (Process Manager) kuruluyor..." -ForegroundColor Cyan
npm install -g pm2 --no-audit

Write-Host ">> Uygulamalar PM2 ile başlatılıyor..." -ForegroundColor Cyan
cd $serverDest
pm2 start server.js --name "coreremote-server"

cd $consoleDest
pm2 start npm --name "coreremote-console" -- start

# PM2 durumunu kaydet
pm2 save

# ── 7. Windows Defender Güvenlik Duvarı Ayarları ──
Write-Host ">> Windows Güvenlik Duvarı kuralları açılıyor..." -ForegroundColor Cyan

# Port 5000 (Backend API & WebSocket)
New-NetFirewallRule -DisplayName "CoreRemote Server (Port 5000)" -Direction Inbound -LocalPort 5000 -Protocol TCP -Action Allow -ErrorAction SilentlyContinue

# Port 3000 (Frontend Console)
New-NetFirewallRule -DisplayName "CoreRemote Console (Port 3000)" -Direction Inbound -LocalPort 3000 -Protocol TCP -Action Allow -ErrorAction SilentlyContinue

Write-Host "==============================================" -ForegroundColor Green
Write-Host "  Kurulum Başarıyla Tamamlandı!               " -ForegroundColor Green
Write-Host "  Operatör Paneli: http://sunucu_ip:3000      " -ForegroundColor Green
Write-Host "  Sinyalizasyon Sunucusu: http://sunucu_ip:5000" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
