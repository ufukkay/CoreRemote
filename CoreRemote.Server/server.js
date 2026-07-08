const express = require("express");
const http = require("http");
const cors = require("cors");
const { Server } = require("socket.io");
const { WebSocketServer } = require("ws");
const url = require("url");
const sqlite3 = require("sqlite3").verbose();
const path = require("path");
const fs = require("fs");
const { exec, spawn } = require("child_process");

const PORT = process.env.PORT || 5000;
const SERVER_VERSION = "1.0.0";
const app = express();
app.use(cors());

// PNG -> ICO dönüştürücü (C# Ajan İkonu için)
const logoPngPath = path.join(__dirname, "agent_logo.png");
const logoIcoPath = path.join(__dirname, "agent_logo.ico");

function generateIcoFromPng() {
  try {
    if (fs.existsSync(logoPngPath)) {
      const pngBuffer = fs.readFileSync(logoPngPath);
      const icoHeader = Buffer.alloc(22);
      icoHeader.writeUInt16LE(0, 0);     // Reserved
      icoHeader.writeUInt16LE(1, 2);     // Type (1 = ICO)
      icoHeader.writeUInt16LE(1, 4);     // Number of images (1)
      icoHeader.writeUInt8(0, 6);        // Width (0 = 256)
      icoHeader.writeUInt8(0, 7);        // Height (0 = 256)
      icoHeader.writeUInt8(0, 8);        // Color count (0 = 256+)
      icoHeader.writeUInt8(0, 9);        // Reserved
      icoHeader.writeUInt16LE(1, 10);    // Color planes (1)
      icoHeader.writeUInt16LE(32, 12);   // Bits per pixel (32)
      icoHeader.writeUInt32LE(pngBuffer.length, 14); // PNG size
      icoHeader.writeUInt32LE(22, 18);   // Offset to PNG data (22)
      
      const icoBuffer = Buffer.concat([icoHeader, pngBuffer]);
      fs.writeFileSync(logoIcoPath, icoBuffer);
      console.log("[SERVER] agent_logo.png başarıyla agent_logo.ico formatına dönüştürüldü.");
    } else {
      console.log("[SERVER] agent_logo.png bulunamadı, varsayılan simge kullanılacak.");
    }
  } catch (err) {
    console.error("[SERVER] İkon dönüştürme hatası:", err.message);
  }
}
generateIcoFromPng();
app.use(express.json());
app.use('/downloads', express.static(path.join(__dirname, 'downloads')));

const server = http.createServer(app);

// Database Setup
const dbPath = path.join(__dirname, "coreremote.db");
const db = new sqlite3.Database(dbPath, (err) => {
  if (err) {
    console.error("Database connection error:", err.message);
  } else {
    console.log("Connected to SQLite database.");
    initializeDatabase();
  }
});

function initializeDatabase() {
  db.run(`
    CREATE TABLE IF NOT EXISTS devices (
      id TEXT PRIMARY KEY,
      hostname TEXT,
      username TEXT,
      os_version TEXT,
      ram TEXT,
      cpu TEXT,
      ip_address TEXT,
      status TEXT DEFAULT 'offline',
      last_seen INTEGER,
      agent_version TEXT,
      logo_customized TEXT DEFAULT 'false'
    )
  `);

  db.run(`
    CREATE TABLE IF NOT EXISTS audit_logs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      device_id TEXT,
      timestamp INTEGER,
      action TEXT,
      details TEXT
    )
  `);
}

// Socket.IO for Web Dashboard
const io = new Server(server, {
  cors: {
    origin: "*",
    methods: ["GET", "POST"]
  }
});

// Raw WebSockets for C# Agents
const wss = new WebSocketServer({ noServer: true });
const operatorWss = new WebSocketServer({ noServer: true });

// Active Agent connections map: deviceId -> ws connection
const activeAgents = new Map();
// Active Operator connections map: deviceId -> ws connection
const activeOperators = new Map();

