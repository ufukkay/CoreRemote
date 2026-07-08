using System;
using System.Collections.Generic;
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
using Microsoft.Win32;

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

        // ── Advanced Support Features State ──
        private static Process _cmdProcess;
        private static StreamWriter _cmdInput;
        private static bool _audioStreaming = false;
        private static string _lastClipboardText = "";
        private static ChatForm _chatForm;
        private static int _activeMonitorIndex = 0; // Primary monitor default

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

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // Win32 Input Block & Screen standby APIs
        [DllImport("user32.dll")]
        public static extern bool BlockInput(bool fBlockIt);

        [DllImport("user32.dll")]
        public static extern int SendMessage(int hWnd, int hMsg, int wParam, int lParam);

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_ON = -1;
        private const int MONITOR_OFF = 2;
        private const int HWND_BROADCAST = 0xFFFF;

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
                // Force administrator privilege check on start
                using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
                    if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    {
                        ElevateProcess();
                        return; // Terminate non-elevated launch
                    }
                }

                // Make process DPI-aware to prevent screen capture scaling offset bugs
                SetProcessDPIAware();

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

                // Start clipboard sync watcher
                Thread clipboardThread = new Thread(ClipboardWatchLoop);
                clipboardThread.SetApartmentState(ApartmentState.STA);
                clipboardThread.Start();

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
            if (_aboutForm != null && !_aboutForm.IsDisposed)
            {
                _aboutForm.Activate();
                return;
            }

            _aboutForm = new Form();
            _aboutForm.Text = TrayTitle + " Bilgisi";
            _aboutForm.Size = new Size(320, 240);
            _aboutForm.FormBorderStyle = FormBorderStyle.FixedDialog;
            _aboutForm.MaximizeBox = false;
            _aboutForm.MinimizeBox = false;
            _aboutForm.StartPosition = FormStartPosition.CenterScreen;
            _aboutForm.BackColor = Color.FromArgb(13, 17, 23);

            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Padding = new Padding(15);
            panel.RowCount = 5;
            panel.ColumnCount = 1;

            Label titleLabel = new Label();
            titleLabel.Text = TrayTitle;
            titleLabel.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(88, 166, 255);
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
                CleanupTerminal();
                StopAudioStream();

                if (connectFailed)
                {
                    Console.WriteLine("Error: " + failMessage + ". Reconnecting in 5 seconds...");
                    await Task.Delay(5000);
                }
            }
        }

        private static async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[65536]; // Support larger JSON packets
            MemoryStream ms = new MemoryStream();

            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", _cts.Token);
                        break;
                    }

                    ms.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        string messageStr = Encoding.UTF8.GetString(ms.ToArray());
                        ms.SetLength(0); // Reset buffer
                        await ProcessServerMessageAsync(messageStr);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Receive loop error: " + ex.Message);
            }
            finally
            {
                ms.Dispose();
            }
        }

        private static async Task ProcessServerMessageAsync(string jsonMessage)
        {
            try
            {
                string type = GetJsonValue(jsonMessage, "type");

                switch (type)
                {
                    case "webrtc_signal":
                        string signalData = GetJsonValue(jsonMessage, "data");
                        string sigAction = GetJsonValue(signalData, "action");
                        
                        // Route control commands to HandleAction, raw mouse/keyboard events to ProcessControlSignal
                        if (sigAction == "start_stream" || sigAction == "stop_stream" || 
                            sigAction == "lock" || sigAction == "restart" || sigAction == "shutdown" ||
                            sigAction == "select_monitor" || sigAction == "block_input" || sigAction == "blank_screen" ||
                            sigAction == "term_cmd" || sigAction == "list_processes" || sigAction == "kill_process" ||
                            sigAction == "list_files" || sigAction == "download_file" || sigAction == "chat_msg" ||
                            sigAction == "elevate_uac" || sigAction == "audio_stream" || sigAction == "clipboard_sync")
                        {
                            HandleAction(sigAction, signalData);
                        }
                        else
                        {
                            ProcessControlSignal(signalData);
                        }
                        break;

                    case "request_telemetry":
                        await SendTelemetryAsync();
                        break;

                    case "trigger_update":
                        string updateUrl = GetJsonValue(jsonMessage, "url");
                        var unused = Task.Run(async () => await TriggerUpdateAsync(updateUrl));
                        break;

                    case "file_chunk":
                        // Handle inbound file upload chunk from technician
                        string fileName = GetJsonValue(jsonMessage, "name");
                        string base64Data = GetJsonValue(jsonMessage, "bytes");
                        bool isFirst = GetJsonValue(jsonMessage, "isFirst").ToLower() == "true";
                        
                        byte[] fileBytes = Convert.FromBase64String(base64Data);
                        string downloadDir = @"C:\ProgramData\CoreRemote\Downloads";
                        if (!Directory.Exists(downloadDir)) Directory.CreateDirectory(downloadDir);
                        string filePath = Path.Combine(downloadDir, fileName);

                        using (FileStream fs = new FileStream(filePath, isFirst ? FileMode.Create : FileMode.Append, FileAccess.Write))
                        {
                            fs.Write(fileBytes, 0, fileBytes.Length);
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

                case "select_monitor":
                    string monitorIdxStr = GetJsonValue(fullMessage, "index");
                    int mIdx;
                    if (int.TryParse(monitorIdxStr, out mIdx) && mIdx >= 0 && mIdx < Screen.AllScreens.Length)
                    {
                        _activeMonitorIndex = mIdx;
                        Console.WriteLine("Active monitor switched to index: " + mIdx);
                    }
                    break;

                case "block_input":
                    bool blockVal = GetJsonValue(fullMessage, "block").ToLower() == "true";
                    BlockInput(blockVal);
                    Console.WriteLine("Physical inputs blocked: " + blockVal);
                    break;

                case "blank_screen":
                    bool blankVal = GetJsonValue(fullMessage, "blank").ToLower() == "true";
                    if (blankVal)
                    {
                        BlockInput(true); // Lock physical movement so screen won't wake up
                        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_OFF);
                    }
                    else
                    {
                        BlockInput(false);
                        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, MONITOR_ON);
                    }
                    Console.WriteLine("Physical monitor blanked: " + blankVal);
                    break;

                case "term_cmd":
                    string termCommand = GetJsonValue(fullMessage, "cmd");
                    InitializeTerminal();
                    if (_cmdInput != null)
                    {
                        _cmdInput.WriteLine(termCommand);
                    }
                    break;

                case "list_processes":
                    SendProcessList();
                    break;

                case "kill_process":
                    int pidToKill = int.Parse(GetJsonValue(fullMessage, "pid"));
                    try
                    {
                        Process.GetProcessById(pidToKill).Kill();
                        SendProcessList(); // Resend updated list
                    }
                    catch (Exception ex)
                    {
                        SendControlMessageAsync("term_output", "Process sonlandırılamadı: " + ex.Message);
                    }
                    break;

                case "list_files":
                    string dirPath = GetJsonValue(fullMessage, "path");
                    SendFileList(dirPath);
                    break;

                case "download_file":
                    string dlFilePath = GetJsonValue(fullMessage, "path");
                    Task.Run(() => StreamFileToTechnician(dlFilePath));
                    break;

                case "chat_msg":
                    string chatText = GetJsonValue(fullMessage, "text");
                    ShowChatForm(chatText, false);
                    break;

                case "elevate_uac":
                    ElevateProcess();
                    break;

                case "audio_stream":
                    bool enableAudio = GetJsonValue(fullMessage, "enable").ToLower() == "true";
                    if (enableAudio)
                    {
                        StartAudioStream();
                    }
                    else
                    {
                        StopAudioStream();
                    }
                    break;

                case "clipboard_sync":
                    string clipText = GetJsonValue(fullMessage, "text");
                    SetLocalClipboardText(clipText);
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
                // Multi-monitor support: select screen coordinates dynamically
                Screen activeScreen = Screen.PrimaryScreen;
                if (_activeMonitorIndex >= 0 && _activeMonitorIndex < Screen.AllScreens.Length)
                {
                    activeScreen = Screen.AllScreens[_activeMonitorIndex];
                }

                int width = activeScreen.Bounds.Width;
                int height = activeScreen.Bounds.Height;
                int left = activeScreen.Bounds.Left;
                int top = activeScreen.Bounds.Top;

                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                        DrawMouseCursor(g, left, top);
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

        private static void DrawMouseCursor(Graphics g, int screenLeft, int screenTop)
        {
            try
            {
                POINT p;
                if (GetCursorPos(out p))
                {
                    // Offset by screen left/top for multi-monitor layout mapping
                    int relativeX = p.X - screenLeft;
                    int relativeY = p.Y - screenTop;
                    g.FillEllipse(Brushes.Red, relativeX, relativeY, 10, 10);
                    g.DrawEllipse(Pens.White, relativeX, relativeY, 10, 10);
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

        // ── INTERACTIVE TERMINAL (cmd.exe) REDIRECT ─────────────────────────
        private static void InitializeTerminal()
        {
            if (_cmdProcess != null && !_cmdProcess.HasExited) return;

            try
            {
                _cmdProcess = new Process();
                _cmdProcess.StartInfo.FileName = "cmd.exe";
                _cmdProcess.StartInfo.RedirectStandardInput = true;
                _cmdProcess.StartInfo.RedirectStandardOutput = true;
                _cmdProcess.StartInfo.RedirectStandardError = true;
                _cmdProcess.StartInfo.UseShellExecute = false;
                _cmdProcess.StartInfo.CreateNoWindow = true;

                _cmdProcess.OutputDataReceived += (s, e) => {
                    if (e.Data != null) SendControlMessageAsync("term_output", e.Data + "\r\n");
                };
                _cmdProcess.ErrorDataReceived += (s, e) => {
                    if (e.Data != null) SendControlMessageAsync("term_output", e.Data + "\r\n");
                };

                _cmdProcess.Start();
                _cmdInput = _cmdProcess.StandardInput;
                _cmdProcess.BeginOutputReadLine();
                _cmdProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to initialize terminal: " + ex.Message);
            }
        }

        private static void CleanupTerminal()
        {
            try
            {
                if (_cmdProcess != null && !_cmdProcess.HasExited)
                {
                    _cmdProcess.Kill();
                }
            }
            catch {}
            _cmdProcess = null;
            _cmdInput = null;
        }

        // ── REMOTE TASK MANAGER API ─────────────────────────────────────────
        private static void SendProcessList()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        long memMb = p.WorkingSet64 / (1024 * 1024);
                        sb.Append(p.Id + "|" + p.ProcessName + "|" + memMb + ";");
                    }
                    catch {}
                }
                SendControlMessageAsync("process_list", sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error listing processes: " + ex.Message);
            }
        }

        // ── FILE MANAGER API ────────────────────────────────────────────────
        private static void SendFileList(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) path = @"C:\";
                var sb = new StringBuilder();

                // Add directories
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var info = new DirectoryInfo(dir);
                    sb.Append("DIR|" + info.Name + ";");
                }

                // Add files
                foreach (var file in Directory.GetFiles(path))
                {
                    var info = new FileInfo(file);
                    sb.Append("FILE|" + info.Name + "|" + info.Length + ";");
                }

                SendControlMessageAsync("file_list", sb.ToString());
            }
            catch (Exception ex)
            {
                SendControlMessageAsync("file_list_error", ex.Message);
            }
        }

        private static async Task StreamFileToTechnician(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    SendControlMessageAsync("file_list_error", "Dosya bulunamadı: " + path);
                    return;
                }

                string name = Path.GetFileName(path);
                byte[] buffer = new byte[65536]; // Stream in 64KB chunks
                int bytesRead;

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        byte[] chunk = new byte[bytesRead];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                        string base64Data = Convert.ToBase64String(chunk);
                        bool isLast = (fs.Position >= fs.Length);

                        string payload = "{" +
                            "\"type\":\"file_download_chunk\"," +
                            "\"name\":\"" + EscJ(name) + "\"," +
                            "\"bytes\":\"" + base64Data + "\"," +
                            "\"isLast\":" + (isLast ? "true" : "false") +
                        "}";

                        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                        if (_ws != null && _ws.State == WebSocketState.Open)
                        {
                            await _ws.SendAsync(new ArraySegment<byte>(payloadBytes), WebSocketMessageType.Text, true, _cts.Token);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SendControlMessageAsync("file_list_error", "İndirme hatası: " + ex.Message);
            }
        }

        // ── CHAT FORM UI ────────────────────────────────────────────────────
        private static void ShowChatForm(string initialText, bool localMsg)
        {
            Application.OpenForms[0].Invoke((MethodInvoker)delegate {
                if (_chatForm == null || _chatForm.IsDisposed)
                {
                    _chatForm = new ChatForm();
                    _chatForm.MessageSent += (s, msg) => {
                        SendControlMessageAsync("chat_msg", msg);
                    };
                    _chatForm.Show();
                }
                _chatForm.AppendMessage(initialText, localMsg);
            });
        }

        // ── CLIPBOARD SYNCHRONIZATION ───────────────────────────────────────
        private static void ClipboardWatchLoop()
        {
            while (true)
            {
                try
                {
                    string currentText = "";
                    Thread t = new Thread(() => {
                        try
                        {
                            if (Clipboard.ContainsText()) currentText = Clipboard.GetText();
                        }
                        catch {}
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join();

                    if (!string.IsNullOrEmpty(currentText) && currentText != _lastClipboardText)
                    {
                        _lastClipboardText = currentText;
                        SendControlMessageAsync("clipboard_sync", currentText);
                    }
                }
                catch {}
                Thread.Sleep(1000);
            }
        }

        private static void SetLocalClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text) || text == _lastClipboardText) return;
            _lastClipboardText = text;

            Thread t = new Thread(() => {
                try
                {
                    Clipboard.SetText(text);
                }
                catch {}
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // ── Win32 / COM LOOPBACK AUDIO STREAMING ────────────────────────────
        private static void StartAudioStream()
        {
            if (_audioStreaming) return;
            _audioStreaming = true;
            Task.Run(() => StreamAudioLoop());
        }

        private static void StopAudioStream()
        {
            _audioStreaming = false;
        }

        private static void StreamAudioLoop()
        {
            IntPtr formatPtr = IntPtr.Zero;
            object deviceEnumeratorObj = null;
            IMMDeviceEnumerator deviceEnumerator = null;
            IMMDevice device = null;
            object audioClientObj = null;
            IAudioClient audioClient = null;
            object captureClientObj = null;
            IAudioCaptureClient captureClient = null;

            try
            {
                // Instantiate standard MMDeviceEnumerator COM object
                deviceEnumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0359-8665-41C6-B4B6-83E90C0030C8")));
                deviceEnumerator = (IMMDeviceEnumerator)deviceEnumeratorObj;

                // Get speaker render output endpoint (0 = eRender, 1 = eConsole)
                int hr = deviceEnumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (hr != 0 || device == null) return;

                // Activate IAudioClient COM interface
                Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                hr = device.Activate(ref IID_IAudioClient, 1 /* CLSCTX_ALL */, IntPtr.Zero, out audioClientObj);
                if (hr != 0 || audioClientObj == null) return;
                audioClient = (IAudioClient)audioClientObj;

                // Get speaker mix format
                hr = audioClient.GetMixFormat(out formatPtr);
                if (hr != 0 || formatPtr == IntPtr.Zero) return;
                WAVEFORMATEX format = (WAVEFORMATEX)Marshal.PtrToStructure(formatPtr, typeof(WAVEFORMATEX));

                // Initialize loopback stream
                Guid sessionGuid = Guid.Empty;
                hr = audioClient.Initialize(0 /* Shared Mode */, 0x00020000 /* AUDCLNT_STREAMFLAGS_LOOPBACK */, 10000000 /* 1 sec latency */, 0, ref format, ref sessionGuid);
                if (hr != 0) return;

                // Get AudioCaptureClient service
                Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                hr = audioClient.GetService(ref IID_IAudioCaptureClient, out captureClientObj);
                if (hr != 0 || captureClientObj == null) return;
                captureClient = (IAudioCaptureClient)captureClientObj;

                audioClient.Start();

                while (_audioStreaming && _ws.State == WebSocketState.Open)
                {
                    uint packetSize;
                    captureClient.GetNextPacketSize(out packetSize);
                    if (packetSize > 0)
                    {
                        IntPtr dataPtr;
                        uint numFrames;
                        uint flags;
                        long devPos;
                        long qpcPos;
                        hr = captureClient.GetBuffer(out dataPtr, out numFrames, out flags, out devPos, out qpcPos);
                        if (hr == 0 && numFrames > 0 && dataPtr != IntPtr.Zero)
                        {
                            int dataSize = (int)(numFrames * format.nBlockAlign);
                            byte[] audioData = new byte[dataSize + 1];
                            audioData[0] = 0x02; // Binary audio packet identifier
                            Marshal.Copy(dataPtr, audioData, 1, dataSize);
                            
                            captureClient.ReleaseBuffer(numFrames);

                            // Send audio binary data packet asynchronously over WebSocket
                            _ws.SendAsync(new ArraySegment<byte>(audioData), WebSocketMessageType.Binary, true, _cts.Token).Wait();
                        }
                    }
                    Thread.Sleep(15);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Audio Stream Loop error: " + ex.Message);
            }
            finally
            {
                if (audioClient != null) audioClient.Stop();
                if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
                _audioStreaming = false;
            }
        }

        // ── UAC PRIVILEGES ELEVATION RESTART ────────────────────────────────
        private static void ElevateProcess()
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = currentExePath,
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
                
                // Terminate non-elevated instance safely
                if (_trayIcon != null) _trayIcon.Visible = false;
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                SendControlMessageAsync("term_output", "UAC yetki talebi reddedildi: " + ex.Message);
            }
        }

        // ── TELEMETRY & HELPER METHODS ──────────────────────────────────────
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

                // Multi-monitor list extraction
                var monitorSb = new StringBuilder();
                for (int i = 0; i < Screen.AllScreens.Length; i++)
                {
                    var s = Screen.AllScreens[i];
                    monitorSb.Append(s.DeviceName + "|" + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " (Ana)" : "") + ";");
                }

                string payload = "{" +
                    "\"type\":\"telemetry\"," +
                    "\"data\":{" +
                        "\"hostname\":\"" + EscJ(hostname) + "\"," +
                        "\"username\":\"" + EscJ(username) + "\"," +
                        "\"osVersion\":\"" + EscJ(osVersion) + "\"," +
                        "\"cpu\":\"" + EscJ(cpu) + "\"," +
                        "\"ram\":\"" + EscJ(ram) + "\"," +
                        "\"ipAddress\":\"" + EscJ(ipAddress) + "\"," +
                        "\"version\":\"" + Version + "\"," +
                        "\"monitors\":\"" + EscJ(monitorSb.ToString()) + "\"" +
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

        private static async void SendControlMessageAsync(string action, string text)
        {
            try
            {
                string payload = "{" +
                    "\"type\":\"webrtc_signal\"," +
                    "\"data\":{" +
                        "\"action\":\"" + action + "\"," +
                        "\"text\":\"" + EscJ(text) + "\"" +
                    "}" +
                "}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
                }
            }
            catch {}
        }

        private static void ProcessControlSignal(string signalJson)
        {
            try
            {
                string action = GetJsonValue(signalJson, "action");
                if (action == "mousemove")
                {
                    double rx = double.Parse(GetJsonValue(signalJson, "x"), System.Globalization.CultureInfo.InvariantCulture);
                    double ry = double.Parse(GetJsonValue(signalJson, "y"), System.Globalization.CultureInfo.InvariantCulture);

                    // Multi-monitor mouse movement coordinate scaling
                    Screen activeScreen = Screen.PrimaryScreen;
                    if (_activeMonitorIndex >= 0 && _activeMonitorIndex < Screen.AllScreens.Length)
                    {
                        activeScreen = Screen.AllScreens[_activeMonitorIndex];
                    }

                    int absoluteX = activeScreen.Bounds.Left + (int)(rx * activeScreen.Bounds.Width);
                    int absoluteY = activeScreen.Bounds.Top + (int)(ry * activeScreen.Bounds.Height);

                    // Map screen relative mouse click onto global absolute coordinates (0-65535)
                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);

                    double mappedX = (double)absoluteX * 65535.0 / screenWidth;
                    double mappedY = (double)absoluteY * 65535.0 / screenHeight;

                    INPUT[] inputs = new INPUT[1];
                    inputs[0] = new INPUT();
                    inputs[0].type = INPUT_MOUSE;
                    inputs[0].mi = new MOUSEINPUT
                    {
                        dx = (int)mappedX,
                        dy = (int)mappedY,
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
                
                if (_trayIcon != null) _trayIcon.Visible = false;
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Console.WriteLine("Update failed: " + ex.Message);
            }

            if (errorMessage != null)
            {
                await SendUpdateStatusAsync("Hata", "Güncelleme başarısız: " + errorMessage);
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

        private static void SetStartup()
        {
            try
            {
                string appName = "CoreRemoteAgent";
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                RegistryKey key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
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

        private static string GetJsonValue(string json, string key)
        {
            try
            {
                string searchKey = "\"" + key + "\"";
                int keyIdx = json.IndexOf(searchKey);
                if (keyIdx == -1) return "";

                int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
                if (colonIdx == -1) return "";

                int valueStart = colonIdx + 1;
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
            catch
            {
                return "";
            }
        }
    }

    // ── LIGHTWEIGHT CLIENT CHAT FORM UI ─────────────────────────────────────
    public class ChatForm : Form
    {
        public event EventHandler<string> MessageSent;
        private TextBox _chatHistory;
        private TextBox _chatInput;
        private Button _sendBtn;

        public ChatForm()
        {
            this.Text = "CoreRemote Uzak Destek Sohbeti";
            this.Size = new Size(350, 450);
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(Screen.PrimaryScreen.Bounds.Right - 370, Screen.PrimaryScreen.Bounds.Bottom - 490);
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            _chatHistory = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.FromArgb(201, 209, 217),
                BorderStyle = BorderStyle.None,
                Location = new Point(10, 10),
                Size = new Size(315, 330),
                Font = new Font("Segoe UI", 9)
            };

            _chatInput = new TextBox
            {
                Location = new Point(10, 355),
                Size = new Size(230, 40),
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.FromArgb(201, 209, 217),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9)
            };
            _chatInput.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { TriggerSend(); e.SuppressKeyPress = true; }
            };

            _sendBtn = new Button
            {
                Text = "Gönder",
                Location = new Point(250, 354),
                Size = new Size(75, 25),
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(201, 209, 217),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _sendBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _sendBtn.Click += (s, e) => TriggerSend();

            this.Controls.Add(_chatHistory);
            this.Controls.Add(_chatInput);
            this.Controls.Add(_sendBtn);
        }

        public void AppendMessage(string msg, bool local)
        {
            string prefix = local ? "Siz: " : "Teknisyen: ";
            _chatHistory.AppendText(prefix + msg + "\r\n\r\n");
            _chatHistory.SelectionStart = _chatHistory.Text.Length;
            _chatHistory.ScrollToCaret();
        }

        private void TriggerSend()
        {
            string txt = _chatInput.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            _chatInput.Clear();
            AppendMessage(txt, true);
            var handler = MessageSent;
            if (handler != null)
            {
                handler(this, txt);
            }
        }
    }

    // ── Win32 / COM LOOPBACK AUDIO API INTEROP DEFINITIONS ─────────────────
    [ComImport, Guid("BCDE0359-8665-41C6-B4B6-83E90C0030C8")]
    internal class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D66606E4-827E-45F1-8B02-DE0904C740C6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hBufferDuration, long hPeriodicity, ref WAVEFORMATEX pFormat, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long pCurrentLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long pDefaultDevicePeriod, out long pMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out long pu64DevicePosition, out long pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }
}
