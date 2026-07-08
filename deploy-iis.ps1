# CoreRemote - IIS Deployment & PM2 Setup Script
# Bu betik, dosyaları IIS dizinine kopyalar, bağımlılıkları kurar ve PM2 servislerini başlatır.

$ErrorActionPreference = "Stop"

$DestPath = "C:\inetpub\wwwroot\CoreRemote"

Write-Host "==============================================" -ForegroundColor Green
Write-Host "     CoreRemote IIS Dağıtımı Başlatılıyor     " -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green

# 1. Klasör Kontrolü ve Oluşturulması
if (-not (Test-Path $DestPath)) {
    New-Item -ItemType Directory -Path $DestPath -Force | Out-Null
    Write-Host ">> Hedef dizin oluşturuldu: $DestPath" -ForegroundColor Green
}

# 2. Dosyaların Kopyalanması
Write-Host ">> Dosyalar kopyalanıyor..." -ForegroundColor Cyan
$SrcPath = $PSScriptRoot

# Kopyalanacak öğeler
$ItemsToCopy = @("CoreRemote.Server", "coreremote-console", "CoreRemote.Agent", "web.config")

foreach ($item in $ItemsToCopy) {
    $srcItem = Join-Path $SrcPath $item
    $destItem = Join-Path $DestPath $item
    if (Test-Path $srcItem) {
        Copy-Item -Path $srcItem -Destination $DestPath -Recurse -Force
        Write-Host "   Kopyalandı: $item" -ForegroundColor Gray
    }
}

# 3. Server Bağımlılıkları
Write-Host ">> Sinyalizasyon Sunucusu (Backend) bağımlılıkları kuruluyor..." -ForegroundColor Cyan
Set-Location "$DestPath\CoreRemote.Server"
npm install --no-audit

# 4. Console Bağımlılıkları ve Derleme
Write-Host ">> Operatör Paneli (Frontend) bağımlılıkları kuruluyor..." -ForegroundColor Cyan
Set-Location "$DestPath\coreremote-console"
npm install --no-audit

Write-Host ">> Operatör Paneli derleniyor (Next.js Production Build)..." -ForegroundColor Cyan
npm run build

# 5. PM2 Servislerinin Başlatılması
Write-Host ">> PM2 Servisleri Yapılandırılıyor..." -ForegroundColor Cyan

# Eski PM2 süreçlerini temizle
Invoke-Expression "pm2 delete coreremote-server" -ErrorAction SilentlyContinue | Out-Null
Invoke-Expression "pm2 delete coreremote-console" -ErrorAction SilentlyContinue | Out-Null

# Backend Başlat
Set-Location "$DestPath\CoreRemote.Server"
pm2 start server.js --name "coreremote-server"

# Frontend Başlat (Windows üzerinde en stabil Next.js PM2 komutu)
Set-Location "$DestPath\coreremote-console"
pm2 start start-console.js --name "coreremote-console"

# PM2 listesini kaydet
pm2 save

Write-Host "==============================================" -ForegroundColor Green
Write-Host "  Dağıtım ve PM2 Servis Kurulumu Başarılı!     " -ForegroundColor Green
Write-Host "  Arka Plan Portları: Backend: 5000 | Console: 3000" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