// Upgrade HTTP server to WebSockets for /agent-socket and /operator-socket
server.on("upgrade", (request, socket, head) => {
  const pathname = url.parse(request.url).pathname;

  if (pathname === "/agent-socket") {
    wss.handleUpgrade(request, socket, head, (ws) => {
      wss.emit("connection", ws, request);
    });
  } else if (pathname === "/operator-socket") {
    operatorWss.handleUpgrade(request, socket, head, (ws) => {
      operatorWss.emit("connection", ws, request);
    });
  } else {
    socket.destroy();
  }
});

// Helper: Update Agent Status in DB
function updateDeviceStatus(deviceId, status, telemetry = null) {
  const now = Math.floor(Date.now() / 1000);
  if (telemetry) {
    const query = `
      INSERT INTO devices (id, hostname, username, os_version, ram, cpu, ip_address, status, last_seen, agent_version)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        hostname=excluded.hostname,
        username=excluded.username,
        os_version=excluded.os_version,
        ram=excluded.ram,
        cpu=excluded.cpu,
        ip_address=excluded.ip_address,
        status=excluded.status,
        last_seen=excluded.last_seen,
        agent_version=excluded.agent_version
    `;
    db.run(query, [
      deviceId,
      telemetry.hostname || "-",
      telemetry.username || "-",
      telemetry.osVersion || "-",
      telemetry.ram || "-",
      telemetry.cpu || "-",
      telemetry.ipAddress || "-",
      status,
      now,
      telemetry.version || "1.0.0"
    ], (err) => {
      if (err) console.error("Error saving telemetry:", err.message);
    });
  } else {
    db.run(
      "UPDATE devices SET status = ?, last_seen = ? WHERE id = ?",
      [status, now, deviceId],
      (err) => {
        if (err) console.error("Error updating status:", err.message);
      }
    );
  }

  // Broadcast to all dashboard clients
  io.emit("device_status_change", { deviceId, status, timestamp: now });
}

// ── Agent (WebSocket) Connection Handler ──────────────────────────────
wss.on("connection", (ws, req) => {
  const parameters = url.parse(req.url, true).query;
  const deviceId = parameters.deviceId;

  if (!deviceId) {
    ws.close(1008, "Device ID Required");
    return;
  }

  console.log(`[AGENT +] Connected: ${deviceId}`);
  activeAgents.set(deviceId, ws);
  ws.deviceId = deviceId;

  updateDeviceStatus(deviceId, "online");

  ws.on("message", (message, isBinary) => {
    if (isBinary || Buffer.isBuffer(message)) {
      const typeByte = message[0];
      if (typeByte === 0x01) {
        // Canlı ekran yayını
        io.to(`device:${deviceId}`).emit("agent_frame", message);
        const opWs = activeOperators.get(deviceId);
        if (opWs && opWs.readyState === wsReadyStateOpen()) {
          opWs.send(message);
        }
      } else if (typeByte === 0x02) {
        // Canlı ses yayını (sadece teknisyen uygulamasına)
        const opWs = activeOperators.get(deviceId);
        if (opWs && opWs.readyState === wsReadyStateOpen()) {
          opWs.send(message);
        }
      }
      return;
    }

    try {
      const payload = JSON.parse(message.toString());
      const { type, data } = payload;

      switch (type) {
        case "telemetry":
          updateDeviceStatus(deviceId, "online", data);
          io.to(`device:${deviceId}`).emit("agent_telemetry", { deviceId, telemetry: data });
          
          // Teknisyen uygulamasına da telemetri paketini yönlendir (monitör listesi için)
          const opWs = activeOperators.get(deviceId);
          if (opWs && opWs.readyState === wsReadyStateOpen()) {
            opWs.send(message);
          }
          break;

        case "webrtc_signal":
          // Forward WebRTC signals from Agent to Operator Console
          io.to(`device:${deviceId}`).emit("signal_from_agent", data);
          break;

        case "update_status":
          console.log(`[AGENT UPDATE] ${deviceId}: ${data.status} - ${data.message || ""}`);
          io.emit("agent_update_progress", { deviceId, status: data.status, message: data.message });
          break;

        default:
          console.log(`[AGENT MSG] Unknown message type: ${type}`);
      }
    } catch (err) {
      if (!Buffer.isBuffer(message)) {
        console.error(`[WS MSG ERR] Device: ${deviceId}`, err.message);
      }
    }
  });

  ws.on("close", () => {
    console.log(`[AGENT -] Disconnected: ${deviceId}`);
    activeAgents.delete(deviceId);
    updateDeviceStatus(deviceId, "offline");
  });

  ws.on("error", (err) => {
    console.error(`[WS ERR] Device: ${deviceId}`, err.message);
  });
});

