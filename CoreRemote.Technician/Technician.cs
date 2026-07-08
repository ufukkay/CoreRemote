using System;
using System.Collections.Concurrent;
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
        public static string Version = "1.1.0"; // Current local version

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                SetProcessDPIAware();
            }
            catch {}

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            RegisterCustomProtocol();

            // Run self-update check in background
            Task.Run(async () => await CheckForUpdatesAsync());

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

            Application.Run(new MainForm());
        }

        private static async Task CheckForUpdatesAsync()
        {
            try
            {
                // Delay slightly to let the UI load
                await Task.Delay(2000);

                string url = ServerHttpUrl + "/api/technician/version";
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    string json = await client.DownloadStringTaskAsync(new Uri(url));
                    string serverVer = GetJsonValue(json, "version");
                    
                    if (!string.IsNullOrEmpty(serverVer) && serverVer.Trim() != Version)
                    {
                        Form activeForm = Form.ActiveForm;
                        if (activeForm != null && activeForm.InvokeRequired)
                        {
                            activeForm.Invoke((MethodInvoker)delegate {
                                PromptUpdate(serverVer);
                            });
                        }
                        else
                        {
                            PromptUpdate(serverVer);
                        }
                    }
                }
            }
            catch {}
        }

        private static void PromptUpdate(string serverVer)
        {
            var res = MessageBox.Show("Yeni bir Teknisyen Uygulaması sürümü mevcut (v" + serverVer + "). Şimdi güncellemek ister misiniz?", "Güncelleme Mevcut", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (res == DialogResult.Yes)
            {
                Task.Run(async () => await TriggerSelfUpdateAsync());
            }
        }

        private static async Task TriggerSelfUpdateAsync()
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                string installDir = Path.GetDirectoryName(currentExePath);
                string newExePath = Path.Combine(installDir, "CoreRemoteViewer_new.exe");
                string batPath = Path.Combine(installDir, "update_tech.bat");

                string downloadUrl = ServerHttpUrl + "/api/builder/download-technician";
                using (WebClient wc = new WebClient())
                {
                    await wc.DownloadFileTaskAsync(new Uri(downloadUrl), newExePath);
                }

                if (File.Exists(newExePath) && new FileInfo(newExePath).Length > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("@echo off");
                    sb.AppendLine("ping 127.0.0.1 -n 3 > nul");
                    sb.AppendLine("move /y \"" + newExePath + "\" \"" + currentExePath + "\"");
                    sb.AppendLine("start \"\" \"" + currentExePath + "\"");
                    sb.AppendLine("del \"%~f0\"");

                    File.WriteAllText(batPath, sb.ToString());

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + batPath + "\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Güncelleme başarısız: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
            this.Size = new Size(900, 550);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            Panel header = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(15, 10, 15, 10), BackColor = Color.FromArgb(22, 27, 34) };
            Label title = new Label 
            { 
                Text = "⚡ CoreRemote - Uzak Yönetim Paneli", 
                Font = new Font("Segoe UI", 12, FontStyle.Bold), 
                ForeColor = Color.FromArgb(88, 166, 255),
                Dock = DockStyle.Left,
                Width = 400,
                TextAlign = ContentAlignment.MiddleLeft
            };
            
            _refreshBtn = new Button
            {
                Text = "Listeyi Yenile",
                Dock = DockStyle.Right,
                Width = 120,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(201, 209, 217),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _refreshBtn.FlatAppearance.BorderSize = 1;
            _refreshBtn.Click += (s, e) => LoadDevicesAsync();

            header.Controls.Add(title);
            header.Controls.Add(_refreshBtn);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(13, 17, 23),
                ForeColor = Color.FromArgb(201, 209, 217),
                GridColor = Color.FromArgb(30, 35, 41),
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 35 }
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(22, 27, 34);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(201, 209, 217);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _grid.ColumnHeadersHeight = 35;
            _grid.DefaultCellStyle.BackColor = Color.FromArgb(13, 17, 23);
            _grid.DefaultCellStyle.ForeColor = Color.FromArgb(201, 209, 217);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 111, 235);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 9);
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
                            _grid.Rows[idx].Cells["Status"].Style.ForeColor = Color.FromArgb(86, 211, 100);
                            _grid.Rows[idx].Cells["Status"].Style.SelectionForeColor = Color.FromArgb(86, 211, 100);
                        }
                        else
                        {
                            _grid.Rows[idx].Cells["Status"].Style.ForeColor = Color.FromArgb(248, 81, 73);
                            _grid.Rows[idx].Cells["Status"].Style.SelectionForeColor = Color.FromArgb(248, 81, 73);
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

    // ── PREMIUM TABBED REMOTE VIEWER ─────────────────────────────────────────
    public class ViewerForm : Form
    {
        private string _deviceId;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private DateTime _lastMouseMoveTime = DateTime.MinValue;
        private volatile bool _frameProcessing = false;
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();

        // UI Components
        private Panel _sidebar;
        private TabControl _tabControl;
        
        // Navigation Buttons
        private Button[] _navButtons;
        private int _activeTabIndex = 0;

        // Tab 1: Screen View
        private PictureBox _screenBox;
        private Panel _screenScrollPanel;
        private ComboBox _monitorSelect;
        private ComboBox _viewModeSelect;
        private Button _blockInputBtn;
        private Button _blankScreenBtn;
        private Button _audioBtn;
        private bool _isAudioMuted = true;
        private bool _isInputBlocked = false;
        private bool _isScreenBlanked = false;

        // Tab 2: File Explorer
        private ListBox _fileList;
        private TextBox _currentPathText;
        private Button _fileUpBtn;
        private Button _fileDownBtn;
        private Button _folderBackBtn;

        // Tab 3: Remote Terminal
        private TextBox _terminalHistory;
        private TextBox _terminalInput;

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
            this.Text = "CoreRemote Canlı Kontrol Paneli - Cihaz: " + deviceId;
            this.Size = new Size(1200, 850);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            InitializeLayout();

            this.Load += async (s, e) => await ConnectAsync();
            this.FormClosing += (s, e) => Disconnect();
        }

        private bool _sidebarExpanded = false;
        private Button _toggleSidebarBtn;

        private void InitializeLayout()
        {
            // Left Sidebar Setup (Initial narrow width: 60px like AnyDesk)
            _sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 60,
                BackColor = Color.FromArgb(22, 27, 34),
                Padding = new Padding(0, 5, 0, 10)
            };

            // Hamburger menu button at the top
            _toggleSidebarBtn = new Button
            {
                Text = "  ☰",
                Dock = DockStyle.Top,
                Height = 45,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(88, 166, 255),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _toggleSidebarBtn.FlatAppearance.BorderSize = 0;
            _toggleSidebarBtn.Click += (s, e) => ToggleSidebar();
            _sidebar.Controls.Add(_toggleSidebarBtn);

            // Separator line
            Panel separator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(48, 54, 61) };
            _sidebar.Controls.Add(separator);
            _sidebar.Controls.SetChildIndex(separator, 0);

            // Navigation buttons creation
            string[] tabNames = { "🖥️ Canlı Ekran", "📁 Dosya Gezgini", "⚙️ Görev Yöneticisi", "💻 Uzak Konsol", "💬 Sohbet" };
            _navButtons = new Button[tabNames.Length];
            
            Panel navContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
            _sidebar.Controls.Add(navContainer);
            _sidebar.Controls.SetChildIndex(navContainer, 0); // Put under separator

            for (int i = tabNames.Length - 1; i >= 0; i--)
            {
                int index = i;
                Button btn = new Button
                {
                    Text = tabNames[i].Substring(0, 2), // Show only icon representation at first
                    Tag = tabNames[i], // Store full name in Tag
                    Dock = DockStyle.Top,
                    Height = 50,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.FromArgb(201, 209, 217),
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 10, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) => SwitchTab(index);

                // Hover styling hooks
                btn.MouseEnter += (s, e) => {
                    if (_activeTabIndex != index) btn.BackColor = Color.FromArgb(33, 38, 45);
                };
                btn.MouseLeave += (s, e) => {
                    if (_activeTabIndex != index) btn.BackColor = Color.Transparent;
                };

                _navButtons[i] = btn;
                navContainer.Controls.Add(btn);
            }

            this.Controls.Add(_sidebar);

            // TabControl (We hide standard headers for custom look)
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Buttons,
                ItemSize = new Size(0, 1),
                SizeMode = TabSizeMode.Fixed
            };

            SetupScreenTab();
            SetupFileTab();
            SetupProcessTab();
            SetupTerminalTab();
            SetupChatTab();

            this.Controls.Add(_tabControl);
            _tabControl.BringToFront();

            // Set default view active button color
            SwitchTab(0);
        }

        private void ToggleSidebar()
        {
            _sidebarExpanded = !_sidebarExpanded;
            _sidebar.Width = _sidebarExpanded ? 220 : 60;

            for (int i = 0; i < _navButtons.Length; i++)
            {
                string fullName = _navButtons[i].Tag.ToString();
                if (_sidebarExpanded)
                {
                    _navButtons[i].Text = "   " + fullName;
                    _navButtons[i].TextAlign = ContentAlignment.MiddleLeft;
                }
                else
                {
                    _navButtons[i].Text = fullName.Substring(0, 2);
                    _navButtons[i].TextAlign = ContentAlignment.MiddleCenter;
                }
            }

            _toggleSidebarBtn.Text = _sidebarExpanded ? "  ☰   Kapat" : "  ☰";
        }

        private void SwitchTab(int index)
        {
            _activeTabIndex = index;
            _tabControl.SelectedIndex = index;

            for (int i = 0; i < _navButtons.Length; i++)
            {
                if (i == index)
                {
                    _navButtons[i].BackColor = Color.FromArgb(31, 111, 235);
                    _navButtons[i].ForeColor = Color.White;
                }
                else
                {
                    _navButtons[i].BackColor = Color.Transparent;
                    _navButtons[i].ForeColor = Color.FromArgb(201, 209, 217);
                }
            }
        }

        private void SetupScreenTab()
        {
            TabPage tab = new TabPage("Masaüstü");
            tab.BackColor = Color.Black;

            // Custom Styled Toolbar Header
            Panel toolbar = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(22, 27, 34), Padding = new Padding(10, 5, 10, 5) };
            
            // Monitor Selector Dropdown
            Label monLbl = new Label { Text = "Ekran:", Width = 45, Location = new Point(10, 15), ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _monitorSelect = new ComboBox { Location = new Point(58, 12), Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _monitorSelect.Items.Add("Ekran 1");
            _monitorSelect.SelectedIndex = 0;
            _monitorSelect.SelectedIndexChanged += (s, e) => {
                SendControlCommand("select_monitor", "\"index\":" + _monitorSelect.SelectedIndex);
            };

            // View Scaling Mode Selector Dropdown (stretch, zoom, original size scroll)
            Label scaleLbl = new Label { Text = "Ölçek:", Width = 45, Location = new Point(180, 15), ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _viewModeSelect = new ComboBox { Location = new Point(228, 12), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            _viewModeSelect.Items.Add("Ekrana Sığdır (Fit)");
            _viewModeSelect.Items.Add("Genişlet (Stretch)");
            _viewModeSelect.Items.Add("Gerçek Boyut (Scroll)");
            _viewModeSelect.SelectedIndex = 0;
            _viewModeSelect.SelectedIndexChanged += (s, e) => UpdateViewerResolutionMode();

            // Action Control Buttons
            _blockInputBtn = CreateToolbarButton("Giriş Kilitle", 395, () => {
                _isInputBlocked = !_isInputBlocked;
                SendControlCommand("block_input", "\"block\":" + (_isInputBlocked ? "true" : "false"));
                _blockInputBtn.BackColor = _isInputBlocked ? Color.FromArgb(248, 81, 73) : Color.FromArgb(33, 38, 45);
            });

            _blankScreenBtn = CreateToolbarButton("Ekranı Karart", 500, () => {
                _isScreenBlanked = !_isScreenBlanked;
                SendControlCommand("blank_screen", "\"blank\":" + (_isScreenBlanked ? "true" : "false"));
                _blankScreenBtn.BackColor = _isScreenBlanked ? Color.FromArgb(248, 81, 73) : Color.FromArgb(33, 38, 45);
            });

            _audioBtn = CreateToolbarButton("Sesi Aç", 605, () => {
                _isAudioMuted = !_isAudioMuted;
                SendControlCommand("audio_stream", "\"enable\":" + (!_isAudioMuted ? "true" : "false"));
                _audioBtn.Text = _isAudioMuted ? "Sesi Aç" : "Sesi Kapat";
                _audioBtn.BackColor = !_isAudioMuted ? Color.FromArgb(86, 211, 100) : Color.FromArgb(33, 38, 45);
            });

            toolbar.Controls.Add(monLbl);
            toolbar.Controls.Add(_monitorSelect);
            toolbar.Controls.Add(scaleLbl);
            toolbar.Controls.Add(_viewModeSelect);
            toolbar.Controls.Add(_blockInputBtn);
            toolbar.Controls.Add(_blankScreenBtn);
            toolbar.Controls.Add(_audioBtn);

            // Container ScrollPanel wrapping PictureBox screen viewer
            _screenScrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                AutoScroll = true
            };

            _screenBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            
            _screenBox.MouseMove += ScreenBox_MouseMove;
            _screenBox.MouseDown += ScreenBox_MouseDown;
            _screenBox.MouseUp += ScreenBox_MouseUp;
            _screenBox.MouseWheel += ScreenBox_MouseWheel;
            _screenBox.MouseClick += (s, e) => { _screenBox.Focus(); };
            _screenBox.Focus();

            // Restore focus to screen after monitor move or resize
            this.LocationChanged += (s, e) => { if (_screenBox.CanFocus) _screenBox.Focus(); };
            this.Activated += (s, e) => { if (_screenBox.CanFocus) _screenBox.Focus(); };

            _screenScrollPanel.Controls.Add(_screenBox);

            tab.Controls.Add(_screenScrollPanel);
            tab.Controls.Add(toolbar);
            _tabControl.TabPages.Add(tab);
        }

        private Button CreateToolbarButton(string text, int x, Action onClick)
        {
            Button btn = new Button
            {
                Text = text,
                Location = new Point(x, 10),
                Width = 100,
                Height = 30,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void UpdateViewerResolutionMode()
        {
            if (_viewModeSelect.SelectedIndex == 0)
            {
                // Zoom / Fit mode
                _screenBox.Dock = DockStyle.Fill;
                _screenBox.SizeMode = PictureBoxSizeMode.Zoom;
            }
            else if (_viewModeSelect.SelectedIndex == 1)
            {
                // Stretch mode
                _screenBox.Dock = DockStyle.Fill;
                _screenBox.SizeMode = PictureBoxSizeMode.StretchImage;
            }
            else
            {
                // Scroll / Original Size mode
                _screenBox.Dock = DockStyle.None;
                _screenBox.SizeMode = PictureBoxSizeMode.Normal;
                if (_screenBox.Image != null)
                {
                    _screenBox.Size = _screenBox.Image.Size;
                }
            }
        }

        private void SetupFileTab()
        {
            TabPage tab = new TabPage("Dosyalar");
            tab.BackColor = Color.FromArgb(13, 17, 23);

            Panel navPanel = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10), BackColor = Color.FromArgb(22, 27, 34) };
            _currentPathText = new TextBox { Location = new Point(10, 12), Width = 550, Text = @"C:\", BackColor = Color.FromArgb(13, 17, 23), ForeColor = Color.White, Font = new Font("Segoe UI", 9), BorderStyle = BorderStyle.FixedSingle };
            
            _folderBackBtn = new Button { Text = "Geri", Location = new Point(570, 10), Width = 60, Height = 28, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _folderBackBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _folderBackBtn.Click += (s, e) => {
                string current = _currentPathText.Text;
                var parent = Directory.GetParent(current.EndsWith("\\") ? current.Substring(0, current.Length - 1) : current);
                if (parent != null)
                {
                    _currentPathText.Text = parent.FullName;
                    RequestFileList();
                }
            };

            _fileUpBtn = new Button { Text = "Yükle (Upload)", Location = new Point(640, 10), Width = 110, Height = 28, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _fileUpBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _fileUpBtn.Click += (s, e) => UploadFile();

            _fileDownBtn = new Button { Text = "İndir (Download)", Location = new Point(760, 10), Width = 120, Height = 28, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _fileDownBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
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
                BorderStyle = BorderStyle.None,
                ItemHeight = 25
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

        private void SetupProcessTab()
        {
            TabPage tab = new TabPage("Süreçler");
            tab.BackColor = Color.FromArgb(13, 17, 23);

            Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10), BackColor = Color.FromArgb(22, 27, 34) };
            Button refreshProc = new Button { Text = "Süreçleri Yenile", Location = new Point(10, 10), Width = 130, Height = 30, BackColor = Color.FromArgb(33, 38, 45), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            refreshProc.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            refreshProc.Click += (s, e) => SendControlCommand("list_processes", "");
            
            _killProcBtn = new Button { Text = "Görevi Sonlandır", Location = new Point(150, 10), Width = 130, Height = 30, BackColor = Color.FromArgb(248, 81, 73), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            _killProcBtn.FlatAppearance.BorderSize = 0;
            _killProcBtn.Click += (s, e) => KillSelectedProcess();

            topPanel.Controls.Add(refreshProc);
            topPanel.Controls.Add(_killProcBtn);

            _procGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(13, 17, 23),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(30, 35, 41),
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.None,
                RowTemplate = { Height = 30 }
            };
            _procGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(22, 27, 34);
            _procGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _procGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _procGrid.ColumnHeadersHeight = 30;
            _procGrid.DefaultCellStyle.BackColor = Color.FromArgb(13, 17, 23);
            _procGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 111, 235);
            _procGrid.EnableHeadersVisualStyles = false;

            _procGrid.Columns.Add("Id", "Process ID");
            _procGrid.Columns.Add("Name", "Uygulama Adı");
            _procGrid.Columns.Add("Memory", "Bellek (MB)");

            tab.Controls.Add(_procGrid);
            tab.Controls.Add(topPanel);
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
                BorderStyle = BorderStyle.FixedSingle,
                Height = 30
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

        private void SetupChatTab()
        {
            TabPage tab = new TabPage("Sohbet (Chat)");
            tab.BackColor = Color.FromArgb(13, 17, 23);

            _chatHistory = new TextBox
            {
                Location = new Point(20, 20),
                Size = new Size(700, 500),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10),
                BorderStyle = BorderStyle.None
            };

            _chatInput = new TextBox
            {
                Location = new Point(20, 545),
                Size = new Size(580, 40),
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10),
                BorderStyle = BorderStyle.FixedSingle
            };
            _chatInput.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { SendChatMessage(); e.SuppressKeyPress = true; }
            };

            Button sendBtn = new Button
            {
                Text = "Gönder",
                Location = new Point(610, 544),
                Size = new Size(110, 30),
                BackColor = Color.FromArgb(31, 111, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            sendBtn.FlatAppearance.BorderSize = 0;
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

                Task.Run(async () => await ReceiveLoopAsync());
                Task.Run(async () => await SendLoopAsync());

                QueueControlCommand("list_processes", "");
                RequestFileList();

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
            byte[] buffer = new byte[1024 * 1024];
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
                            // Only process if NOT already rendering a frame — drop to avoid UI thread saturation
                            if (!_frameProcessing)
                            {
                                _frameProcessing = true;
                                byte[] packetCopy = packet;
                                Task.Run(() => {
                                    try { ProcessBinaryPacket(packetCopy); }
                                    finally { _frameProcessing = false; }
                                });
                            }
                            // else: drop this frame — the previous one is still being decoded
                        }
                        else
                        {
                            string text = Encoding.UTF8.GetString(packet);
                            Task.Run(() => ProcessTextPacket(text));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Operator connection error: " + ex.Message);
            }

            // Auto-reconnect: if closed unexpectedly and form is still alive, reconnect
            if (!this.IsDisposed && !_cts.IsCancellationRequested)
            {
                Console.WriteLine("Connection lost. Attempting reconnect in 2 seconds...");
                await Task.Delay(2000);
                try
                {
                    if (_ws != null) _ws.Dispose();
                    _ws = new ClientWebSocket();
                    string url = Program.ServerWsUrl + "/operator-socket?deviceId=" + Uri.EscapeDataString(_deviceId);
                    await _ws.ConnectAsync(new Uri(url), _cts.Token);
                    Console.WriteLine("Reconnected successfully.");
                    // Restart streaming
                    SendControlCommand("list_processes", "");
                    // Loop again
                    await ReceiveLoopAsync();
                }
                catch (Exception rex)
                {
                    Console.WriteLine("Reconnect failed: " + rex.Message);
                }
            }
        }

        private void ProcessBinaryPacket(byte[] packet)
        {
            try
            {
                if (packet.Length < 2) return;
                byte packetType = packet[0];

                if (packetType == 0x01)
                {
                    byte[] jpegData = new byte[packet.Length - 1];
                    Buffer.BlockCopy(packet, 1, jpegData, 0, jpegData.Length);
                    Bitmap bmp = null;
                    try
                    {
                        using (MemoryStream stream = new MemoryStream(jpegData))
                        using (Image tempImg = Image.FromStream(stream))
                        {
                            bmp = new Bitmap(tempImg);
                        }
                    }
                    catch { return; }

                    Bitmap frameBmp = bmp;
                    // Invoke on the FORM (not the PictureBox) - Form handle survives monitor moves
                    if (this.IsHandleCreated && !this.IsDisposed)
                    {
                        this.BeginInvoke((MethodInvoker)delegate {
                            if (this.IsDisposed || _screenBox.IsDisposed) { frameBmp.Dispose(); return; }
                            try
                            {
                                Image old = _screenBox.Image;
                                _screenBox.Image = frameBmp;
                                if (old != null) old.Dispose();

                                // Auto-scroll mode sizing update if selected
                                if (_viewModeSelect.SelectedIndex == 2)
                                {
                                    _screenBox.Size = frameBmp.Size;
                                }
                            }
                            catch { frameBmp.Dispose(); }
                        });
                    }
                    else
                    {
                        bmp.Dispose(); // Handle not ready yet — drop this frame
                    }
                }
                else if (packetType == 0x02)
                {
                    if (_isAudioMuted) return;
                    byte[] pcmData = new byte[packet.Length - 1];
                    Buffer.BlockCopy(packet, 1, pcmData, 0, pcmData.Length);

                    if (_audioPlayer == null)
                    {
                        _audioPlayer = new WavePlayer(44100, 2, 16);
                    }
                    _audioPlayer.Play(pcmData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing binary packet: " + ex.Message);
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

        // ── COORDINATE TRANSLATING ON ZOOM/STRETCH/NORMAL MODES ─────────────

        private Point TranslateCoordinates(Point p)
        {
            if (_screenBox.Image == null) return p;

            if (_screenBox.SizeMode == PictureBoxSizeMode.StretchImage)
            {
                double rx = (double)p.X / _screenBox.Width;
                double ry = (double)p.Y / _screenBox.Height;
                return new Point((int)(rx * 65535), (int)(ry * 65535));
            }
            else if (_screenBox.SizeMode == PictureBoxSizeMode.Normal)
            {
                // Normal mode: 1:1 original resolution with scrollbars
                double rx = (double)p.X / _screenBox.Width;
                double ry = (double)p.Y / _screenBox.Height;
                return new Point((int)(rx * 65535), (int)(ry * 65535));
            }
            else // Zoom mode (Fit)
            {
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

                relativeX = Math.Max(0, Math.Min(imgWidth, relativeX));
                relativeY = Math.Max(0, Math.Min(imgHeight, relativeY));

                double rx = (double)relativeX / imgWidth;
                double ry = (double)relativeY / imgHeight;

                return new Point((int)(rx * 65535), (int)(ry * 65535));
            }
        }

        private void ScreenBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_screenBox.Image == null) return;
            if ((DateTime.Now - _lastMouseMoveTime).TotalMilliseconds < 30) return;
            _lastMouseMoveTime = DateTime.Now;

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
            // Windows Delta = 120 per notch; tarayıcı standardına normalize ediyoruz
            int normalizedDelta = -(e.Delta / 120) * 100;
            if (normalizedDelta == 0) normalizedDelta = (e.Delta > 0) ? -100 : 100;
            string json = "{\"action\":\"scroll\",\"deltaY\":" + normalizedDelta + "}";
            SendInputSignal(json);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_tabControl.SelectedTab.Text == "Masaüstü")
            {
                int vk = (int)(keyData & Keys.KeyCode);
                SendInputSignal("{\"action\":\"keydown\",\"vk\":" + vk + "}");
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_tabControl.SelectedTab.Text == "Masaüstü")
            {
                int vk = (int)e.KeyCode;
                SendInputSignal("{\"action\":\"keyup\",\"vk\":" + vk + "}");
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        // ── FEATURE ACTIONS DISPATCHERS ──────────────────────────────────────

        private void SendInputSignal(string jsonPayload)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            _sendQueue.Enqueue(Encoding.UTF8.GetBytes(jsonPayload));
        }

        private void QueueControlCommand(string action, string additionalJson)
        {
            string inner = "\"action\":\"" + action + "\"";
            if (!string.IsNullOrEmpty(additionalJson)) inner += "," + additionalJson;
            string payload = "{" +
                "\"type\":\"webrtc_signal\"," +
                "\"data\":{" + inner + "}" +
            "}";
            _sendQueue.Enqueue(Encoding.UTF8.GetBytes(payload));
        }

        private void SendControlCommand(string action, string additionalJson)
        {
            QueueControlCommand(action, additionalJson);
        }

        // Dedicated sender loop — ONLY this thread ever calls _ws.SendAsync
        private async Task SendLoopAsync()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    byte[] data;
                    if (_sendQueue.TryDequeue(out data))
                    {
                        try
                        {
                            if (_ws != null && _ws.State == WebSocketState.Open)
                            {
                                await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, _cts.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Technician Send Error: " + ex.Message);
                        }
                    }
                    else
                    {
                        await Task.Delay(5); // Small wait when queue empty
                    }
                }
            }
            catch {}
        }

        private void RequestFileList()
        {
            SendControlCommand("list_files", "\"path\":\"" + EscJ(_currentPathText.Text) + "\"");
        }

        private void UploadFile()
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
                                _sendQueue.Enqueue(payloadBytes);
                            }
                            isFirst = false;
                        }
                    }

                    MessageBox.Show("Dosya karşı bilgisayara başarıyla yüklendi!", "Yükleme Tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RequestFileList();
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
                    wFormatTag = 1,
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
