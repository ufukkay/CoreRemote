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

            // Register the custom URL protocol scheme coreremote://
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

            // Otherwise launch the main device list dashboard
            Application.Run(new MainForm());
        }

        private static string ParseDeviceIdFromUrl(string url)
        {
            try
            {
                // Format: coreremote://connect/DEVICE_ID or coreremote://connect/DEVICE_ID/
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
                
                // Register in HKCU (HKEY_CURRENT_USER\Software\Classes) so it does not require Admin privileges
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

    // ── MAIN DASHBOARD FORM (Device List Grid) ──────────────────────────────
    public class MainForm : Form
    {
        private DataGridView _grid;
        private Button _refreshBtn;
        private System.Windows.Forms.Timer _timer;

        public MainForm()
        {
            this.Text = "CoreRemote Teknisyen Portalı";
            this.Size = new Size(800, 500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 17, 23);
            this.ForeColor = Color.FromArgb(201, 209, 217);

            // Header Panel
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
                Width = 100,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(33, 38, 45),
                ForeColor = Color.FromArgb(201, 209, 217),
                Cursor = Cursors.Hand
            };
            _refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(48, 54, 61);
            _refreshBtn.Click += (s, e) => LoadDevicesAsync();

            header.Controls.Add(title);
            header.Controls.Add(_refreshBtn);

            // DataGrid Layout
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

            // Auto-refresh timer
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
                MessageBox.Show("Seçilen cihaz çevrimdışı, bağlantı kurulamaz.", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    
                    // Simple C# JSON Array parser
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
                Console.WriteLine("Error fetching devices: " + ex.Message);
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        // Lightweight JSON array parser to avoid external library dependency (Newtonsoft.Json etc)
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
                if (c == '"')
                {
                    insideQuote = !insideQuote;
                }
                else if ((c == ':' || c == ',') && !insideQuote)
                {
                    tokens.Add(sb.ToString().Trim().Trim('"'));
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString().Trim().Trim('"'));
            }

            for (int i = 0; i < tokens.Count - 1; i += 2)
            {
                dict[tokens[i]] = tokens[i + 1];
            }
            return dict;
        }
    }

    // ── REMOTE DESKTOP VIEWER FORM ──────────────────────────────────────────
    public class ViewerForm : Form
    {
        private string _deviceId;
        private PictureBox _screenBox;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private bool _isConnecting = false;

        public ViewerForm(string deviceId)
        {
            _deviceId = deviceId;
            this.Text = "CoreRemote Canlı Ekran - Cihaz: " + deviceId;
            this.Size = new Size(1024, 768);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;

            _screenBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            // Set up input events
            _screenBox.MouseMove += ScreenBox_MouseMove;
            _screenBox.MouseDown += ScreenBox_MouseDown;
            _screenBox.MouseUp += ScreenBox_MouseUp;
            this.KeyDown += ViewerForm_KeyDown;
            this.KeyUp += ViewerForm_KeyUp;
            this.KeyPreview = true; // Route key events to Form before controls

            this.Controls.Add(_screenBox);

            this.Load += async (s, e) => await ConnectToStreamAsync();
            this.FormClosing += (s, e) => Disconnect();
        }

        private async Task ConnectToStreamAsync()
        {
            if (_isConnecting) return;
            _isConnecting = true;

            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                // Connect to the raw operator websocket bridge endpoint on server
                string wsUrl = Program.ServerWsUrl + "/operator-socket?deviceId=" + Uri.EscapeDataString(_deviceId);
                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);

                // Start receiving screen frames
                Task.Run(async () => await ReceiveFramesAsync());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sunucuyla bağlantı kurulamadı: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task ReceiveFramesAsync()
        {
            byte[] buffer = new byte[1024 * 1024]; // 1MB frame buffer
            MemoryStream ms = new MemoryStream();

            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", _cts.Token);
                        break;
                    }

                    ms.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] packet = ms.ToArray();
                        ms.SetLength(0); // Reset memory stream for next message

                        if (packet.Length > 1 && packet[0] == 0x01)
                        {
                            // Binary JPEG frame payload (skip byte 0 frame identifier)
                            byte[] jpegData = new byte[packet.Length - 1];
                            Buffer.BlockCopy(packet, 1, jpegData, 0, jpegData.Length);

                            using (MemoryStream jpegMs = new MemoryStream(jpegData))
                            {
                                Image img = Image.FromStream(jpegMs);
                                
                                // Invoke render on GUI thread
                                _screenBox.Invoke((MethodInvoker)delegate {
                                    Image old = _screenBox.Image;
                                    _screenBox.Image = img;
                                    if (old != null) old.Dispose(); // Clear RAM
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("WebSocket receive error: " + ex.Message);
            }
            finally
            {
                ms.Dispose();
                _screenBox.Invoke((MethodInvoker)delegate {
                    this.Text += " (Bağlantı Kesildi)";
                });
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_cts != null) _cts.Cancel();
                if (_ws != null) _ws.Dispose();
            }
            catch {}
        }

        // ── CONTROL INPUT SIMULATION EMITTERS ───────────────────────────────

        private async void SendControlSignalAsync(string jsonPayload)
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

        private void ScreenBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_screenBox.Image == null) return;

            // Map local click coordinates into remote screen percentages
            double rx = (double)e.X / _screenBox.Width;
            double ry = (double)e.Y / _screenBox.Height;

            string json = "{" +
                "\"action\":\"mousemove\"," +
                "\"x\":" + rx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"y\":" + ry.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
            "}";
            SendControlSignalAsync(json);
        }

        private void ScreenBox_MouseDown(object sender, MouseEventArgs e)
        {
            int btn = (e.Button == MouseButtons.Right) ? 2 : 0;
            string json = "{\"action\":\"mousedown\",\"button\":" + btn + "}";
            SendControlSignalAsync(json);
        }

        private void ScreenBox_MouseUp(object sender, MouseEventArgs e)
        {
            int btn = (e.Button == MouseButtons.Right) ? 2 : 0;
            string json = "{\"action\":\"mouseup\",\"button\":" + btn + "}";
            SendControlSignalAsync(json);
        }

        private void ViewerForm_KeyDown(object sender, KeyEventArgs e)
        {
            int vk = (int)e.KeyCode;
            string json = "{\"action\":\"keydown\",\"vk\":" + vk + "}";
            SendControlSignalAsync(json);
            e.Handled = true;
        }

        private void ViewerForm_KeyUp(object sender, KeyEventArgs e)
        {
            int vk = (int)e.KeyCode;
            string json = "{\"action\":\"keyup\",\"vk\":" + vk + "}";
            SendControlSignalAsync(json);
            e.Handled = true;
        }
    }
}