// ── Teknisyen (WebSocket) Connection Handler ──────────────────────────
operatorWss.on("connection", (ws, req) => {
  const parameters = url.parse(req.url, true).query;
  const deviceId = parameters.deviceId;

  if (!deviceId) {
    ws.close(1008, "Device ID Required");
    return;
  }

  console.log(`[OPERATOR +] Teknisyen bağlandı, hedef cihaz: ${deviceId}`);
  activeOperators.set(deviceId, ws);

  // Ajan eğer aktifse ona ekran yayınını başlatma komutunu gönder ve güncel telemetri iste
  const agentWs = activeAgents.get(deviceId);
  if (agentWs && agentWs.readyState === wsReadyStateOpen()) {
    agentWs.send(JSON.stringify({
      type: "webrtc_signal",
      data: { action: "start_stream", quality: 60, interval: 100 }
    }));
    agentWs.send(JSON.stringify({ type: "request_telemetry" }));
  }

  ws.on("message", (message) => {
    // Teknisyenden gelen girdi aksiyonlarını ajana ilet
    const aWs = activeAgents.get(deviceId);
    if (aWs && aWs.readyState === wsReadyStateOpen()) {
      try {
        aWs.send(JSON.stringify({ type: "webrtc_signal", data: JSON.parse(message.toString()) }));
      } catch (err) {
        console.error("[OPERATOR MSG ERR] Geçersiz girdi verisi:", err.message);
      }
    }
  });

  ws.on("close", () => {
    console.log(`[OPERATOR -] Teknisyen ayrıldı, hedef cihaz: ${deviceId}`);
    activeOperators.delete(deviceId);

    // Eğer izleyen başka teknisyen yoksa yayını durdur komutu yollanabilir
    const aWs = activeAgents.get(deviceId);
    if (aWs && aWs.readyState === wsReadyStateOpen()) {
      aWs.send(JSON.stringify({
        type: "webrtc_signal",
        data: { action: "stop_stream" }
      }));
    }
  });

  ws.on("error", (err) => {
    console.error(`[OPERATOR ERR] Cihaz: ${deviceId}`, err.message);
  });
});

// ── Dashboard (Socket.IO) Handler ───────────────────────────────────
io.on("connection", (socket) => {
  console.log(`[DASHBOARD +] Operator connected: ${socket.id}`);

  // Operator watches a specific device's room
  socket.on("watch_device", (deviceId) => {
    socket.join(`device:${deviceId}`);
    console.log(`[DASHBOARD] Operator watching device: ${deviceId}`);

    // If agent is online, ask it to send fresh telemetry
    const agentWs = activeAgents.get(deviceId);
    if (agentWs && agentWs.readyState === wsReadyStateOpen()) {
      agentWs.send(JSON.stringify({ type: "request_telemetry" }));
    }
  });

  socket.on("unwatch_device", (deviceId) => {
    socket.leave(`device:${deviceId}`);
    console.log(`[DASHBOARD] Operator stopped watching: ${deviceId}`);
  });

  // WebRTC Signal from Operator to Agent
  socket.on("signal_to_agent", ({ deviceId, signal }) => {
    const agentWs = activeAgents.get(deviceId);
    if (agentWs && agentWs.readyState === wsReadyStateOpen()) {
      agentWs.send(JSON.stringify({ type: "webrtc_signal", data: signal }));
    } else {
      socket.emit("error_message", { message: "Agent is offline" });
    }
  });

  // Remote Update trigger
  socket.on("trigger_update", ({ deviceId, updateUrl }) => {
    const agentWs = activeAgents.get(deviceId);
    if (agentWs && agentWs.readyState === wsReadyStateOpen()) {
      agentWs.send(JSON.stringify({ type: "trigger_update", data: { url: updateUrl } }));
      console.log(`[UPDATE TRIGGERED] Sent update request to ${deviceId} with URL: ${updateUrl}`);
    } else {
      socket.emit("error_message", { message: "Agent offline, cannot trigger update." });
    }
  });

  socket.on("disconnect", () => {
    console.log(`[DASHBOARD -] Operator disconnected: ${socket.id}`);
  });
});

