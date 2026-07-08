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
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CoreRemote.Technician
{
    class Program
    {
        public static string ServerHttpUrl = "http://localhost:5000";
        public static string ServerWsUrl = "ws://localhost:5000";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Register registry custom URL protocol scheme coreremote://
            RegisterCustomProtocol();

            // Check if launched via deep-link
            if (args.Length > 0 && args[0].StartsWith("coreremote://", StringComparison.OrdinalIgnoreCase))
            {
                string rawUrl = args[0];
                string deviceId = ParseDeviceIdFromUrl(rawUrl);
                if (!string.IsNullOrEmpty(deviceId))
                {
                    Application.Run(new ViewerForm(deviceId));
                    return;
                }
            }

            // Default fallback
            Application.Run(new MainForm());
        }

        private static string ParseDeviceIdFromUrl(string url)
        {
            try
            {
                string clean = url.Replace("coreremote://", "");
                if (clean.StartsWith("connect/", StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean.Substring("connect/".Length);
                }
                if (clean.EndsWith("/")) clean = clean.Substring(0, clean.Length - 1);
                return Uri.UnescapeDataString(clean).Trim();
            }
            catch
            {
                return null;
            }
        }

        private static void RegisterCustomProtocol()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\coreremote"))
                {
                    key.SetValue("", "URL:CoreRemote Connection Protocol");
                    key.SetValue("URL Protocol", "");
                    using (RegistryKey shellKey = key.CreateSubKey(@"shell\open\command"))
                    {
                        shellKey.SetValue("", "\"" + exePath + "\" \"%1\"");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Protocol registration failed: " + ex.Message);
            }
        }
    }

    // ── MAIN DASHBOARD FORM (Device Grid View) ──────────────────────────────
    public class MainForm : Form
    {
        private DataGridView _grid;
        private Button _refreshBtn;
        private System.Windows.Forms.Timer _timer;

        public MainForm()
        {
            this.Text = "CoreRemote Teknisyen Portalı";
            this.Size = new Size(850, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            Panel header = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10) };
            Label title = new Label 
            { 
                Text = "Kayıtlı Uzak Bilgisayarlar", 
                Font = new Font("Segoe UI", 12, FontStyle.Bold), 
                ForeColor = Color.FromArgb(88, 166, 255),
                Dock = DockStyle.Left,
                Width = 300
            };
            
            _refreshBtn = new Button
            {
                Text = "Listeyi Yenile",
                Dock = DockStyle.Right,
                Width = 110,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(201, 209, 217),
                Cursor = Cursors.Hand
            };
            _refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _refreshBtn.Click += (s, e) => LoadDevicesAsync();

            header.Controls.Add(title);
            header.Controls.Add(_refreshBtn);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.FromArgb(201, 209, 217),
                GridColor = Color.FromArgb(48, 54, 61),
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 38, 45);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(201, 209, 217);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _grid.DefaultCellStyle.BackColor = Color.FromArgb(13, 17, 23);
            _grid.DefaultCellStyle.ForeColor = Color.FromArgb(201, 209, 217);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(56, 139, 253);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.EnableHeadersVisualStyles = false;

            _grid.Columns.Add("Id", "Cihaz ID");
            _grid.Columns.Add("Hostname", "Makine Adı");
            _grid.Columns.Add("Username", "Aktif Kullanıcı");
            _grid.Columns.Add("Ip", "IP Adresi");
            _grid.Columns.Add("Version", "Sürüm");
            _grid.Columns.Add("Status", "Durum");

            _grid.CellDoubleClick += Grid_CellDoubleClick;

            this.Controls.Add(_grid);
            this.Controls.Add(header);

            _timer = new System.Windows.Forms.Timer { Interval = 10000 };
            _timer.Tick += (s, e) => LoadDevicesAsync();
            _timer.Start();

            this.Load += (s, e) => LoadDevicesAsync();
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string deviceId = _grid.Rows[e.RowIndex].Cells["Id"].Value.ToString();
            string status = _grid.Rows[e.RowIndex].Cells["Status"].Value.ToString();

            if (status != "Online")
            {
                MessageBox.Show("Seçilen cihaz çevrimdışı, bağlantı kurulamaz.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ViewerForm viewer = new ViewerForm(deviceId);
            viewer.Show();
        }

        private async void LoadDevicesAsync()
        {
            try
            {
                _refreshBtn.Enabled = false;
                string url = Program.ServerHttpUrl + "/api/devices";
                
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    string json = await client.DownloadStringTaskAsync(new Uri(url));
                    
                    _grid.Rows.Clear();
                    
                    List<Dictionary<string, string>> items = ParseJsonArray(json);
                    foreach (var dict in items)
                    {
                        string id = dict.ContainsKey("id") ? dict["id"] : "-";
                        string hostname = dict.ContainsKey("hostname") ? dict["hostname"] : "-";
                        string username = dict.ContainsKey("username") ? dict["username"] : "-";
                        string ip = dict.ContainsKey("ip_address") ? dict["ip_address"] : "-";
                        string version = dict.ContainsKey("agent_version") ? dict["agent_version"] : "-";
                        string status = dict.ContainsKey("status") ? dict["status"] : "offline";

                        string displayStatus = (status.ToLower() == "online") ? "Online" : "Offline";
                        
                        int idx = _grid.Rows.Add(id, hostname, username, ip, version, displayStatus);
                        if (displayStatus == "Online")
                        {
                            _grid.Rows[idx].Cells["Status"].Style.ForeColor = Color.LightGreen;
                            _grid.Rows[idx].Cells["Status"].Style.SelectionForeColor = Color.LightGreen;
                        }
                        else
                        {
                            _grid.Rows[idx].Cells["Status"].Style.ForeColor = Color.LightCoral;
                            _grid.Rows[idx].Cells["Status"].Style.SelectionForeColor = Color.LightCoral;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Devices load failed: " + ex.Message);
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        private List<Dictionary<string, string>> ParseJsonArray(string json)
        {
            var list = new List<Dictionary<string, string>>();
            json = json.Trim();
            if (!json.StartsWith("[") || !json.EndsWith("]")) return list;

            string content = json.Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(content)) return list;

            int braceCount = 0;
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    if (braceCount == 0) start = i;
                    braceCount++;
                }
                else if (content[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        string objStr = content.Substring(start, i - start + 1);
                        list.Add(ParseJsonObject(objStr));
                    }
                }
            }
            return list;
        }

        private Dictionary<string, string> ParseJsonObject(string json)
        {
            var dict = new Dictionary<string, string>();
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return dict;

            string inner = json.Substring(1, json.Length - 2).Trim();
            bool insideQuote = false;
            StringBuilder sb = new StringBuilder();
            List<string> tokens = new List<string>();

            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '"') insideQuote = !insideQuote;
                else if ((c == ':' || c == ',') && !insideQuote)
                {
                    tokens.Add(sb.ToString().Trim().Trim('"'));
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString().Trim().Trim('"'));

            for (int i = 0; i < tokens.Count - 1; i += 2)
            {
                dict[tokens[i]] = tokens[i + 1];
            }
            return dict;
        }
    }

    // ── MULTI-TAB REMOTE CONTROL VIEWER ──────────────────────────────────────
    public class ViewerForm : Form
    {
        private string _deviceId;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;

        // UI Tabs & Views
        private TabControl _tabControl;
        
        // Tab 1: Screen View
        private PictureBox _screenBox;
        private ComboBox _monitorSelect;
        private Button _blockInputBtn;
        private Button _blankScreenBtn;
        private Button _elevateUacBtn;
        private Button _audioBtn;
        private bool _isAudioMuted = true;
        private bool _isInputBlocked = false;
        private bool _isScreenBlanked = false;

        // Tab 2: Remote Terminal
        private TextBox _terminalHistory;
        private TextBox _terminalInput;

        // Tab 3: File Explorer
        private ListBox _fileList;
        private TextBox _currentPathText;
        private Button _fileUpBtn;
        private Button _fileDownBtn;
        private Button _folderBackBtn;

        // Tab 4: Process Manager
        private DataGridView _procGrid;
        private Button _killProcBtn;

        // Tab 5: Chat Box
        private TextBox _chatHistory;
        private TextBox _chatInput;

        // State Helpers
        private WavePlayer _audioPlayer;
        private string _lastClipboardText = "";

        public ViewerForm(string deviceId)
        {
            _deviceId = deviceId;
            this.Text = "CoreRemote Uzak Yönetim Paneli - " + deviceId;
            this.Size = new Size(1100, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            // TabControl Layout Setup
            _tabControl = new TabControl { Dock = DockStyle.Fill };
            
            SetupScreenTab();
            SetupFileTab();
            SetupTerminalTab();
            SetupProcessTab();
            SetupChatTab();

            this.Controls.Add(_tabControl);

            this.Load += async (s, e) => await ConnectAsync();
            this.FormClosing += (s, e) => Disconnect();
        }

        private void SetupScreenTab()
        {
            TabPage tab = new TabPage("Uzak Masaüstü");
            tab.BackColor = Color.Black;

            Panel toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(22, 27, 34) };
            
            Label monLbl = new Label { Text = "Monitör:", Width = 50, Location = new Point(10, 12), ForeColor = Color.White };
            _monitorSelect = new ComboBox { Location = new Point(65, 8), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            _monitorSelect.Items.Add("Ekran 1 (Ana)");
            _monitorSelect.SelectedIndex = 0;
            _monitorSelect.SelectedIndexChanged += (s, e) => {
                SendControlCommand("select_monitor", "\"index\":" + _monitorSelect.SelectedIndex);
            };

            _blockInputBtn = new Button 
            { 
                Text = "Giriş Kilitle", 
                Location = new Point(230, 6), 
                Width = 100, 
                BackColor = Color.FromArgb(33, 38, 45), 
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat 
            };
            _blockInputBtn.Click += (s, e) => {
                _isInputBlocked = !_isInputBlocked;
                SendControlCommand("block_input", "\"block\":" + (_isInputBlocked ? "true" : "false"));
                _blockInputBtn.BackColor = _isInputBlocked ? Color.DarkRed : Color.FromArgb(33, 38, 45);
            };

            _blankScreenBtn = new Button 
            { 
                Text = "Ekranı Karart", 
                Location = new Point(340, 6), 
                Width = 100, 
                BackColor = Color.FromArgb(33, 38, 45), 
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat 
            };
            _blankScreenBtn.Click += (s, e) => {
                _isScreenBlanked = !_isScreenBlanked;
                SendControlCommand("blank_screen", "\"blank\":" + (_isScreenBlanked ? "true" : "false"));
                _blankScreenBtn.BackColor = _isScreenBlanked ? Color.DarkSlateGray : Color.FromArgb(33, 38, 45);
            };

            _audioBtn = new Button 
            { 
                Text = "Sesi Aç", 
                Location = new Point(450, 6), 
                Width = 80, 
                BackColor = Color.FromArgb(33, 38, 45), 
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat 
            };
            _audioBtn.Click += (s, e) => {
                _isAudioMuted = !_isAudioMuted;
                SendControlCommand("audio_stream", "\"enable\":" + (!_isAudioMuted ? "true" : "false"));
                _audioBtn.Text = _isAudioMuted ? "Sesi Aç" : "Sesi Kapat";
                _audioBtn.BackColor = !_isAudioMuted ? Color.DarkGreen : Color.FromArgb(33, 38, 45);
            };

            _elevateUacBtn = new Button 
            { 
                Text = "Yönetici Yetkisi İste (UAC)", 
                Location = new Point(540, 6), 
                Width = 160, 
                BackColor = Color.FromArgb(23, 108, 232), 
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat 
            };
            _elevateUacBtn.Click += (s, e) => SendControlCommand("elevate_uac", "");

            toolbar.Controls.Add(monLbl);
            toolbar.Controls.Add(_monitorSelect);
            toolbar.Controls.Add(_blockInputBtn);
            toolbar.Controls.Add(_blankScreenBtn);
            toolbar.Controls.Add(_audioBtn);
            toolbar.Controls.Add(_elevateUacBtn);

            _screenBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            
            // Scaled PictureBox client events
            _screenBox.MouseMove += ScreenBox_MouseMove;
            _screenBox.MouseDown += ScreenBox_MouseDown;
            _screenBox.MouseUp += ScreenBox_MouseUp;
            _screenBox.MouseWheel += ScreenBox_MouseWheel;
            _screenBox.Focus();

            tab.Controls.Add(_screenBox);
            tab.Controls.Add(toolbar);
            _tabControl.TabPages.Add(tab);
        }

        private void SetupFileTab()
        {
            TabPage tab = new TabPage("Dosya Transferi");
            tab.BackColor = Color.FromArgb(22, 27, 34);

            Panel navPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
            _currentPathText = new TextBox { Location = new Point(10, 8), Width = 500, Text = @"C:\", BackColor = Color.FromArgb(13, 17, 23), ForeColor = Color.White };
            
            _folderBackBtn = new Button { Text = "Geri", Location = new Point(520, 6), Width = 60, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _folderBackBtn.Click += (s, e) => {
                string current = _currentPathText.Text;
                var parent = Directory.GetParent(current.EndsWith("\\") ? current.Substring(0, current.Length - 1) : current);
                if (parent != null)
                {
                    _currentPathText.Text = parent.FullName;
                    RequestFileList();
                }
            };

            _fileUpBtn = new Button { Text = "Yükle (Upload)", Location = new Point(590, 6), Width = 110, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _fileUpBtn.Click += (s, e) => UploadFile();

            _fileDownBtn = new Button { Text = "İndir (Download)", Location = new Point(710, 6), Width = 120, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _fileDownBtn.Click += (s, e) => {
                if (_fileList.SelectedItem != null)
                {
                    string selected = _fileList.SelectedItem.ToString();
                    if (selected.StartsWith("FILE | "))
                    {
                        string cleanName = selected.Substring("FILE | ".Length).Split('(')[0].Trim();
                        string fullPath = Path.Combine(_currentPathText.Text, cleanName);
                        SendControlCommand("download_file", "\"path\":\"" + EscJ(fullPath) + "\"");
                        MessageBox.Show("İndirme arka planda başlatıldı.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            };

            navPanel.Controls.Add(_currentPathText);
            navPanel.Controls.Add(_folderBackBtn);
            navPanel.Controls.Add(_fileUpBtn);
            navPanel.Controls.Add(_fileDownBtn);

            _fileList = new ListBox 
            { 
                Dock = DockStyle.Fill, 
                BackColor = Color.FromArgb(13, 17, 23), 
                ForeColor = Color.White, 
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.None
            };
            _fileList.DoubleClick += (s, e) => {
                if (_fileList.SelectedItem != null)
                {
                    string selected = _fileList.SelectedItem.ToString();
                    if (selected.StartsWith("DIR | "))
                    {
                        string folder = selected.Substring("DIR | ".Length);
                        _currentPathText.Text = Path.Combine(_currentPathText.Text, folder);
                        RequestFileList();
                    }
                }
            };

            tab.Controls.Add(_fileList);
            tab.Controls.Add(navPanel);
            _tabControl.TabPages.Add(tab);
        }

        private void SetupTerminalTab()
        {
            TabPage tab = new TabPage("Uzak Konsol");
            tab.BackColor = Color.Black;

            _terminalHistory = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.Black,
                ForeColor = Color.GreenYellow,
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.None
            };

            _terminalInput = new TextBox
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.FixedSingle
            };
            _terminalInput.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter)
                {
                    string cmd = _terminalInput.Text;
                    _terminalInput.Clear();
                    SendControlCommand("term_cmd", "\"cmd\":\"" + EscJ(cmd) + "\"");
                }
            };

            tab.Controls.Add(_terminalHistory);
            tab.Controls.Add(_terminalInput);
            _tabControl.TabPages.Add(tab);
        }

        private void SetupProcessTab()
        {
            TabPage tab = new TabPage("Görev Yöneticisi");
            tab.BackColor = Color.FromArgb(22, 27, 34);

            Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
            Button refreshProc = new Button { Text = "Süreçleri Yenile", Location = new Point(10, 6), Width = 120, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            refreshProc.Click += (s, e) => SendControlCommand("list_processes", "");
            
            _killProcBtn = new Button { Text = "Görevi Sonlandır", Location = new Point(140, 6), Width = 130, BackColor = Color.DarkRed, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _killProcBtn.Click += (s, e) => KillSelectedProcess();

            topPanel.Controls.Add(refreshProc);
            topPanel.Controls.Add(_killProcBtn);

            _procGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(13, 17, 23),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(48, 54, 61),
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            _procGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 38, 45);
            _procGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _procGrid.EnableHeadersVisualStyles = false;

            _procGrid.Columns.Add("Id", "Process ID");
            _procGrid.Columns.Add("Name", "Uygulama Adı");
            _procGrid.Columns.Add("Memory", "Bellek (MB)");

            tab.Controls.Add(_procGrid);
            tab.Controls.Add(topPanel);
            _tabControl.TabPages.Add(tab);
        }

        private void SetupChatTab()
        {
            TabPage tab = new TabPage("Sohbet (Chat)");
            tab.BackColor = Color.FromArgb(13, 17, 23);

            _chatHistory = new TextBox
            {
                Location = new Point(10, 10),
                Size = new Size(500, 400),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9),
                BorderStyle = BorderStyle.None
            };

            _chatInput = new TextBox
            {
                Location = new Point(10, 425),
                Size = new Size(400, 30),
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            _chatInput.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { SendChatMessage(); e.SuppressKeyPress = true; }
            };

            Button sendBtn = new Button
            {
                Text = "Gönder",
                Location = new Point(420, 424),
                Size = new Size(90, 25),
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            sendBtn.Click += (s, e) => SendChatMessage();

            tab.Controls.Add(_chatHistory);
            tab.Controls.Add(_chatInput);
            tab.Controls.Add(sendBtn);
            _tabControl.TabPages.Add(tab);
        }

        // ── WEBSOCKET NETWORKING LOOP ────────────────────────────────────────

        private async Task ConnectAsync()
        {
            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                string url = Program.ServerWsUrl + "/operator-socket?deviceId=" + Uri.EscapeDataString(_deviceId);
                await _ws.ConnectAsync(new Uri(url), _cts.Token);

                // Start inbound packet loop
                Task.Run(async () => await ReceiveLoopAsync());

                // Trigger process list load & directory explorer on start
                SendControlCommand("list_processes", "");
                RequestFileList();

                // Start local clipboard observer thread
                Thread clipboardThread = new Thread(ClipboardWatchLoop);
                clipboardThread.SetApartmentState(ApartmentState.STA);
                clipboardThread.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Bağlantı başarısız: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[1024 * 1024]; // 1MB buffer
            MemoryStream ms = new MemoryStream();

            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    ms.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] packet = ms.ToArray();
                        ms.SetLength(0);

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            ProcessBinaryPacket(packet);
                        }
                        else
                        {
                            string text = Encoding.UTF8.GetString(packet);
                            ProcessTextPacket(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Operator connection error: " + ex.Message);
            }
        }

        private void ProcessBinaryPacket(byte[] packet)
        {
            if (packet.Length < 2) return;
            byte packetType = packet[0];

            if (packetType == 0x01)
            {
                // Canlı Ekran Karesi (JPEG)
                byte[] jpegData = new byte[packet.Length - 1];
                Buffer.BlockCopy(packet, 1, jpegData, 0, jpegData.Length);
                using (MemoryStream stream = new MemoryStream(jpegData))
                {
                    Image img = Image.FromStream(stream);
                    _screenBox.Invoke((MethodInvoker)delegate {
                        Image old = _screenBox.Image;
                        _screenBox.Image = img;
                        if (old != null) old.Dispose();
                    });
                }
            }
            else if (packetType == 0x02)
            {
                // Canlı Hoparlör Sesi (PCM)
                if (_isAudioMuted) return;
                byte[] pcmData = new byte[packet.Length - 1];
                Buffer.BlockCopy(packet, 1, pcmData, 0, pcmData.Length);

                if (_audioPlayer == null)
                {
                    // Core Audio mix default: 44100Hz, stereo, 16bit
                    _audioPlayer = new WavePlayer(44100, 2, 16);
                }
                _audioPlayer.Play(pcmData);
            }
        }

        private void ProcessTextPacket(string json)
        {
            try
            {
                string type = GetJsonValue(json, "type");
                if (type == "webrtc_signal")
                {
                    string data = GetJsonValue(json, "data");
                    string action = GetJsonValue(data, "action");
                    string text = GetJsonValue(data, "text");

                    switch (action)
                    {
                        case "term_output":
                            _terminalHistory.Invoke((MethodInvoker)delegate {
                                _terminalHistory.AppendText(text);
                            });
                            break;

                        case "process_list":
                            _procGrid.Invoke((MethodInvoker)delegate {
                                _procGrid.Rows.Clear();
                                string[] procs = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var p in procs)
                                {
                                    string[] info = p.Split('|');
                                    if (info.Length == 3)
                                    {
                                        _procGrid.Rows.Add(info[0], info[1], info[2]);
                                    }
                                }
                            });
                            break;

                        case "file_list":
                            _fileList.Invoke((MethodInvoker)delegate {
                                _fileList.Items.Clear();
                                string[] items = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var item in items)
                                {
                                    string[] info = item.Split('|');
                                    if (info.Length >= 2)
                                    {
                                        if (info[0] == "DIR") _fileList.Items.Add("DIR | " + info[1]);
                                        else _fileList.Items.Add("FILE | " + info[1] + " (" + (long.Parse(info[2]) / 1024) + " KB)");
                                    }
                                }
                            });
                            break;

                        case "file_list_error":
                            MessageBox.Show("Klasör okunurken hata oluştu: " + text, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;

                        case "chat_msg":
                            _chatHistory.Invoke((MethodInvoker)delegate {
                                _chatHistory.AppendText("Kullanıcı: " + text + "\r\n\r\n");
                            });
                            break;

                        case "clipboard_sync":
                            SetLocalClipboardText(text);
                            break;
                    }
                }
                else if (type == "file_download_chunk")
                {
                    string name = GetJsonValue(json, "name");
                    string base64Data = GetJsonValue(json, "bytes");
                    bool isLast = GetJsonValue(json, "isLast").ToLower() == "true";

                    string destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CoreRemoteDownloads");
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                    string destPath = Path.Combine(destDir, name);

                    byte[] dataBytes = Convert.FromBase64String(base64Data);
                    using (FileStream fs = new FileStream(destPath, FileMode.Append, FileAccess.Write))
                    {
                        fs.Write(dataBytes, 0, dataBytes.Length);
                    }

                    if (isLast)
                    {
                        MessageBox.Show("Dosya başarıyla Masaüstündeki CoreRemoteDownloads klasörüne indirildi!", "İndirme Tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else if (type == "telemetry")
                {
                    // Process multi-monitor list update
                    string telemetryData = GetJsonValue(json, "data");
                    string monitors = GetJsonValue(telemetryData, "monitors");
                    if (!string.IsNullOrEmpty(monitors))
                    {
                        _monitorSelect.Invoke((MethodInvoker)delegate {
                            _monitorSelect.Items.Clear();
                            string[] list = monitors.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var mon in list)
                            {
                                string[] info = mon.Split('|');
                                _monitorSelect.Items.Add(info[0] + " " + info[1]);
                            }
                            if (_monitorSelect.Items.Count > 0) _monitorSelect.SelectedIndex = 0;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Inbound text parse error: " + ex.Message);
            }
        }

        // ── CONTROL INPUT COORDINATES TRANSLATING ───────────────────────────

        private Point TranslateCoordinates(Point p)
        {
            if (_screenBox.Image == null) return p;

            int w_i = _screenBox.Image.Width;
            int h_i = _screenBox.Image.Height;
            int w_c = _screenBox.Width;
            int h_c = _screenBox.Height;

            double ratioX = (double)w_c / w_i;
            double ratioY = (double)h_c / h_i;
            double ratio = Math.Min(ratioX, ratioY);

            int imgWidth = (int)(w_i * ratio);
            int imgHeight = (int)(h_i * ratio);

            int imgX = (w_c - imgWidth) / 2;
            int imgY = (h_c - imgHeight) / 2;

            int relativeX = p.X - imgX;
            int relativeY = p.Y - imgY;

            // Clamp coordinate bounds inside PictureBox image frame boundaries
            relativeX = Math.Max(0, Math.Min(imgWidth, relativeX));
            relativeY = Math.Max(0, Math.Min(imgHeight, relativeY));

            double rx = (double)relativeX / imgWidth;
            double ry = (double)relativeY / imgHeight;

            return new Point((int)(rx * 65535), (int)(ry * 65535));
        }

        private void ScreenBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_screenBox.Image == null) return;
            Point p = TranslateCoordinates(e.Location);
            double rx = (double)p.X / 65535.0;
            double ry = (double)p.Y / 65535.0;

            string json = "{" +
                "\"action\":\"mousemove\"," +
                "\"x\":" + rx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"y\":" + ry.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
            "}";
            SendInputSignal(json);
        }

        private void ScreenBox_MouseDown(object sender, MouseEventArgs e)
        {
            int btn = (e.Button == MouseButtons.Right) ? 2 : 0;
            string json = "{\"action\":\"mousedown\",\"button\":" + btn + "}";
            SendInputSignal(json);
        }

        private void ScreenBox_MouseUp(object sender, MouseEventArgs e)
        {
            int btn = (e.Button == MouseButtons.Right) ? 2 : 0;
            string json = "{\"action\":\"mouseup\",\"button\":" + btn + "}";
            SendInputSignal(json);
        }

        private void ScreenBox_MouseWheel(object sender, MouseEventArgs e)
        {
            // Optional wheel handling
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_tabControl.SelectedTab.Text == "Uzak Masaüstü")
            {
                // KeyDown
                int vk = (int)(keyData & Keys.KeyCode);
                SendInputSignal("{\"action\":\"keydown\",\"vk\":" + vk + "}");
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_tabControl.SelectedTab.Text == "Uzak Masaüstü")
            {
                int vk = (int)e.KeyCode;
                SendInputSignal("{\"action\":\"keyup\",\"vk\":" + vk + "}");
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        // ── FEATURE ACTIONS DISPATCHERS ──────────────────────────────────────

        private async void SendInputSignal(string jsonPayload)
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
                }
            }
            catch {}
        }

        private async void SendControlCommand(string action, string additionalJson)
        {
            try
            {
                string inner = "\"action\":\"" + action + "\"";
                if (!string.IsNullOrEmpty(additionalJson)) inner += "," + additionalJson;

                string payload = "{" +
                    "\"type\":\"webrtc_signal\"," +
                    "\"data\":{" + inner + "}" +
                "}";

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
                }
            }
            catch {}
        }

        private void RequestFileList()
        {
            SendControlCommand("list_files", "\"path\":\"" + EscJ(_currentPathText.Text) + "\"");
        }

        private async void UploadFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string localPath = ofd.FileName;
                    string name = Path.GetFileName(localPath);
                    byte[] buffer = new byte[65536];
                    int bytesRead;

                    using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                    {
                        bool isFirst = true;
                        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            byte[] chunk = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                            string base64Data = Convert.ToBase64String(chunk);
                            string payload = "{" +
                                "\"type\":\"file_chunk\"," +
                                "\"name\":\"" + EscJ(name) + "\"," +
                                "\"bytes\":\"" + base64Data + "\"," +
                                "\"isFirst\":" + (isFirst ? "true" : "false") +
                            "}";

                            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                            if (_ws != null && _ws.State == WebSocketState.Open)
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(payloadBytes), WebSocketMessageType.Text, true, _cts.Token);
                            }
                            isFirst = false;
                        }
                    }

                    MessageBox.Show("Dosya karşı bilgisayara başarıyla yüklendi!", "Yükleme Tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RequestFileList(); // Refresh directory view
                }
            }
        }

        private void KillSelectedProcess()
        {
            if (_procGrid.SelectedRows.Count > 0)
            {
                string pid = _procGrid.SelectedRows[0].Cells["Id"].Value.ToString();
                SendControlCommand("kill_process", "\"pid\":" + pid);
            }
        }

        private void SendChatMessage()
        {
            string txt = _chatInput.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            _chatInput.Clear();

            _chatHistory.AppendText("Siz: " + txt + "\r\n\r\n");
            SendControlCommand("chat_msg", "\"text\":\"" + EscJ(txt) + "\"");
        }

        // ── CLIPBOARD SYNCHRONIZATION ───────────────────────────────────────
        private void ClipboardWatchLoop()
        {
            while (_ws != null && _ws.State == WebSocketState.Open)
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
                        SendControlCommand("clipboard_sync", "\"text\":\"" + EscJ(currentText) + "\"");
                    }
                }
                catch {}
                Thread.Sleep(1000);
            }
        }

        private void SetLocalClipboardText(string text)
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

        private void Disconnect()
        {
            try
            {
                if (_cts != null) _cts.Cancel();
                if (_ws != null) _ws.Dispose();
                if (_audioPlayer != null) _audioPlayer.Dispose();
            }
            catch {}
        }

        private string EscJ(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "").Replace("\t", " ");
        }

        private string GetJsonValue(string json, string key)
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

    // ── NATIVE waveOut AUDIO PLAYER INTEROP ──────────────────────────────────
    internal class WavePlayer : IDisposable
    {
        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        
        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
        
        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
        
        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, ref WAVEHDR pwh, uint cbwh);
        
        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hwo);
        
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }
        
        private IntPtr _hWaveOut = IntPtr.Zero;
        
        public WavePlayer(int samplesPerSec, short channels, short bitsPerSample)
        {
            try
            {
                WAVEFORMATEX format = new WAVEFORMATEX
                {
                    wFormatTag = 1, // WAVE_FORMAT_PCM
                    nChannels = channels,
                    nSamplesPerSec = samplesPerSec,
                    wBitsPerSample = bitsPerSample,
                    nBlockAlign = (short)(channels * (bitsPerSample / 8)),
                    nAvgBytesPerSec = samplesPerSec * (channels * (bitsPerSample / 8)),
                    cbSize = 0
                };
                waveOutOpen(out _hWaveOut, 0, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            }
            catch {}
        }
        
        public void Play(byte[] data)
        {
            if (_hWaveOut == IntPtr.Zero) return;
            
            try
            {
                IntPtr bufferPtr = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, bufferPtr, data.Length);
                
                WAVEHDR hdr = new WAVEHDR
                {
                    lpData = bufferPtr,
                    dwBufferLength = (uint)data.Length,
                    dwFlags = 0
                };
                
                waveOutPrepareHeader(_hWaveOut, ref hdr, (uint)Marshal.SizeOf(hdr));
                waveOutWrite(_hWaveOut, ref hdr, (uint)Marshal.SizeOf(hdr));
                
                // Asynchronously dispose play buffer after short play duration block
                Task.Run(() => {
                    Thread.Sleep(600);
                    try
                    {
                        waveOutUnprepareHeader(_hWaveOut, ref hdr, (uint)Marshal.SizeOf(hdr));
                        Marshal.FreeHGlobal(bufferPtr);
                    }
                    catch {}
                });
            }
            catch {}
        }

        public void Dispose()
        {
            try
            {
                if (_hWaveOut != IntPtr.Zero)
                {
                    waveOutClose(_hWaveOut);
                    _hWaveOut = IntPtr.Zero;
                }
            }
            catch {}
        }
    }
}
