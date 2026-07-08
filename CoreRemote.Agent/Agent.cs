using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Management;
using System.Windows.Forms;

namespace CoreRemote.Agent
{
    class Program
    {
        // ── Configuration ──
        private static string ServerUrl = "ws://localhost:5000/agent-socket";
        private static string ApiUrl = "http://localhost:5000/api/update/check";
        private static string DeviceId = Environment.MachineName;
        private static string Version = "1.0.0";
        private static string TrayTitle = "CoreRemote Ajanı";

        private static ClientWebSocket _ws;
        private static CancellationTokenSource _cts;
        private static bool _isStreaming = false;
        private static int _frameIntervalMs = 100; // ~10 FPS default
        private static double _jpegQuality = 60.0; // Compression quality
        private static bool _isConnected = false;

        // ── System Tray Icon & Notify ──
        private static NotifyIcon _trayIcon;
        private static Form _aboutForm;
        private static Label _statusLabel;

        // ── Win32 APIs for Console Hiding and Input Simulation ──
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUT
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(4)] public MOUSEINPUT mi;
            [FieldOffset(4)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public int dwFlags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public short wVk;
            public short wScan;
            public int dwFlags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;

        private const int MOUSEEVENTF_MOVE = 0x0001;
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;
        private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const int MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const int MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const int MOUSEEVENTF_WHEEL = 0x0800;

        private const int KEYEVENTF_KEYDOWN = 0x0000;
        private const int KEYEVENTF_KEYUP = 0x0002;

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                // Register autostart in Registry
                SetStartup();

                // Hide the console window immediately
                IntPtr hConsole = GetConsoleWindow();
                if (hConsole != IntPtr.Zero)
                {
                    ShowWindow(hConsole, SW_HIDE);
                }

                // Parse arguments
                if (args.Length > 0)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "--server" && i + 1 < args.Length) ServerUrl = args[++i];
                        if (args[i] == "--device-id" && i + 1 < args.Length) DeviceId = args[++i];
                        if (args[i] == "--api" && i + 1 < args.Length) ApiUrl = args[++i];
                        if (args[i] == "--show-console")
                        {
                            if (hConsole != IntPtr.Zero) ShowWindow(hConsole, SW_SHOW);
                        }
                    }
                }

                // Run WebSocket background thread
                Task.Run(() => ConnectLoopAsync());

                // Initialize System Tray Icon on the main WinForms thread
                InitializeTray();

                // Start Windows Forms Message Loop
                Application.Run();
            }
            catch (Exception ex)
            {
                File.WriteAllText("crash.txt", ex.ToString());
            }
        }

        private static void InitializeTray()
        {
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = TrayTitle + " v" + Version + " (Bağlanıyor...)";
            
            try
            {
                // Try to extract compiled win32icon if exists
                _trayIcon.Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                // Fallback to dynamic CR icon
                _trayIcon.Icon = CreateCustomIcon();
            }
            _trayIcon.Visible = true;

            // Context Menu
            ContextMenu contextMenu = new ContextMenu();
            contextMenu.MenuItems.Add("Hakkında / Sürüm Göster", (s, e) => ShowAboutDialog());
            contextMenu.MenuItems.Add("-");
            contextMenu.MenuItems.Add("Çıkış (Exit)", (s, e) => {
                _trayIcon.Visible = false;
                Environment.Exit(0);
            });
            _trayIcon.ContextMenu = contextMenu;

            // Double click opens the About UI
            _trayIcon.DoubleClick += (s, e) => ShowAboutDialog();
            _trayIcon.Click += (s, e) => ShowAboutDialog();
        }

        private static Icon CreateCustomIcon()
        {
            // Create a 16x16 icon with a dark background and green "CR" letters
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(22, 27, 34)); // Dark background
                    g.DrawRectangle(new Pen(Color.FromArgb(48, 54, 61), 1), 0, 0, 15, 15);
                    
                    using (Font font = new Font("Arial", 8, FontStyle.Bold))
                    {
                        g.DrawString("CR", font, Brushes.GreenYellow, 0, 1);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void ShowAboutDialog()
        {
            // If already open, bring to front
            if (_aboutForm != null && !_aboutForm.IsDisposed)
            {
                _aboutForm.Activate();
                return;
            }

            // Design a modern, clean about dialog
            _aboutForm = new Form();
            _aboutForm.Text = TrayTitle + " Bilgisi";
            _aboutForm.Size = new Size(320, 240);
            _aboutForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            _aboutForm.MaximizeBox = false;
            _aboutForm.MinimizeBox = false;
            _aboutForm.StartPosition = FormStartPosition.CenterScreen;
            _aboutForm.BackColor = Color.FromArgb(13, 17, 23); // GitHub background color

            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(15);
            panel.RowCount = 5;
            panel.ColumnCount = 1;

            Label titleLabel = new Label();
            titleLabel.Text = TrayTitle;
            titleLabel.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(88, 166, 255); // Blue Accent
            titleLabel.Dock = DockStyle.Fill;

            Label versionLabel = new Label();
            versionLabel.Text = "Sürüm (Version): " + Version;
            versionLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            versionLabel.ForeColor = Color.FromArgb(201, 209, 217);
            versionLabel.Dock = DockStyle.Fill;

            Label idLabel = new Label();
            idLabel.Text = "Cihaz ID: " + DeviceId;
            idLabel.Font = new Font("Consolas", 9, FontStyle.Regular);
            idLabel.ForeColor = Color.FromArgb(201, 209, 217);
            idLabel.Dock = DockStyle.Fill;

            _statusLabel = new Label();
            _statusLabel.Text = "Durum: " + (_isConnected ? "Bağlı (Online)" : "Bağlantı Kesildi (Offline)");
            _statusLabel.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _statusLabel.ForeColor = _isConnected ? Color.LightGreen : Color.LightCoral;
            _statusLabel.Dock = DockStyle.Fill;

            Button closeBtn = new Button();
            closeBtn.Text = "Kapat";
            closeBtn.BackColor = Color.FromArgb(33, 38, 45);
            closeBtn.ForeColor = Color.FromArgb(201, 209, 217);
            closeBtn.FlatStyle = FlatStyle.Flat;
            closeBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            closeBtn.Dock = DockStyle.Right;
            closeBtn.Click += (s, e) => _aboutForm.Close();

            panel.Controls.Add(titleLabel);
            panel.Controls.Add(versionLabel);
            panel.Controls.Add(idLabel);
            panel.Controls.Add(_statusLabel);
            panel.Controls.Add(closeBtn);

            _aboutForm.Controls.Add(panel);
            _aboutForm.Show();
        }

        private static void UpdateTrayStatus(bool connected)
        {
            _isConnected = connected;
            if (_trayIcon != null)
            {
                string status = connected ? "Bağlandı (Online)" : "Bağlantı Kesildi (Offline)";
                _trayIcon.Text = TrayTitle + " v" + Version + " (" + status + ")";
            }

            if (_aboutForm != null && !_aboutForm.IsDisposed)
            {
                // Invoke UI update if required
                _aboutForm.Invoke((MethodInvoker)delegate {
                    if (_statusLabel != null)
                    {
                        _statusLabel.Text = "Durum: " + (connected ? "Bağlı (Online)" : "Bağlantı Kesildi (Offline)");
                        _statusLabel.ForeColor = connected ? Color.LightGreen : Color.LightCoral;
                    }
                });
            }
        }

        private static async Task ConnectLoopAsync()
        {
            while (true)
            {
                bool connectFailed = false;
                string failMessage = "";

                try
                {
                    _cts = new CancellationTokenSource();
                    _ws = new ClientWebSocket();

                    string wsConnectUrl = ServerUrl + "?deviceId=" + Uri.EscapeDataString(DeviceId);
                    Console.WriteLine("Connecting to server...");
                    await _ws.ConnectAsync(new Uri(wsConnectUrl), _cts.Token);
                    Console.WriteLine("Connected successfully!");

                    UpdateTrayStatus(true);

                    // Send initial telemetry
                    await SendTelemetryAsync();

                    // Start receive loop
                    await ReceiveLoopAsync();
                }
                catch (Exception ex)
                {
                    connectFailed = true;
                    failMessage = ex.Message;
                }

                UpdateTrayStatus(false);

                if (connectFailed)
                {
                    Console.WriteLine("Error: " + failMessage + ". Reconnecting in 5 seconds...");
                    await Task.Delay(5000);
                }
            }
        }

        private static async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[8192];
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", _cts.Token);
                    break;
                }

                string messageStr = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await ProcessServerMessageAsync(messageStr);
            }
        }

        private static async Task ProcessServerMessageAsync(string jsonMessage)
        {
            try
            {
                string type = GetJsonValue(jsonMessage, "type");
                
                Console.WriteLine("Received message type: " + type);

                switch (type)
                {
                    case "request_telemetry":
                        await SendTelemetryAsync();
                        break;

                    case "trigger_update":
                        string updateUrl = GetJsonValue(jsonMessage, "url");
                        var unused = Task.Run(async () => await TriggerUpdateAsync(updateUrl));
                        break;

                    case "webrtc_signal":
                        string signalData = GetJsonValue(jsonMessage, "data");
                        string sigAction = GetJsonValue(signalData, "action");
                        if (sigAction == "start_stream" || sigAction == "stop_stream" || sigAction == "lock" || sigAction == "restart" || sigAction == "shutdown")
                        {
                            HandleAction(sigAction, signalData);
                        }
                        else
                        {
                            ProcessControlSignal(signalData);
                        }
                        break;

                    default:
                        string action = GetJsonValue(jsonMessage, "action");
                        if (!string.IsNullOrEmpty(action))
                        {
                            HandleAction(action, jsonMessage);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing message: " + ex.Message);
            }
        }

        private static void HandleAction(string action, string fullMessage)
        {
            Console.WriteLine("Handling action: " + action);
            switch (action)
            {
                case "start_stream":
                    if (!_isStreaming)
                    {
                        _isStreaming = true;
                        string qualityStr = GetJsonValue(fullMessage, "quality");
                        double q;
                        if (!string.IsNullOrEmpty(qualityStr) && double.TryParse(qualityStr, out q)) _jpegQuality = q;
                        
                        string intervalStr = GetJsonValue(fullMessage, "interval");
                        int iv;
                        if (!string.IsNullOrEmpty(intervalStr) && int.TryParse(intervalStr, out iv)) _frameIntervalMs = iv;

                        Task.Run(async () => await StreamScreenAsync());
                    }
                    break;

                case "stop_stream":
                    _isStreaming = false;
                    break;

                case "lock":
                    Process.Start(@"C:\Windows\System32\rundll32.exe", "user32.dll,LockWorkStation");
                    break;

                case "restart":
                    Process.Start("shutdown.exe", "-r -t 0");
                    break;

                case "shutdown":
                    Process.Start("shutdown.exe", "-s -t 0");
                    break;

                case "input":
                    string inputData = GetJsonValue(fullMessage, "data");
                    ProcessControlSignal(inputData);
                    break;
            }
        }

        private static async Task StreamScreenAsync()
        {
            Console.WriteLine("Started screen streaming loop...");
            while (_isStreaming && _ws.State == WebSocketState.Open)
            {
                try
                {
                    byte[] frameBytes = CaptureScreenJpeg();
                    if (frameBytes != null)
                    {
                        byte[] wsPacket = new byte[frameBytes.Length + 1];
                        wsPacket[0] = 0x01; // Frame type identifier
                        Buffer.BlockCopy(frameBytes, 0, wsPacket, 1, frameBytes.Length);

                        await _ws.SendAsync(new ArraySegment<byte>(wsPacket), WebSocketMessageType.Binary, true, _cts.Token);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error streaming screen frame: " + ex.Message);
                    break;
                }

                await Task.Delay(_frameIntervalMs);
            }
            Console.WriteLine("Screen streaming loop stopped.");
        }

        private static byte[] CaptureScreenJpeg()
        {
            try
            {
                int width = GetSystemMetrics(SM_CXSCREEN);
                int height = GetSystemMetrics(SM_CYSCREEN);

                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                        DrawMouseCursor(g);
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);
                        System.Drawing.Imaging.Encoder myEncoder = System.Drawing.Imaging.Encoder.Quality;
                        EncoderParameters myEncoderParameters = new EncoderParameters(1);
                        EncoderParameter myEncoderParameter = new EncoderParameter(myEncoder, (long)_jpegQuality);
                        myEncoderParameters.Param[0] = myEncoderParameter;

                        bmp.Save(ms, jpgEncoder, myEncoderParameters);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CaptureScreen Error: " + ex.Message);
                return null;
            }
        }

        private static void DrawMouseCursor(Graphics g)
        {
            try
            {
                POINT p;
                if (GetCursorPos(out p))
                {
                    g.FillEllipse(Brushes.Red, p.X, p.Y, 10, 10);
                    g.DrawEllipse(Pens.White, p.X, p.Y, 10, 10);
                }
            }
            catch {}
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        private static async Task SendTelemetryAsync()
        {
            try
            {
                string hostname = Environment.MachineName;
                string username = Environment.UserName;
                string osVersion = Environment.OSVersion.ToString();
                string cpu = GetCpuName();
                string ram = GetRamSize();
                string ipAddress = GetLocalIpAddress();

                string payload = "{" +
                    "\"type\":\"telemetry\"," +
                    "\"data\":{" +
                        "\"hostname\":\"" + EscJ(hostname) + "\"," +
                        "\"username\":\"" + EscJ(username) + "\"," +
                        "\"osVersion\":\"" + EscJ(osVersion) + "\"," +
                        "\"cpu\":\"" + EscJ(cpu) + "\"," +
                        "\"ram\":\"" + EscJ(ram) + "\"," +
                        "\"ipAddress\":\"" + EscJ(ipAddress) + "\"," +
                        "\"version\":\"" + Version + "\"" +
                    "}" +
                "}";

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
                Console.WriteLine("Telemetry sent!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error sending telemetry: " + ex.Message);
            }
        }

        private static async Task SendUpdateStatusAsync(string status, string message)
        {
            try
            {
                string payload = "{" +
                    "\"type\":\"update_status\"," +
                    "\"data\":{" +
                        "\"status\":\"" + EscJ(status) + "\"," +
                        "\"message\":\"" + EscJ(message) + "\"" +
                    "}" +
                "}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error sending update status: " + ex.Message);
            }
        }

        private static void ProcessControlSignal(string signalJson)
        {
            try
            {
                string action = GetJsonValue(signalJson, "action");
                if (action == "mousemove")
                {
                    double rx = double.Parse(GetJsonValue(signalJson, "x"));
                    double ry = double.Parse(GetJsonValue(signalJson, "y"));

                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);

                    int absoluteX = (int)(rx * screenWidth);
                    int absoluteY = (int)(ry * screenHeight);

                    INPUT[] inputs = new INPUT[1];
                    inputs[0] = new INPUT();
                    inputs[0].type = INPUT_MOUSE;
                    inputs[0].mi = new MOUSEINPUT
                    {
                        dx = (int)(rx * 65535.0),
                        dy = (int)(ry * 65535.0),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                        mouseData = 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    };
                    SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                }
                else if (action == "mousedown" || action == "mouseup")
                {
                    int button = int.Parse(GetJsonValue(signalJson, "button"));
                    int flag = 0;

                    if (action == "mousedown")
                    {
                        if (button == 0) flag = MOUSEEVENTF_LEFTDOWN;
                        else if (button == 2) flag = MOUSEEVENTF_RIGHTDOWN;
                    }
                    else // mouseup
                    {
                        if (button == 0) flag = MOUSEEVENTF_LEFTUP;
                        else if (button == 2) flag = MOUSEEVENTF_RIGHTUP;
                    }

                    if (flag != 0)
                    {
                        INPUT[] inputs = new INPUT[1];
                        inputs[0] = new INPUT();
                        inputs[0].type = INPUT_MOUSE;
                        inputs[0].mi = new MOUSEINPUT
                        {
                            dwFlags = flag,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        };
                        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                    }
                }
                else if (action == "keydown" || action == "keyup")
                {
                    short vk = short.Parse(GetJsonValue(signalJson, "vk"));
                    int flag = (action == "keyup") ? KEYEVENTF_KEYUP : KEYEVENTF_KEYDOWN;

                    INPUT[] inputs = new INPUT[1];
                    inputs[0] = new INPUT();
                    inputs[0].type = INPUT_KEYBOARD;
                    inputs[0].ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flag,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    };
                    SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error simulating input: " + ex.Message);
            }
        }

        private static async Task TriggerUpdateAsync(string url)
        {
            string errorMessage = null;
            try
            {
                await SendUpdateStatusAsync("İndiriliyor", "Yeni güncelleme paketi indiriliyor...");
                Console.WriteLine("Downloading update from: " + url);
                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                string installDir = Path.GetDirectoryName(currentExePath);
                
                string newExePath = Path.Combine(installDir, "CoreRemoteAgent_new.exe");
                string batPath = Path.Combine(installDir, "update.bat");

                using (WebClient wc = new WebClient())
                {
                    await wc.DownloadFileTaskAsync(new Uri(url), newExePath);
                }

                if (!File.Exists(newExePath) || new FileInfo(newExePath).Length == 0)
                {
                    throw new FileNotFoundException("İndirilen güncelleme dosyası boş veya bulunamadı.");
                }

                await SendUpdateStatusAsync("Kuruluyor", "Güncelleme dosyaları hazırlanıyor, ajan yeniden başlatılacak...");
                Console.WriteLine("Download complete. Generating update script...");

                // Generate self-deleting batch update script with logging
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping 127.0.0.1 -n 5 > nul");
                sb.AppendLine("echo Starting update... > \"" + Path.Combine(installDir, "updatelog.txt") + "\"");
                sb.AppendLine("move /y \"" + newExePath + "\" \"" + currentExePath + "\" >> \"" + Path.Combine(installDir, "updatelog.txt") + "\" 2>&1");
                sb.AppendLine("echo Starting new agent... >> \"" + Path.Combine(installDir, "updatelog.txt") + "\"");
                sb.AppendLine("start \"\" \"" + currentExePath + "\" >> \"" + Path.Combine(installDir, "updatelog.txt") + "\" 2>&1");
                sb.AppendLine("echo Done. >> \"" + Path.Combine(installDir, "updatelog.txt") + "\"");
                sb.AppendLine("del \"%~f0\"");

                File.WriteAllText(batPath, sb.ToString());

                Console.WriteLine("Running update script and exiting...");
                
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + batPath + "\"";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.UseShellExecute = true;
                
                Process.Start(psi);
                
                // Exit current process immediately so it releases file lock
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Update failed: " + ex.Message);
                errorMessage = ex.Message;
            }

            if (errorMessage != null)
            {
                await SendUpdateStatusAsync("Hata", "Güncelleme başarısız: " + errorMessage);
            }
        }

        private static string GetJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return "";
            
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey);
            if (keyIndex == -1) return "";

            int colonIndex = json.IndexOf(":", keyIndex + searchKey.Length);
            if (colonIndex == -1) return "";

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && (json[valueStart] == ' ' || json[valueStart] == '\t' || json[valueStart] == '\r' || json[valueStart] == '\n'))
            {
                valueStart++;
            }

            if (valueStart >= json.Length) return "";

            if (json[valueStart] == '"')
            {
                int stringEnd = json.IndexOf('"', valueStart + 1);
                if (stringEnd == -1) return "";
                return json.Substring(valueStart + 1, stringEnd - valueStart - 1);
            }
            else if (json[valueStart] == '{')
            {
                int bracketCount = 1;
                int idx = valueStart + 1;
                while (idx < json.Length && bracketCount > 0)
                {
                    if (json[idx] == '{') bracketCount++;
                    else if (json[idx] == '}') bracketCount--;
                    idx++;
                }
                return json.Substring(valueStart, idx - valueStart);
            }
            else
            {
                int endIdx = valueStart;
                while (endIdx < json.Length && json[endIdx] != ',' && json[endIdx] != '}' && json[endIdx] != ']')
                {
                    endIdx++;
                }
                return json.Substring(valueStart, endIdx - valueStart).Trim();
            }
        }

        private static string GetCpuName()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("select Name from Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        return obj["Name"].ToString().Trim();
                    }
                }
            }
            catch {}
            return "Intel/AMD Processor";
        }

        private static string GetRamSize()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        double bytes = double.Parse(obj["TotalPhysicalMemory"].ToString());
                        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
                        return gb.ToString("F1") + " GB";
                    }
                }
            }
            catch {}
            return "Unknown RAM";
        }

        private static void SetStartup()
        {
            try
            {
                string appName = "CoreRemoteAgent";
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key != null)
                {
                    key.SetValue(appName, "\"" + path + "\"");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Startup registration failed: " + ex.Message);
            }
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch {}
            return "127.0.0.1";
        }

        private static string EscJ(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "").Replace("\t", " ");
        }
    }
}