// Helper for WS ready state
function wsReadyStateOpen() {
  return 1; // WebSocket.OPEN
}

// ── HTTP API Uç Noktaları (REST API) ──────────────────────────────────

// Cihaz listesini getir
app.get("/api/devices", (req, res) => {
  db.all("SELECT * FROM devices ORDER BY last_seen DESC", [], (err, rows) => {
    if (err) {
      return res.status(500).json({ error: err.message });
    }
    // Ekleme yapalım: Canlı soket bağlantısı var mı kontrolü
    const devices = rows.map(r => ({
      ...r,
      wsConnected: activeAgents.has(r.id)
    }));
    res.json(devices);
  });
});

// Güncelleme sorgulama API'si (GitHub Releases entegrasyonlu)
app.get("/api/update/check", async (req, res) => {
  try {
    const response = await fetch("https://api.github.com/repos/ufukkay/CoreRemote/releases/latest", {
      headers: { "User-Agent": "CoreRemote-Server" }
    });
    if (!response.ok) {
      throw new Error(`GitHub API returned status ${response.status}`);
    }
    const data = await response.json();
    const latestVersion = data.tag_name ? data.tag_name.replace(/^v/, "") : "1.0.0";
    
    // CoreRemoteAgent.exe asset'ini bul
    const asset = data.assets.find(a => a.name === "CoreRemoteAgent.exe");
    const downloadUrl = asset ? asset.browser_download_url : "";

    res.json({
      latestVersion,
      url: downloadUrl,
      releaseName: data.name || data.tag_name || "Sürüm Bilgisi Yok",
      publishedAt: data.published_at || "",
      releaseNotes: data.body || "Sürüm notu eklenmemiş.",
      githubUrl: data.html_url || "https://github.com/ufukkay/CoreRemote",
      serverVersion: SERVER_VERSION
    });
  } catch (err) {
    console.error("Error checking updates from GitHub:", err.message);
    res.status(500).json({ error: "Failed to check update", details: err.message });
  }
});

// Test için uzaktan güncelleme tetikleme API'si
app.get("/api/test/trigger-update", (req, res) => {
  const deviceId = req.query.deviceId || "DESKTOP-SIH3FAC";
  const agentWs = activeAgents.get(deviceId);
  if (agentWs && agentWs.readyState === wsReadyStateOpen()) {
    const updateUrl = `http://${req.headers.host}/downloads/CoreRemote.Agent.Setup.exe`;
    agentWs.send(JSON.stringify({ type: "trigger_update", data: { url: updateUrl } }));
    console.log(`[HTTP TRIGGER] Sent update request to ${deviceId} via REST API`);
    return res.send(`Triggered update for ${deviceId} with ${updateUrl}`);
  }
  res.status(404).send(`Agent ${deviceId} offline or not found`);
});

// Ajan Özelleştirilmiş Kurulum Scripti (PowerShell) API'si
app.get("/api/builder/install", (req, res) => {
  const host = req.headers.host;
  const title = req.query.title || "CoreRemote Ajanı";
  const serverUrl = req.query.server || `ws://${host.split(':')[0]}:5000/agent-socket`;

  try {
    const agentSourcePath = path.join(__dirname, "../CoreRemote.Agent/Agent.cs");
    let agentCode = fs.readFileSync(agentSourcePath, "utf-8");

    // Read the logo icon as Base64 if exists
    const logoIcoPath = path.join(__dirname, "agent_logo.ico");
    let logoBase64 = "";
    if (fs.existsSync(logoIcoPath)) {
      logoBase64 = fs.readFileSync(logoIcoPath).toString("base64");
    }

    // Replace the configuration values dynamically
    agentCode = agentCode.replace(
      /private static string ServerUrl = "ws:\/\/localhost:5000\/agent-socket";/,
      `private static string ServerUrl = "${serverUrl}";`
    );
    agentCode = agentCode.replace(
      /private static string TrayTitle = "CoreRemote Ajanı";/,
      `private static string TrayTitle = "${title}";`
    );
    agentCode = agentCode.replace(
      /private static string ApiUrl = "http:\/\/localhost:5000\/api\/update\/check";/,
      `private static string ApiUrl = "http://${host}/api/update/check";`
    );

    // Base64 encode to prevent encoding and escape issues in PowerShell here-string
    const base64Code = Buffer.from(agentCode, "utf-8").toString("base64");

    const psScript = `<#
.SYNOPSIS
    CoreRemote Agent Unified Installer
    Installs, compiles and runs the customized CoreRemote Agent.
#>

$ErrorActionPreference = "Stop"
$dir = "C:\\ProgramData\\CoreRemote"
$logFile = "$dir\\setup.log"

if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

Write-Host "=> CoreRemote Ajan kurulumu basliyor..." -ForegroundColor Cyan
Add-Content -Path $logFile -Value "Installer started at $(Get-Date)"

# 1. Check Administrator privileges
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[HATA] Lutfen PowerShell'i Yonetici (Admin) olarak calistirin." -ForegroundColor Red
    Add-Content -Path $logFile -Value "Error: Not running as Administrator"
    exit 1
}

# 2. Stop running agent
Write-Host ">> Eski ajan surumu kontrol ediliyor..." -ForegroundColor Gray
Stop-Process -Name CoreRemoteAgent -Force -ErrorAction SilentlyContinue

# 3. Decode C# Agent code
Write-Host ">> Kod cozumleniyor..." -ForegroundColor Gray
$base64Code = "${base64Code}"
$bytes = [System.Convert]::FromBase64String($base64Code)
$utf8 = New-Object System.Text.UTF8Encoding
$code = $utf8.GetString($bytes)
$code | Out-File -FilePath "$dir\\Agent.cs" -Encoding UTF8

# 4. Compile C# code
Write-Host ">> Derleme baslatiliyor (csc.exe)..." -ForegroundColor Gray
$csc = "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"
if (!(Test-Path $csc)) {
    $csc = "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe"
}

if (!(Test-Path $csc)) {
    Write-Host "[HATA] .NET Framework derleyicisi (csc.exe) bulunamadi!" -ForegroundColor Red
    Add-Content -Path $logFile -Value "Error: csc.exe not found"
    exit 1
}

# 3.5 Decode custom icon if embedded
$icoBase64 = "${logoBase64}"
if ($icoBase64) {
    [System.IO.File]::WriteAllBytes("$dir\\agent_logo.ico", [System.Convert]::FromBase64String($icoBase64))
}

$icoArg = if (Test-Path "$dir\\agent_logo.ico") { "/win32icon:\`"$dir\\agent_logo.ico\`" " } else { "" }
$cmd = "& \`"$csc\`" /target:winexe " + $icoArg + "/out:\`"$dir\\CoreRemoteAgent.exe\`" /reference:System.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.dll,System.Core.dll \`"$dir\\Agent.cs\`""
Invoke-Expression $cmd

if ($LASTEXITCODE -ne 0) {
    Write-Host "[HATA] Derleme sirasinda hata olustu!" -ForegroundColor Red
    Add-Content -Path $logFile -Value "Error: Compilation failed"
    exit 1
}

# 5. Start Agent
Write-Host ">> Ajan baslatiliyor..." -ForegroundColor Gray
Start-Process -FilePath "$dir\\CoreRemoteAgent.exe" -WorkingDirectory $dir

Write-Host "=> Kurulum ve Derleme Basariyla Tamamlandi!" -ForegroundColor Green
Add-Content -Path $logFile -Value "Installation completed successfully"
`;

    res.setHeader("Content-Type", "text/plain; charset=utf-8");
    res.send(psScript);
  } catch (err) {
    console.error("Builder installation generation error:", err);
    res.status(500).send("Error generating installer: " + err.message);
  }
});

// Ajanı özelleştirilmiş exe olarak doğrudan derleyip indiren API
app.get("/api/builder/download-exe", (req, res) => {
  const host = req.headers.host;
  const title = req.query.title || "CoreRemote Ajanı";
  const serverUrl = req.query.server || `ws://${host.split(':')[0]}:5000/agent-socket`;

  const buildId = Date.now();
  const tempCsPath = path.join(__dirname, `temp_agent_${buildId}.cs`);
  const tempExePath = path.join(__dirname, `temp_agent_${buildId}.exe`);

  try {
    const agentSourcePath = path.join(__dirname, "../CoreRemote.Agent/Agent.cs");
    let agentCode = fs.readFileSync(agentSourcePath, "utf-8");

    // Parametreleri değiştir
    agentCode = agentCode.replace(
      /private static string ServerUrl = "ws:\/\/localhost:5000\/agent-socket";/,
      `private static string ServerUrl = "${serverUrl}";`
    );
    agentCode = agentCode.replace(
      /private static string TrayTitle = "CoreRemote Ajanı";/,
      `private static string TrayTitle = "${title}";`
    );
    agentCode = agentCode.replace(
      /private static string ApiUrl = "http:\/\/localhost:5000\/api\/update\/check";/,
      `private static string ApiUrl = "http://${host}/api/update/check";`
    );

    // Geçici .cs dosyasını yaz
    fs.writeFileSync(tempCsPath, agentCode, "utf-8");

    // Derleyiciyi (csc.exe) çalıştır
    const logoIcoPath = path.join(__dirname, "agent_logo.ico");
    const hasIco = fs.existsSync(logoIcoPath);
    const icoArg = hasIco ? `/win32icon:"${logoIcoPath}"` : "";

    const cscPath = "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe";
    const cmd = `"${cscPath}" /target:winexe ${icoArg} /out:"${tempExePath}" /reference:System.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.dll,System.Core.dll "${tempCsPath}"`;

    exec(cmd, (err, stdout, stderr) => {
      if (err) {
        console.error("C# Compilation error:", stderr || stdout || err.message);
        // Temizle
        try { fs.unlinkSync(tempCsPath); } catch (e) {}
        try { fs.unlinkSync(tempExePath); } catch (e) {}
        return res.status(500).send("Derleme hatası oluştu: " + (stderr || err.message));
      }

      // Başarılıysa indirmeye başla
      res.download(tempExePath, "CoreRemoteAgent.exe", (downloadErr) => {
        // İndirme bittiğinde geçici dosyaları sil
        try { fs.unlinkSync(tempCsPath); } catch (e) {}
        try { fs.unlinkSync(tempExePath); } catch (e) {}
        if (downloadErr) {
          console.error("Download error:", downloadErr.message);
        }
      });
    });
  } catch (err) {
    console.error("Builder download generation error:", err);
    // Temizle
    try { fs.unlinkSync(tempCsPath); } catch (e) {}
    try { fs.unlinkSync(tempExePath); } catch (e) {}
    res.status(500).send("Ajan oluşturulamadı: " + err.message);
  }
});

// Teknisyen Portal Uygulamasını (EXE) derleyen ve indiren API
app.get("/api/builder/download-technician", (req, res) => {
  const host = req.headers.host;
  const buildId = Date.now();
  const tempCsPath = path.join(__dirname, `temp_tech_${buildId}.cs`);
  const tempExePath = path.join(__dirname, `temp_tech_${buildId}.exe`);

  try {
    const techSourcePath = path.join(__dirname, "../CoreRemote.Technician/Technician.cs");
    let techCode = fs.readFileSync(techSourcePath, "utf-8");

    // Sunucu HTTP ve WS adreslerini dinamik olarak yerleştir
    techCode = techCode.replace(
      /public static string ServerHttpUrl = "http:\/\/localhost:5000";/,
      `public static string ServerHttpUrl = "http://${host}";`
    );
    techCode = techCode.replace(
      /public static string ServerWsUrl = "ws:\/\/localhost:5000";/,
      `public static string ServerWsUrl = "ws://${host.split(':')[0]}:5000";`
    );

    // Geçici .cs dosyasını yaz
    fs.writeFileSync(tempCsPath, techCode, "utf-8");

    // Derleyiciyi (csc.exe) çalıştır
    const logoIcoPath = path.join(__dirname, "agent_logo.ico");
    const hasIco = fs.existsSync(logoIcoPath);
    const icoArg = hasIco ? `/win32icon:"${logoIcoPath}"` : "";

    const cscPath = "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe";
    const cmd = `"${cscPath}" /target:winexe ${icoArg} /out:"${tempExePath}" /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Core.dll "${tempCsPath}"`;

    exec(cmd, (err, stdout, stderr) => {
      if (err) {
        console.error("C# Technician Compilation error:", stderr || stdout || err.message);
        try { fs.unlinkSync(tempCsPath); } catch (e) {}
        try { fs.unlinkSync(tempExePath); } catch (e) {}
        return res.status(500).send("Teknisyen uygulaması derlenirken hata oluştu: " + (stderr || err.message));
      }

      res.download(tempExePath, "CoreRemoteViewer.exe", (downloadErr) => {
        try { fs.unlinkSync(tempCsPath); } catch (e) {}
        try { fs.unlinkSync(tempExePath); } catch (e) {}
        if (downloadErr) {
          console.error("Download error:", downloadErr.message);
        }
      });
    });
  } catch (err) {
    console.error("Technician builder error:", err);
    try { fs.unlinkSync(tempCsPath); } catch (e) {}
    try { fs.unlinkSync(tempExePath); } catch (e) {}
    res.status(500).send("Teknisyen uygulaması oluşturulamadı: " + err.message);
  }
});

// Teknisyen uygulamasının güncel versiyonunu dönen API
app.get("/api/technician/version", (req, res) => {
  res.json({ version: "1.1.0" });
});

// Sunucuyu kendi kendine güncelleyen (git pull + deploy-iis.ps1) API
app.all("/api/admin/update-server", (req, res) => {
  console.log("[SERVER UPDATE] Sunucu otomatik güncelleme tetiklendi!");

  // Bağımsız (detached) PowerShell süreci başlat (çift tırnak kaçış hatasını önlemek için spawn kullanılır)
  const pCmd = `Start-Process powershell.exe -ArgumentList '-NoProfile -ExecutionPolicy Bypass -Command "cd C:\\CoreRemote; git pull; .\\deploy-iis.ps1"' -WindowStyle Hidden`;
  
  try {
    const child = spawn("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", pCmd], {
      detached: true,
      stdio: "ignore"
    });
    child.unref();

    console.log("[SERVER UPDATE] Güncelleme arka planda başarıyla başlatıldı.");
    res.json({ success: true, message: "Sunucu güncelleme süreci başlatıldı. Servisler 15 saniye içinde yeniden yüklenecektir." });
  } catch (err) {
    console.error("[SERVER UPDATE ERR] Güncelleme başlatılamadı:", err.message);
    res.status(500).json({ success: false, error: err.message });
  }
});

// Sunucuyu Başlat
server.listen(PORT, "0.0.0.0", () => {
  console.log(`\n🚀 CoreRemote Sunucusu hazır -> http://localhost:${PORT}\n`);
});
