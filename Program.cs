using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (new Mutex(true, "Local\\CodexQuotaTray", out created))
            {
                if (!created)
                {
                    return;
                }

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayAppContext());
            }
        }
    }

    internal sealed class TrayAppContext : ApplicationContext
    {
        private const string RunKeyName = "CodexQuotaTray";
        private const uint EventSystemForeground = 0x0003;
        private const uint WineventOutOfContext = 0x0000;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _displayTimer;
        private readonly System.Windows.Forms.Timer _usageTimer;
        private readonly System.Windows.Forms.Timer _widgetRecoveryTimer;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _widgetItem;
        private readonly StatusWidget _widget;
        private readonly string _appDir;
        private readonly string _configPath;
        private readonly string _usageStatePath;
        private readonly string _widgetStatePath;
        private readonly object _widgetRecoveryLock = new object();
        private UsageState _usageState;
        private Config _config;
        private bool _refreshing;
        private bool _drainingUsage;
        private bool _widgetRecoveryScheduled;
        private DateTime _screenCaptureSeenUntil;
        private IntPtr _foregroundHook;
        private WinEventDelegate _foregroundDelegate;
        private int _displayMode;
        private Icon _currentIcon;
        private StatusSnapshot _lastStatus;

        public TrayAppContext()
        {
            _appDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(_appDir, "config.json");
            _usageStatePath = Path.Combine(_appDir, "usage-state.json");
            _widgetStatePath = Path.Combine(_appDir, "widget-state.json");
            _config = Config.Load(_configPath);
            _usageState = UsageState.Load(_usageStatePath);

            _startupItem = new ToolStripMenuItem("开机自启", null, ToggleStartup)
            {
                Checked = IsStartupEnabled()
            };
            _widgetItem = new ToolStripMenuItem("显示状态条", null, ToggleWidget)
            {
                Checked = _config.ShowTaskbarWidget
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("立即刷新", null, async (s, e) => await RefreshStatusAsync(true));
            menu.Items.Add("打开 CPA", null, (s, e) => OpenExternal(_config.ManagementUrl));
            menu.Items.Add("打开配置", null, (s, e) => OpenExternal(_configPath));
            menu.Items.Add("重新加载配置", null, (s, e) => ReloadConfig());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_widgetItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitThread());

            _currentIcon = IconPainter.Create("...", Color.DimGray);
            _tray = new NotifyIcon
            {
                Icon = _currentIcon,
                Text = "Codex 状态：启动中",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.MouseClick += async (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ShowLastStatus();
                }
                else if (e.Button == MouseButtons.Middle)
                {
                    await RefreshStatusAsync(true);
                }
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(5, _config.RefreshSeconds) * 1000;
            _timer.Tick += async (s, e) => await RefreshStatusAsync(false);
            _timer.Start();

            _displayTimer = new System.Windows.Forms.Timer();
            _displayTimer.Interval = 3000;
            _displayTimer.Tick += (s, e) =>
            {
                _displayMode++;
                if (_lastStatus != null)
                {
                    UpdateTray(_lastStatus);
                }
            };
            _displayTimer.Start();

            _usageTimer = new System.Windows.Forms.Timer();
            _usageTimer.Interval = 10000;
            _usageTimer.Tick += async (s, e) => await DrainUsageQueueAsync();
            _usageTimer.Start();

            _widget = new StatusWidget(menu, _widgetStatePath);
            if (_config.ShowTaskbarWidget)
            {
                _widget.Show();
            }

            _widgetRecoveryTimer = new System.Windows.Forms.Timer();
            _widgetRecoveryTimer.Interval = 30000;
            _widgetRecoveryTimer.Tick += (s, e) => _widget.EnsureVisibleLight(_widgetItem.Checked);
            _widgetRecoveryTimer.Start();

            InstallForegroundHook();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            Application.Idle += FirstRefresh;
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _timer.Dispose();
            _displayTimer.Stop();
            _displayTimer.Dispose();
            _usageTimer.Stop();
            _usageTimer.Dispose();
            _widgetRecoveryTimer.Stop();
            _widgetRecoveryTimer.Dispose();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            _widget.Close();
            _widget.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_currentIcon != null)
            {
                _currentIcon.Dispose();
            }
            base.ExitThreadCore();
        }

        private void InstallForegroundHook()
        {
            _foregroundDelegate = OnForegroundChanged;
            _foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _foregroundDelegate,
                0,
                0,
                WineventOutOfContext);
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint eventThread,
            uint eventTime)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)
            {
                return;
            }

            var processName = GetForegroundProcessName(hwnd);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            if (IsScreenCaptureProcess(processName))
            {
                _screenCaptureSeenUntil = DateTime.Now.AddSeconds(15);
                ScheduleWidgetRecovery(700);
                return;
            }

            if (IsShellProcess(processName) && DateTime.Now <= _screenCaptureSeenUntil)
            {
                ScheduleWidgetRecovery(300);
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            ScheduleWidgetRecovery(300);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            ScheduleWidgetRecovery(300);
        }

        private void ScheduleWidgetRecovery(int delayMs)
        {
            lock (_widgetRecoveryLock)
            {
                if (_widgetRecoveryScheduled)
                {
                    return;
                }

                _widgetRecoveryScheduled = true;
            }

            Task.Delay(Math.Max(0, delayMs)).ContinueWith(t =>
            {
                try
                {
                    if (_widget == null || _widget.IsDisposed || !_widget.IsHandleCreated)
                    {
                        lock (_widgetRecoveryLock)
                        {
                            _widgetRecoveryScheduled = false;
                        }
                        return;
                    }

                    _widget.BeginInvoke((MethodInvoker)delegate
                    {
                        lock (_widgetRecoveryLock)
                        {
                            _widgetRecoveryScheduled = false;
                        }

                        _widget.EnsureVisible(_widgetItem.Checked);
                    });
                }
                catch
                {
                    lock (_widgetRecoveryLock)
                    {
                        _widgetRecoveryScheduled = false;
                    }
                }
            });
        }

        private static string GetForegroundProcessName(IntPtr hwnd)
        {
            try
            {
                uint processId;
                GetWindowThreadProcessId(hwnd, out processId);
                if (processId == 0)
                {
                    return null;
                }

                using (var process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsScreenCaptureProcess(string processName)
        {
            processName = processName.Trim().ToLowerInvariant();
            return processName == "snippingtool" ||
                processName == "screenclippinghost" ||
                processName == "screensketch";
        }

        private static bool IsShellProcess(string processName)
        {
            processName = processName.Trim().ToLowerInvariant();
            return processName == "explorer";
        }

        private void ReloadConfig()
        {
            _config = Config.Load(_configPath);
            _timer.Interval = Math.Max(5, _config.RefreshSeconds) * 1000;
            _widgetItem.Checked = _config.ShowTaskbarWidget;
            _widget.SetVisible(_config.ShowTaskbarWidget);
            _ = RefreshStatusAsync(true);
        }

        private void ToggleWidget(object sender, EventArgs e)
        {
            _widgetItem.Checked = !_widgetItem.Checked;
            _widget.SetVisible(_widgetItem.Checked);
        }

        private void FirstRefresh(object sender, EventArgs e)
        {
            Application.Idle -= FirstRefresh;
            _ = RefreshStatusAsync(false);
        }

        private async Task RefreshStatusAsync(bool showBalloon)
        {
            if (_refreshing)
            {
                return;
            }

            _refreshing = true;
            try
            {
                var status = await StatusClient.FetchAsync(_config);
                ApplyUsageState(status);
                _lastStatus = status;
                UpdateTray(status);
                if (showBalloon)
                {
                    ShowStatus(status);
                }
            }
            catch (Exception ex)
            {
                var status = StatusSnapshot.Error("程序异常：" + ex.Message);
                ApplyUsageState(status);
                _lastStatus = status;
                UpdateTray(status);
                if (showBalloon)
                {
                    ShowStatus(status);
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void UpdateTray(StatusSnapshot status)
        {
            var color = status.GetColor();
            var text = status.GetIconText(_displayMode);
            var nextIcon = IconPainter.Create(text, color);
            var oldIcon = _currentIcon;
            _currentIcon = nextIcon;
            _tray.Icon = nextIcon;
            _tray.Text = LimitTooltip(status.GetTooltip());
            _widget.UpdateStatus(status);
            if (oldIcon != null)
            {
                oldIcon.Dispose();
            }
        }

        private async Task DrainUsageQueueAsync()
        {
            if (_drainingUsage)
            {
                return;
            }

            _drainingUsage = true;
            try
            {
                var delta = await CpamcClient.DrainUsageQueueAsync(_config, 200);
                if (delta != null && delta.HasData)
                {
                    _usageState.Add(delta);
                    _usageState.Save(_usageStatePath);
                    if (_lastStatus != null)
                    {
                        ApplyUsageState(_lastStatus);
                        UpdateTray(_lastStatus);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _drainingUsage = false;
            }
        }

        private void ApplyUsageState(StatusSnapshot status)
        {
            if (status == null || _usageState == null)
            {
                return;
            }

            status.TokenTotal = _usageState.TotalTokens;
            status.TokenInput = _usageState.InputTokens;
            status.TokenOutput = _usageState.OutputTokens;
            status.TokenReasoning = _usageState.ReasoningTokens;
        }

        private void ShowLastStatus()
        {
            if (_lastStatus == null)
            {
                _ = RefreshStatusAsync(true);
                return;
            }

            ShowStatus(_lastStatus);
        }

        private void ShowStatus(StatusSnapshot status)
        {
            var icon = status.Online ? ToolTipIcon.Info : ToolTipIcon.Error;
            _tray.ShowBalloonTip(5000, "Codex 状态", status.GetDetail(), icon);
        }

        private static string LimitTooltip(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Codex 状态";
            }

            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private static void OpenExternal(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(RunKeyName, false);
                        _startupItem.Checked = false;
                    }
                    else
                    {
                        key.SetValue(RunKeyName, "\"" + Application.ExecutablePath + "\"");
                        _startupItem.Checked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, "开机自启设置失败", ex.Message, ToolTipIcon.Error);
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    var value = key == null ? null : key.GetValue(RunKeyName) as string;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
            catch
            {
                return false;
            }
        }

        private delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr eventHookAssembly,
            WinEventDelegate eventProc,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }

    internal sealed class Config
    {
        public string BaseUrl { get; set; }
        public string ManagementPath { get; set; }
        public string ProviderMode { get; set; }
        public int RefreshSeconds { get; set; }
        public bool ShowTaskbarWidget { get; set; }
        public string ManagementKeyEnvironmentVariable { get; set; }
        public string ApiKeyEnvironmentVariable { get; set; }
        public string[] StatusPaths { get; set; }
        public string[] CompatibilityNotes { get; set; }

        public string ManagementUrl
        {
            get
            {
                var path = string.IsNullOrWhiteSpace(ManagementPath) ? "/management.html" : ManagementPath.Trim();
                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }

                return BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
            }
        }

        public static Config Load(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultJson(), new UTF8Encoding(false));
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var config = serializer.Deserialize<Config>(json) ?? Default();
                config.Normalize();
                return config;
            }
            catch
            {
                var config = Default();
                config.Normalize();
                return config;
            }
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                BaseUrl = "http://192.168.0.16:8317";
            }

            BaseUrl = BaseUrl.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(ManagementPath))
            {
                ManagementPath = "/management.html";
            }

            if (string.IsNullOrWhiteSpace(ProviderMode))
            {
                ProviderMode = "cpamc";
            }

            ProviderMode = ProviderMode.Trim().ToLowerInvariant();
            if (RefreshSeconds < 5)
            {
                RefreshSeconds = 60;
            }

            if (ManagementKeyEnvironmentVariable == null)
            {
                ManagementKeyEnvironmentVariable = "CPAMC_MANAGEMENT_KEY";
            }

            if (StatusPaths == null || StatusPaths.Length == 0)
            {
                StatusPaths = Default().StatusPaths;
            }
        }

        private static Config Default()
        {
            return new Config
            {
                BaseUrl = "http://192.168.0.16:8317",
                ManagementPath = "/management.html",
                ProviderMode = "cpamc",
                RefreshSeconds = 60,
                ShowTaskbarWidget = true,
                ManagementKeyEnvironmentVariable = "CPAMC_MANAGEMENT_KEY",
                ApiKeyEnvironmentVariable = "",
                StatusPaths = new[]
                {
                    "/"
                },
                CompatibilityNotes = new[]
                {
                    "cpamc is tested.",
                    "newapi and sub2api are planned compatibility targets, not tested yet."
                }
            };
        }

        private static string DefaultJson()
        {
            return "{\r\n" +
                "  \"_comment\": \"CodexQuotaTray configuration. JSON does not allow comments, so fields starting with _ are notes and are ignored by the app.\",\r\n" +
                "  \"_security\": \"Do not put CPAMC keys or API keys here. Store the management key in the Windows user environment variable below.\",\r\n" +
                "\r\n" +
                "  \"BaseUrl\": \"http://192.168.0.16:8317\",\r\n" +
                "  \"_BaseUrl\": \"CPA / CPAMC base URL. Keep host and port here only.\",\r\n" +
                "  \"ManagementPath\": \"/management.html\",\r\n" +
                "  \"_ManagementPath\": \"Right-click menu 'Open CPA' opens BaseUrl + ManagementPath.\",\r\n" +
                "  \"ProviderMode\": \"cpamc\",\r\n" +
                "  \"_ProviderMode\": \"cpamc is tested. newapi/sub2api are reserved for later testing and currently use generic StatusPaths only.\",\r\n" +
                "\r\n" +
                "  \"RefreshSeconds\": 60,\r\n" +
                "  \"ShowTaskbarWidget\": true,\r\n" +
                "  \"ManagementKeyEnvironmentVariable\": \"CPAMC_MANAGEMENT_KEY\",\r\n" +
                "  \"ApiKeyEnvironmentVariable\": \"\",\r\n" +
                "\r\n" +
                "  \"StatusPaths\": [\r\n" +
                "    \"/\"\r\n" +
                "  ],\r\n" +
                "  \"_StatusPaths\": \"Fallback generic status endpoints for non-CPAMC providers. CPAMC quota aggregation uses management APIs.\",\r\n" +
                "\r\n" +
                "  \"CompatibilityNotes\": [\r\n" +
                "    \"cpamc: tested with /v0/management/* APIs.\",\r\n" +
                "    \"newapi: planned, not tested yet.\",\r\n" +
                "    \"sub2api: planned, not tested yet.\"\r\n" +
                "  ]\r\n" +
                "}\r\n";
        }
    }

    internal static class StatusClient
    {
        public static async Task<StatusSnapshot> FetchAsync(Config config)
        {
            if (string.Equals(config.ProviderMode, "cpamc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.ProviderMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var cpamcStatus = await CpamcClient.TryFetchAsync(config);
                if (cpamcStatus != null)
                {
                    return cpamcStatus;
                }
            }

            var apiKey = GetApiKey(config.ApiKeyEnvironmentVariable);
            StatusSnapshot lastFailure = null;

            foreach (var path in config.StatusPaths)
            {
                var url = BuildUrl(config.BaseUrl, path);
                try
                {
                    var response = await GetAsync(url, apiKey);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        lastFailure = StatusSnapshot.Error("接口需要认证：" + path);
                        continue;
                    }

                    if ((int)response.StatusCode == 404)
                    {
                        continue;
                    }

                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    {
                        lastFailure = StatusSnapshot.Error("接口异常 " + (int)response.StatusCode + "：" + path);
                        continue;
                    }

                    var snapshot = StatusParser.Parse(response.Body, path);
                    snapshot.Online = true;
                    snapshot.SourcePath = path;
                    snapshot.CheckedAt = DateTime.Now;
                    return snapshot;
                }
                catch (WebException ex)
                {
                    var http = ex.Response as HttpWebResponse;
                    if (http != null)
                    {
                        if (http.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            lastFailure = StatusSnapshot.Error("接口需要认证：" + path);
                            continue;
                        }

                        if ((int)http.StatusCode == 404)
                        {
                            continue;
                        }

                        lastFailure = StatusSnapshot.Error("HTTP " + (int)http.StatusCode + "：" + path);
                        continue;
                    }

                    lastFailure = StatusSnapshot.Error(ex.Message);
                }
                catch (Exception ex)
                {
                    lastFailure = StatusSnapshot.Error(ex.Message);
                }
            }

            return lastFailure ?? StatusSnapshot.Error("未找到可用状态接口");
        }

        private static string GetApiKey(string envName)
        {
            if (string.IsNullOrWhiteSpace(envName))
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(envName.Trim(), EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(envName.Trim(), EnvironmentVariableTarget.Machine);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(envName.Trim(), EnvironmentVariableTarget.Process);
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string BuildUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return baseUrl + "/";
            }

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return baseUrl + "/" + path.TrimStart('/');
        }

        private static async Task<HttpResult> GetAsync(string url, string apiKey)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.UserAgent = "CodexQuotaTray/1.0";
            request.Accept = "application/json,text/plain,*/*";

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return new HttpResult
                {
                    StatusCode = response.StatusCode,
                    Body = await reader.ReadToEndAsync()
                };
            }
        }
    }

    internal sealed class HttpResult
    {
        public HttpStatusCode StatusCode { get; set; }
        public string Body { get; set; }
    }

    internal sealed class UsageState
    {
        public long TotalTokens { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long CachedTokens { get; set; }
        public long QueueRecords { get; set; }
        public string UpdatedAt { get; set; }

        public static UsageState Load(string path)
        {
            if (!File.Exists(path))
            {
                return new UsageState();
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return new JavaScriptSerializer().Deserialize<UsageState>(json) ?? new UsageState();
            }
            catch
            {
                return new UsageState();
            }
        }

        public void Add(TokenUsageDelta delta)
        {
            if (delta == null)
            {
                return;
            }

            TotalTokens += delta.TotalTokens;
            InputTokens += delta.InputTokens;
            OutputTokens += delta.OutputTokens;
            ReasoningTokens += delta.ReasoningTokens;
            CachedTokens += delta.CachedTokens;
            QueueRecords += delta.Records;
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public void Save(string path)
        {
            try
            {
                var json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    internal sealed class WidgetState
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public bool HasCustomPosition { get; set; }

        public static WidgetState Load(string path)
        {
            if (!File.Exists(path))
            {
                return new WidgetState();
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return new JavaScriptSerializer().Deserialize<WidgetState>(json) ?? new WidgetState();
            }
            catch
            {
                return new WidgetState();
            }
        }

        public void Save(string path)
        {
            try
            {
                var json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    internal sealed class TokenUsageDelta
    {
        public long TotalTokens { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long CachedTokens { get; set; }
        public long Records { get; set; }

        public bool HasData
        {
            get
            {
                return Records > 0 || TotalTokens > 0 || InputTokens > 0 || OutputTokens > 0 ||
                    ReasoningTokens > 0 || CachedTokens > 0;
            }
        }
    }

    internal static class CpamcClient
    {
        private const string ManagementPrefix = "/v0/management";
        private const string CodexUsageUrl = "https://chatgpt.com/backend-api/wham/usage";

        public static async Task<TokenUsageDelta> DrainUsageQueueAsync(Config config, int count)
        {
            if (!string.Equals(config.ProviderMode, "cpamc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(config.ProviderMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return new TokenUsageDelta();
            }

            if (string.IsNullOrWhiteSpace(config.ManagementKeyEnvironmentVariable))
            {
                return new TokenUsageDelta();
            }

            var managementKey = GetEnvValue(config.ManagementKeyEnvironmentVariable.Trim());
            if (string.IsNullOrWhiteSpace(managementKey))
            {
                return new TokenUsageDelta();
            }

            count = Math.Max(1, Math.Min(1000, count));
            var response = await SendAsync("GET", ManagementUrl(config, "/usage-queue?count=" + count), managementKey, null);
            var root = new JavaScriptSerializer().DeserializeObject(response.Body);
            var items = ToObjectList(root);
            var delta = new TokenUsageDelta();
            if (items == null)
            {
                return delta;
            }

            foreach (var item in items)
            {
                var record = ParseObject(item);
                if (record == null)
                {
                    continue;
                }

                delta.Records++;
                var tokens = GetDict(record, "tokens");
                if (tokens == null)
                {
                    continue;
                }

                var input = ToLong(GetValue(tokens, "input_tokens", "inputTokens")) ?? 0;
                var output = ToLong(GetValue(tokens, "output_tokens", "outputTokens")) ?? 0;
                var reasoning = ToLong(GetValue(tokens, "reasoning_tokens", "reasoningTokens")) ?? 0;
                var cached = ToLong(GetValue(tokens, "cached_tokens", "cachedTokens", "cache_read_tokens", "cacheReadTokens", "cache_creation_tokens", "cacheCreationTokens")) ?? 0;
                var total = ToLong(GetValue(tokens, "total_tokens", "totalTokens")) ?? 0;
                if (total == 0)
                {
                    total = input + output + reasoning;
                }

                if (total == 0)
                {
                    total = input + output + reasoning + cached;
                }

                delta.InputTokens += input;
                delta.OutputTokens += output;
                delta.ReasoningTokens += reasoning;
                delta.CachedTokens += cached;
                delta.TotalTokens += total;
            }

            return delta;
        }

        public static async Task<StatusSnapshot> TryFetchAsync(Config config)
        {
            if (!string.Equals(config.ProviderMode, "cpamc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(config.ProviderMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(config.ManagementKeyEnvironmentVariable))
            {
                return null;
            }

            var envName = config.ManagementKeyEnvironmentVariable.Trim();
            var managementKey = GetEnvValue(envName);
            if (string.IsNullOrWhiteSpace(managementKey))
            {
                return StatusSnapshot.Error("Missing env var: " + envName);
            }

            var errors = new List<string>();
            var usage = new UsageTotals();
            var hasUsage = false;
            var codexFiles = new List<AuthFileInfo>();
            var hasAuthFiles = false;

            try
            {
                usage = await FetchUsageTotalsAsync(config, managementKey);
                hasUsage = true;
            }
            catch (Exception ex)
            {
                errors.Add("api-key-usage: " + CleanMessage(ex.Message));
            }

            try
            {
                var logUsage = await FetchRequestLogUsageAsync(config, managementKey);
                if (logUsage.Total > usage.Total)
                {
                    usage = logUsage;
                    hasUsage = true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("logs: " + CleanMessage(ex.Message));
            }

            try
            {
                codexFiles = await FetchCodexAuthFilesAsync(config, managementKey);
                hasAuthFiles = true;
                var authUsage = SumAuthFileUsage(codexFiles);
                if (usage.Total == 0 && authUsage.Total > 0)
                {
                    usage = authUsage;
                    hasUsage = true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("auth-files: " + CleanMessage(ex.Message));
            }

            var quotas = new List<CodexQuotaResult>();
            if (hasAuthFiles)
            {
                foreach (var file in codexFiles)
                {
                    try
                    {
                        quotas.Add(await FetchCodexQuotaAsync(config, managementKey, file));
                    }
                    catch (Exception ex)
                    {
                        quotas.Add(new CodexQuotaResult
                        {
                            Name = file.Name,
                            PlanType = file.PlanType,
                            Error = CleanMessage(ex.Message)
                        });
                    }
                }
            }

            if (!hasUsage && !hasAuthFiles)
            {
                return StatusSnapshot.Error(errors.Count > 0 ? string.Join("; ", errors.ToArray()) : "CPAMC management API unavailable");
            }

            return BuildSnapshot(usage, hasUsage, codexFiles.Count, quotas, errors);
        }

        private static StatusSnapshot BuildSnapshot(
            UsageTotals usage,
            bool hasUsage,
            int codexFileCount,
            List<CodexQuotaResult> quotas,
            List<string> errors)
        {
            var snapshot = new StatusSnapshot
            {
                Online = true,
                SourcePath = ManagementPrefix,
                CheckedAt = DateTime.Now,
                CallSuccess = usage.Success,
                CallFailed = usage.Failed
            };

            var quotaLines = new List<string>();
            foreach (var quota in quotas)
            {
                if (!string.IsNullOrWhiteSpace(quota.Error))
                {
                    quotaLines.Add(ShortName(quota.Name) + ": " + quota.Error);
                    continue;
                }

                quotaLines.Add(BuildQuotaAccountLine(quota));
            }

            var fiveHourPool = BuildQuotaPool(quotas, "5h");
            var weekPool = BuildQuotaPool(quotas, "7d");
            ApplyQuotaPool(snapshot, fiveHourPool, weekPool);

            var messageParts = new List<string>();
            if (hasUsage)
            {
                messageParts.Add("Requests " + usage.Total + " (OK " + usage.Success + ", Fail " + usage.Failed + ")");
            }

            messageParts.Add("Codex files " + codexFileCount);

            if (fiveHourPool.HasData || weekPool.HasData)
            {
                var pools = new List<string>();
                if (fiveHourPool.HasData)
                {
                    pools.Add("5h " + FormatPoolSummary(fiveHourPool));
                }

                if (weekPool.HasData)
                {
                    pools.Add("7d " + FormatPoolSummary(weekPool));
                }

                messageParts.Add("Pool " + string.Join(", ", pools.ToArray()));
            }
            else if (hasUsage && usage.Total > 0)
            {
                snapshot.Used = usage.Total > int.MaxValue ? int.MaxValue : (int)usage.Total;
            }

            if (errors.Count > 0)
            {
                messageParts.Add("Warnings " + errors.Count);
            }

            if (messageParts.Count == 0)
            {
                messageParts.Add("CPAMC online");
            }

            snapshot.Message = string.Join("; ", messageParts.ToArray());
            if (quotaLines.Count > 0)
            {
                snapshot.RawSummary = string.Join(" | ", quotaLines.ToArray());
            }
            else if (errors.Count > 0)
            {
                snapshot.RawSummary = string.Join(" | ", errors.ToArray());
            }

            return snapshot;
        }

        private static void ApplyQuotaPool(StatusSnapshot snapshot, QuotaPoolAggregate fiveHourPool, QuotaPoolAggregate weekPool)
        {
            var lowestRemaining = new int?();

            if (fiveHourPool.HasData)
            {
                snapshot.Quota5hRemaining = fiveHourPool.RemainingPercent;
                snapshot.Quota5hReset = fiveHourPool.ResetLabel;
                snapshot.Quota5hAccountCount = fiveHourPool.AccountCount;
                lowestRemaining = LowerRemaining(lowestRemaining, fiveHourPool.RemainingPercent);
            }

            if (weekPool.HasData)
            {
                snapshot.Quota7dRemaining = weekPool.RemainingPercent;
                snapshot.Quota7dReset = weekPool.ResetLabel;
                snapshot.Quota7dAccountCount = weekPool.AccountCount;
                lowestRemaining = LowerRemaining(lowestRemaining, weekPool.RemainingPercent);
            }

            if (lowestRemaining.HasValue)
            {
                snapshot.Remaining = lowestRemaining.Value;
                snapshot.Limit = 100;
                snapshot.Used = 100 - lowestRemaining.Value;
            }
        }

        private static int? LowerRemaining(int? current, int? candidate)
        {
            if (!candidate.HasValue)
            {
                return current;
            }

            if (!current.HasValue || candidate.Value < current.Value)
            {
                return candidate.Value;
            }

            return current;
        }

        private static QuotaPoolAggregate BuildQuotaPool(List<CodexQuotaResult> quotas, string windowKey)
        {
            var pool = new QuotaPoolAggregate(windowKey);
            foreach (var quota in quotas)
            {
                if (!string.IsNullOrWhiteSpace(quota.Error))
                {
                    continue;
                }

                var window = SelectPoolWindow(quota, windowKey);
                if (window != null)
                {
                    pool.Add(window);
                }
            }

            return pool;
        }

        private static CodexQuotaWindow SelectPoolWindow(CodexQuotaResult quota, string windowKey)
        {
            var useCodeOnly = false;
            foreach (var window in quota.Windows)
            {
                if (string.Equals(window.Category, "Code", StringComparison.OrdinalIgnoreCase))
                {
                    useCodeOnly = true;
                    break;
                }
            }

            CodexQuotaWindow selected = null;
            foreach (var window in quota.Windows)
            {
                if (!string.Equals(window.WindowKey, windowKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (useCodeOnly && !string.Equals(window.Category, "Code", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (selected == null || CompareQuotaWindow(window, selected) < 0)
                {
                    selected = window;
                }
            }

            return selected;
        }

        private static int CompareQuotaWindow(CodexQuotaWindow left, CodexQuotaWindow right)
        {
            var leftRemaining = GetWindowRemainingPercent(left);
            var rightRemaining = GetWindowRemainingPercent(right);
            if (leftRemaining.HasValue && rightRemaining.HasValue)
            {
                return leftRemaining.Value.CompareTo(rightRemaining.Value);
            }

            if (leftRemaining.HasValue)
            {
                return -1;
            }

            if (rightRemaining.HasValue)
            {
                return 1;
            }

            return string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildQuotaAccountLine(CodexQuotaResult quota)
        {
            var plan = string.IsNullOrWhiteSpace(quota.PlanType) ? "" : " " + quota.PlanType;
            var fiveHour = SelectPoolWindow(quota, "5h");
            var week = SelectPoolWindow(quota, "7d");
            return ShortName(quota.Name) + plan +
                " 5h " + FormatWindowRemaining(fiveHour) +
                " 7d " + FormatWindowRemaining(week);
        }

        private static string FormatWindowRemaining(CodexQuotaWindow window)
        {
            if (window == null)
            {
                return "--";
            }

            var remaining = GetWindowRemainingPercent(window);
            return remaining.HasValue ? remaining.Value + "%" : "--";
        }

        private static string FormatPoolSummary(QuotaPoolAggregate pool)
        {
            var value = pool.RemainingPercent.HasValue ? pool.RemainingPercent.Value + "%" : "--";
            return value + " x" + pool.AccountCount;
        }

        private static int? GetWindowRemainingPercent(CodexQuotaWindow window)
        {
            if (window == null)
            {
                return null;
            }

            if (window.Limit.HasValue && window.Limit.Value > 0)
            {
                long remaining;
                if (window.Remaining.HasValue)
                {
                    remaining = window.Remaining.Value;
                }
                else if (window.Used.HasValue)
                {
                    remaining = window.Limit.Value - window.Used.Value;
                }
                else
                {
                    remaining = -1;
                }

                if (remaining >= 0)
                {
                    return ClampPercent((int)Math.Round(remaining * 100.0 / window.Limit.Value));
                }
            }

            if (window.UsedPercent.HasValue)
            {
                return ClampPercent(100 - window.UsedPercent.Value);
            }

            return null;
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static async Task<UsageTotals> FetchUsageTotalsAsync(Config config, string managementKey)
        {
            var response = await SendAsync("GET", ManagementUrl(config, "/api-key-usage"), managementKey, null);
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(response.Body);
            var totals = new UsageTotals();
            AddUsageTotals(root, totals);
            return totals;
        }

        private static async Task<UsageTotals> FetchRequestLogUsageAsync(Config config, string managementKey)
        {
            var response = await SendAsync("GET", ManagementUrl(config, "/logs"), managementKey, null);
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(response.Body) as IDictionary<string, object>;
            var lines = root == null ? null : ToObjectList(GetValue(root, "lines"));
            var totals = new UsageTotals();
            if (lines == null)
            {
                return totals;
            }

            foreach (var item in lines)
            {
                var line = Convert.ToString(item);
                if (!IsConversationRequestLog(line))
                {
                    continue;
                }

                var statusCode = ExtractStatusCode(line);
                if (statusCode >= 200 && statusCode < 400)
                {
                    totals.Success++;
                }
                else
                {
                    totals.Failed++;
                }
            }

            return totals;
        }

        private static bool IsConversationRequestLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("POST"))
            {
                return false;
            }

            return line.Contains("\"/v1/responses\"") ||
                line.Contains("\"/v1/chat/completions\"") ||
                line.Contains("\"/v1/completions\"");
        }

        private static int ExtractStatusCode(string line)
        {
            var match = Regex.Match(line ?? "", @"\]\s+(\d{3})\s+\|");
            if (!match.Success)
            {
                return 0;
            }

            int code;
            return int.TryParse(match.Groups[1].Value, out code) ? code : 0;
        }

        private static async Task<List<AuthFileInfo>> FetchCodexAuthFilesAsync(Config config, string managementKey)
        {
            var response = await SendAsync("GET", ManagementUrl(config, "/auth-files"), managementKey, null);
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(response.Body) as IDictionary<string, object>;
            var files = root == null ? null : ToObjectList(GetValue(root, "files"));
            var result = new List<AuthFileInfo>();

            if (files == null)
            {
                return result;
            }

            foreach (var item in files)
            {
                var entry = item as IDictionary<string, object>;
                if (entry == null || !IsCodexFile(entry) || IsDisabled(entry))
                {
                    continue;
                }

                var authIndex = GetString(entry, "auth_index", "authIndex");
                if (string.IsNullOrWhiteSpace(authIndex))
                {
                    continue;
                }

                result.Add(new AuthFileInfo
                {
                    Name = GetString(entry, "name") ?? authIndex,
                    AuthIndex = authIndex,
                    AccountId = ResolveCodexAccountId(entry),
                    PlanType = NormalizePlanType(GetString(entry, "plan_type", "planType")),
                    Success = ToLong(GetValue(entry, "success")) ?? 0,
                    Failed = ToLong(GetValue(entry, "failed")) ?? 0,
                    Raw = entry
                });
            }

            return result;
        }

        private static async Task<CodexQuotaResult> FetchCodexQuotaAsync(Config config, string managementKey, AuthFileInfo file)
        {
            var serializer = new JavaScriptSerializer();
            var header = new Dictionary<string, object>
            {
                { "Authorization", "Bearer $TOKEN$" },
                { "Content-Type", "application/json" },
                { "User-Agent", "codex_cli_rs/0.76.0 (Windows; x86_64) CodexQuotaTray" }
            };

            if (!string.IsNullOrWhiteSpace(file.AccountId))
            {
                header["Chatgpt-Account-Id"] = file.AccountId;
            }

            var payload = new Dictionary<string, object>
            {
                { "authIndex", file.AuthIndex },
                { "method", "GET" },
                { "url", CodexUsageUrl },
                { "header", header }
            };

            var response = await SendAsync("POST", ManagementUrl(config, "/api-call"), managementKey, serializer.Serialize(payload));
            var root = serializer.DeserializeObject(response.Body) as IDictionary<string, object>;
            if (root == null)
            {
                throw new Exception("Invalid api-call response");
            }

            var statusCode = ToInt(GetValue(root, "status_code", "statusCode"));
            if (!statusCode.HasValue)
            {
                throw new Exception("Missing api-call status");
            }

            var body = GetValue(root, "body");
            if (statusCode.Value < 200 || statusCode.Value >= 300)
            {
                throw new Exception("HTTP " + statusCode.Value + " " + BodyMessage(body));
            }

            var bodyDict = ParseObject(body);
            if (bodyDict == null)
            {
                throw new Exception("Empty quota payload");
            }

            var result = new CodexQuotaResult
            {
                Name = file.Name,
                PlanType = NormalizePlanType(GetString(bodyDict, "plan_type", "planType")) ?? file.PlanType
            };

            AddLimitWindows(result, "Code", GetDict(bodyDict, "rate_limit", "rateLimit"));
            AddLimitWindows(result, "Review", GetDict(bodyDict, "code_review_rate_limit", "codeReviewRateLimit"));

            var additional = ToObjectList(GetValue(bodyDict, "additional_rate_limits", "additionalRateLimits"));
            if (additional != null)
            {
                var index = 1;
                foreach (var item in additional)
                {
                    var limit = item as IDictionary<string, object>;
                    if (limit == null)
                    {
                        continue;
                    }

                    var name = GetString(limit, "limit_name", "limitName", "metered_feature", "meteredFeature") ?? ("Extra " + index);
                    AddLimitWindows(result, name, GetDict(limit, "rate_limit", "rateLimit"));
                    index++;
                }
            }

            if (result.Windows.Count == 0)
            {
                throw new Exception("No quota windows");
            }

            return result;
        }

        private static void AddLimitWindows(CodexQuotaResult result, string prefix, IDictionary<string, object> limitInfo)
        {
            if (limitInfo == null)
            {
                return;
            }

            var limitReached = ToBool(GetValue(limitInfo, "limit_reached", "limitReached"));
            var allowed = ToBool(GetValue(limitInfo, "allowed"));
            AddWindow(result, prefix, "Primary", GetDict(limitInfo, "primary_window", "primaryWindow"), limitReached == true, allowed);
            AddWindow(result, prefix, "Secondary", GetDict(limitInfo, "secondary_window", "secondaryWindow"), limitReached == true, allowed);
        }

        private static void AddWindow(
            CodexQuotaResult result,
            string prefix,
            string fallback,
            IDictionary<string, object> window,
            bool limitReached,
            bool? allowed)
        {
            if (window == null)
            {
                return;
            }

            var used = ToLong(GetValue(window, "used", "used_count", "usedCount", "used_requests", "usedRequests", "usage", "current"));
            var limit = ToLong(GetValue(window, "limit", "quota", "max", "cap", "hard_limit", "hardLimit", "total"));
            var remaining = ToLong(GetValue(window, "remaining", "remain", "left", "available"));
            var percent = ToInt(GetValue(window, "used_percent", "usedPercent"));
            if (!percent.HasValue && limit.HasValue && limit.Value > 0)
            {
                if (used.HasValue)
                {
                    percent = ClampPercent((int)Math.Round(used.Value * 100.0 / limit.Value));
                }
                else if (remaining.HasValue)
                {
                    percent = ClampPercent(100 - (int)Math.Round(remaining.Value * 100.0 / limit.Value));
                }
            }

            if (!percent.HasValue && (limitReached || allowed == false))
            {
                percent = 100;
            }

            if (percent.HasValue)
            {
                percent = ClampPercent(percent.Value);
            }

            var seconds = ToInt(GetValue(window, "limit_window_seconds", "limitWindowSeconds"));
            var windowKey = WindowLabel(seconds, fallback);
            var label = prefix + " " + windowKey;
            result.Windows.Add(new CodexQuotaWindow
            {
                Category = prefix,
                Label = label,
                WindowKey = windowKey,
                UsedPercent = percent,
                Used = used,
                Limit = limit,
                Remaining = remaining,
                ResetLabel = FormatReset(window)
            });
        }

        private static string WindowLabel(int? seconds, string fallback)
        {
            if (seconds == 18000)
            {
                return "5h";
            }

            if (seconds == 604800)
            {
                return "7d";
            }

            return fallback;
        }

        private static string FormatReset(IDictionary<string, object> window)
        {
            var resetAfter = ToInt(GetValue(window, "reset_after_seconds", "resetAfterSeconds"));
            if (resetAfter.HasValue)
            {
                return "reset " + FormatDuration(resetAfter.Value);
            }

            var resetAt = ToLong(GetValue(window, "reset_at", "resetAt"));
            if (resetAt.HasValue)
            {
                var seconds = resetAt.Value > 100000000000L ? resetAt.Value / 1000L : resetAt.Value;
                var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(seconds)
                    .ToLocalTime();
                return "reset " + time.ToString("MM-dd HH:mm");
            }

            return "reset --";
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds < 60)
            {
                return seconds + "s";
            }

            if (seconds < 3600)
            {
                return (seconds / 60) + "m";
            }

            if (seconds < 86400)
            {
                return (seconds / 3600) + "h" + ((seconds % 3600) / 60) + "m";
            }

            return (seconds / 86400) + "d" + ((seconds % 86400) / 3600) + "h";
        }

        private static async Task<HttpResult> SendAsync(string method, string url, string managementKey, string body)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "CodexQuotaTray/1.0";
            request.Accept = "application/json,text/plain,*/*";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + managementKey;

            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (var stream = await request.GetRequestStreamAsync())
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
            {
                return new HttpResult
                {
                    StatusCode = response.StatusCode,
                    Body = await reader.ReadToEndAsync()
                };
            }
        }

        private static string ManagementUrl(Config config, string path)
        {
            return config.BaseUrl.TrimEnd('/') + ManagementPrefix + "/" + path.TrimStart('/');
        }

        private static void AddUsageTotals(object value, UsageTotals totals)
        {
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                if (HasKey(dict, "success") || HasKey(dict, "failed"))
                {
                    totals.Success += ToLong(GetValue(dict, "success")) ?? 0;
                    totals.Failed += ToLong(GetValue(dict, "failed")) ?? 0;
                    return;
                }

                foreach (var item in dict.Values)
                {
                    AddUsageTotals(item, totals);
                }
                return;
            }

            var array = ToObjectList(value);
            if (array != null)
            {
                foreach (var item in array)
                {
                    AddUsageTotals(item, totals);
                }
            }
        }

        private static UsageTotals SumAuthFileUsage(List<AuthFileInfo> files)
        {
            var totals = new UsageTotals();
            foreach (var file in files)
            {
                totals.Success += file.Success;
                totals.Failed += file.Failed;
            }

            return totals;
        }

        private static bool IsCodexFile(IDictionary<string, object> entry)
        {
            var type = (GetString(entry, "type") ?? "").ToLowerInvariant();
            var provider = (GetString(entry, "provider") ?? "").ToLowerInvariant();
            var name = (GetString(entry, "name") ?? "").ToLowerInvariant();
            return type == "codex" || provider == "codex" || name.Contains("codex");
        }

        private static List<object> ToObjectList(object value)
        {
            if (value == null || value is string || value is IDictionary<string, object>)
            {
                return null;
            }

            var result = new List<object>();
            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                return null;
            }

            foreach (var item in enumerable)
            {
                result.Add(item);
            }

            return result;
        }

        private static bool IsDisabled(IDictionary<string, object> entry)
        {
            return ToBool(GetValue(entry, "disabled", "unavailable")) == true;
        }

        private static string ResolveCodexAccountId(IDictionary<string, object> entry)
        {
            var candidates = new List<object>();
            candidates.Add(GetValue(entry, "id_token", "idToken"));

            var metadata = GetDict(entry, "metadata");
            if (metadata != null)
            {
                candidates.Add(GetValue(metadata, "id_token", "idToken"));
            }

            var attributes = GetDict(entry, "attributes");
            if (attributes != null)
            {
                candidates.Add(GetValue(attributes, "id_token", "idToken"));
            }

            foreach (var candidate in candidates)
            {
                var dict = candidate as IDictionary<string, object>;
                if (dict != null)
                {
                    var id = GetString(dict, "chatgpt_account_id", "chatgptAccountId");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        return id;
                    }
                }

                var text = candidate as string;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var payload = ParseJwtPayload(text);
                    var id = payload == null ? null : GetString(payload, "chatgpt_account_id", "chatgptAccountId");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        return id;
                    }
                }
            }

            return null;
        }

        private static IDictionary<string, object> ParseJwtPayload(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2)
                {
                    return null;
                }

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                while (payload.Length % 4 != 0)
                {
                    payload += "=";
                }

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return new JavaScriptSerializer().DeserializeObject(json) as IDictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private static IDictionary<string, object> ParseObject(object value)
        {
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                return dict;
            }

            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return new JavaScriptSerializer().DeserializeObject(text) as IDictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private static string BodyMessage(object body)
        {
            var text = body as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length > 160 ? text.Substring(0, 160) : text;
            }

            var dict = body as IDictionary<string, object>;
            if (dict != null)
            {
                var error = GetValue(dict, "error", "message");
                return Convert.ToString(error);
            }

            return "";
        }

        private static string GetEnvValue(string envName)
        {
            var value = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Machine);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Process);
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static object GetValue(IDictionary<string, object> dict, params string[] names)
        {
            if (dict == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (dict.ContainsKey(name))
                {
                    return dict[name];
                }
            }

            foreach (var item in dict)
            {
                foreach (var name in names)
                {
                    if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return item.Value;
                    }
                }
            }

            return null;
        }

        private static bool HasKey(IDictionary<string, object> dict, string name)
        {
            return GetValue(dict, name) != null;
        }

        private static IDictionary<string, object> GetDict(IDictionary<string, object> dict, params string[] names)
        {
            return GetValue(dict, names) as IDictionary<string, object>;
        }

        private static string GetString(IDictionary<string, object> dict, params string[] names)
        {
            var value = GetValue(dict, names);
            if (value == null)
            {
                return null;
            }

            var text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static int? ToInt(object value)
        {
            var number = ToLong(value);
            if (!number.HasValue)
            {
                return null;
            }

            if (number.Value > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (number.Value < int.MinValue)
            {
                return int.MinValue;
            }

            return (int)number.Value;
        }

        private static long? ToLong(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                return (long)value;
            }

            if (value is decimal)
            {
                return (long)(decimal)value;
            }

            if (value is double)
            {
                return (long)(double)value;
            }

            long parsed;
            return long.TryParse(Convert.ToString(value), out parsed) ? parsed : (long?)null;
        }

        private static bool? ToBool(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            var text = Convert.ToString(value).Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes")
            {
                return true;
            }

            if (text == "false" || text == "0" || text == "no")
            {
                return false;
            }

            return null;
        }

        private static string NormalizePlanType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToLowerInvariant();
        }

        private static string ShortName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "codex";
            }

            value = value.Trim();
            return value.Length > 24 ? value.Substring(0, 24) : value;
        }

        private static string CleanMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 180 ? value.Substring(0, 180) : value;
        }

        private sealed class QuotaPoolAggregate
        {
            private readonly List<int> _remainingPercents;
            private long _weightedLimit;
            private long _weightedRemaining;
            private int _weightedSamples;

            public QuotaPoolAggregate(string windowKey)
            {
                WindowKey = windowKey;
                _remainingPercents = new List<int>();
            }

            public string WindowKey { get; private set; }
            public int AccountCount { get; private set; }
            public string ResetLabel { get; private set; }

            public bool HasData
            {
                get { return AccountCount > 0 && RemainingPercent.HasValue; }
            }

            public int? RemainingPercent
            {
                get
                {
                    if (_weightedLimit > 0 && _weightedSamples == AccountCount)
                    {
                        return ClampPercent((int)Math.Round(_weightedRemaining * 100.0 / _weightedLimit));
                    }

                    if (_remainingPercents.Count == 0)
                    {
                        return null;
                    }

                    var total = 0;
                    foreach (var value in _remainingPercents)
                    {
                        total += value;
                    }

                    return ClampPercent((int)Math.Round(total / (double)_remainingPercents.Count));
                }
            }

            public void Add(CodexQuotaWindow window)
            {
                var remainingPercent = GetWindowRemainingPercent(window);
                if (!remainingPercent.HasValue)
                {
                    return;
                }

                AccountCount++;
                _remainingPercents.Add(remainingPercent.Value);
                if (string.IsNullOrWhiteSpace(ResetLabel))
                {
                    ResetLabel = window.ResetLabel;
                }

                if (window.Limit.HasValue && window.Limit.Value > 0)
                {
                    long remaining;
                    if (window.Remaining.HasValue)
                    {
                        remaining = window.Remaining.Value;
                    }
                    else if (window.Used.HasValue)
                    {
                        remaining = window.Limit.Value - window.Used.Value;
                    }
                    else
                    {
                        remaining = -1;
                    }

                    if (remaining >= 0)
                    {
                        _weightedLimit += window.Limit.Value;
                        _weightedRemaining += Math.Max(0, Math.Min(window.Limit.Value, remaining));
                        _weightedSamples++;
                    }
                }
            }
        }

        private sealed class UsageTotals
        {
            public long Success { get; set; }
            public long Failed { get; set; }
            public long Total { get { return Success + Failed; } }
        }

        private sealed class AuthFileInfo
        {
            public string Name { get; set; }
            public string AuthIndex { get; set; }
            public string AccountId { get; set; }
            public string PlanType { get; set; }
            public long Success { get; set; }
            public long Failed { get; set; }
            public IDictionary<string, object> Raw { get; set; }
        }

        private sealed class CodexQuotaResult
        {
            public string Name { get; set; }
            public string PlanType { get; set; }
            public string Error { get; set; }
            public List<CodexQuotaWindow> Windows { get; private set; }

            public CodexQuotaResult()
            {
                Windows = new List<CodexQuotaWindow>();
            }
        }

        private sealed class CodexQuotaWindow
        {
            public string Category { get; set; }
            public string Label { get; set; }
            public string WindowKey { get; set; }
            public int? UsedPercent { get; set; }
            public long? Used { get; set; }
            public long? Limit { get; set; }
            public long? Remaining { get; set; }
            public string ResetLabel { get; set; }
        }
    }

    internal static class StatusParser
    {
        public static StatusSnapshot Parse(string body, string path)
        {
            var snapshot = new StatusSnapshot
            {
                Online = true,
                SourcePath = path,
                CheckedAt = DateTime.Now,
                RawSummary = Summarize(body)
            };

            if (string.IsNullOrWhiteSpace(body))
            {
                snapshot.Message = "接口在线，但响应为空";
                return snapshot;
            }

            try
            {
                var serializer = new JavaScriptSerializer();
                var data = serializer.DeserializeObject(body);
                var values = new List<KeyValuePair<string, object>>();
                Flatten(data, "", values);

                snapshot.Used = FindInt(values, new[] { "used", "usedcount", "usedrequests", "usedmessages", "usedconversations", "requestcount", "requests", "conversationcount", "conversations", "messagecount", "messages" });
                snapshot.Limit = FindInt(values, new[] { "limit", "quota", "max", "cap", "requestlimit", "conversationlimit", "messagelimit", "totalquota", "hardlimit" });
                snapshot.Remaining = FindInt(values, new[] { "remaining", "remain", "left", "available", "requestsleft", "messagesleft", "conversationsleft" });
                snapshot.Message = FindString(values, new[] { "message", "status", "detail", "description" });

                if (!snapshot.Used.HasValue && snapshot.Limit.HasValue && snapshot.Remaining.HasValue)
                {
                    snapshot.Used = Math.Max(0, snapshot.Limit.Value - snapshot.Remaining.Value);
                }

                if (!snapshot.Remaining.HasValue && snapshot.Limit.HasValue && snapshot.Used.HasValue)
                {
                    snapshot.Remaining = Math.Max(0, snapshot.Limit.Value - snapshot.Used.Value);
                }

                if (string.IsNullOrWhiteSpace(snapshot.Message))
                {
                    snapshot.Message = snapshot.HasQuotaData ? "已读取额度字段" : "接口在线，但未提供额度字段";
                }
            }
            catch
            {
                snapshot.Message = "接口在线，但不是可解析 JSON";
            }

            return snapshot;
        }

        private static void Flatten(object value, string prefix, List<KeyValuePair<string, object>> values)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                foreach (var item in dictionary)
                {
                    var key = string.IsNullOrWhiteSpace(prefix) ? item.Key : prefix + "." + item.Key;
                    values.Add(new KeyValuePair<string, object>(key, item.Value));
                    Flatten(item.Value, key, values);
                }
                return;
            }

            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                var i = 0;
                foreach (var item in array)
                {
                    Flatten(item, prefix + "[" + i + "]", values);
                    i++;
                }
            }
        }

        private static int? FindInt(List<KeyValuePair<string, object>> values, string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var item in values)
                {
                    if (NormalizeKey(item.Key).EndsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        var number = ToInt(item.Value);
                        if (number.HasValue)
                        {
                            return number.Value;
                        }
                    }
                }
            }

            return null;
        }

        private static string FindString(List<KeyValuePair<string, object>> values, string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var item in values)
                {
                    if (NormalizeKey(item.Key).EndsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        var text = item.Value as string;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                }
            }

            return null;
        }

        private static int? ToInt(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                var number = (long)value;
                return number > int.MaxValue ? int.MaxValue : (int)number;
            }

            if (value is decimal)
            {
                return (int)(decimal)value;
            }

            if (value is double)
            {
                return (int)(double)value;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : (int?)null;
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "";
            }

            var builder = new StringBuilder();
            foreach (var ch in key)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }
            return builder.ToString();
        }

        private static string Summarize(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "";
            }

            var compact = body.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            while (compact.Contains("  "))
            {
                compact = compact.Replace("  ", " ");
            }

            compact = compact.Trim();
            return compact.Length > 300 ? compact.Substring(0, 300) : compact;
        }
    }

    internal sealed class StatusSnapshot
    {
        public bool Online { get; set; }
        public string SourcePath { get; set; }
        public DateTime CheckedAt { get; set; }
        public int? Used { get; set; }
        public int? Limit { get; set; }
        public int? Remaining { get; set; }
        public long? CallSuccess { get; set; }
        public long? CallFailed { get; set; }
        public long? TokenTotal { get; set; }
        public long? TokenInput { get; set; }
        public long? TokenOutput { get; set; }
        public long? TokenReasoning { get; set; }
        public int? Quota5hRemaining { get; set; }
        public int? Quota7dRemaining { get; set; }
        public int? Quota5hAccountCount { get; set; }
        public int? Quota7dAccountCount { get; set; }
        public string Quota5hReset { get; set; }
        public string Quota7dReset { get; set; }
        public string Message { get; set; }
        public string RawSummary { get; set; }

        public bool HasQuotaData
        {
            get { return Used.HasValue || Limit.HasValue || Remaining.HasValue; }
        }

        public static StatusSnapshot Error(string message)
        {
            return new StatusSnapshot
            {
                Online = false,
                CheckedAt = DateTime.Now,
                Message = message
            };
        }

        public Color GetColor()
        {
            if (!Online)
            {
                return Color.Firebrick;
            }

            if (!HasQuotaData)
            {
                return Color.SteelBlue;
            }

            if (Limit.HasValue && Remaining.HasValue && Limit.Value > 0)
            {
                var ratio = Remaining.Value / (double)Limit.Value;
                if (ratio <= 0.1)
                {
                    return Color.Firebrick;
                }

                if (ratio <= 0.3)
                {
                    return Color.DarkOrange;
                }
            }

            return Color.SeaGreen;
        }

        public string GetIconText()
        {
            return GetIconText(0);
        }

        public string GetIconText(int displayMode)
        {
            if (!Online)
            {
                return "ERR";
            }

            if (displayMode % 2 == 0 && CallTotal.HasValue)
            {
                return TrimNumber(CallTotal.Value);
            }

            if (Limit.HasValue && Used.HasValue && Limit.Value > 0)
            {
                var percent = Math.Max(0, Math.Min(999, (int)Math.Round(Used.Value * 100.0 / Limit.Value)));
                return percent + "%";
            }

            if (Remaining.HasValue)
            {
                return TrimNumber(Remaining.Value);
            }

            if (Used.HasValue)
            {
                return TrimNumber(Used.Value);
            }

            return "OK";
        }

        public string GetWidgetText()
        {
            if (!Online)
            {
                return "CPA ERR";
            }

            return GetCallsText() + " | 5h " + GetQuota5hText() + " | 7d " + GetQuota7dText();
        }

        public string GetCallsText()
        {
            var calls = CallTotal.HasValue ? FormatLong(CallTotal.Value) : "--";
            var tokens = TokenTotal.HasValue ? FormatLong(TokenTotal.Value) : "--";
            var fail = CallFailed.HasValue && CallFailed.Value > 0 ? " F" + FormatLong(CallFailed.Value) : "";
            return "Calls " + calls + fail + " | Tokens " + tokens;
        }

        public string GetCompactCallsText()
        {
            var calls = CallTotal.HasValue ? FormatLong(CallTotal.Value) : "--";
            var tokens = TokenTotal.HasValue ? FormatLong(TokenTotal.Value) : "--";
            var fail = CallFailed.HasValue && CallFailed.Value > 0 ? " F" + FormatLong(CallFailed.Value) : "";
            return "Calls " + calls + fail + " | Tok " + tokens;
        }

        public static string FormatPercent(int? value)
        {
            return value.HasValue ? value.Value + "%" : "--";
        }

        public string GetQuota5hText()
        {
            return FormatQuotaPool(Quota5hRemaining, Quota5hAccountCount);
        }

        public string GetQuota7dText()
        {
            return FormatQuotaPool(Quota7dRemaining, Quota7dAccountCount);
        }

        private static string FormatQuotaPool(int? remaining, int? accountCount)
        {
            if (!remaining.HasValue)
            {
                return "--";
            }

            var text = remaining.Value + "%";
            if (accountCount.HasValue && accountCount.Value > 0)
            {
                text += " x" + accountCount.Value;
            }

            return text;
        }

        public long? CallTotal
        {
            get
            {
                if (!CallSuccess.HasValue && !CallFailed.HasValue)
                {
                    return null;
                }

                return (CallSuccess ?? 0) + (CallFailed ?? 0);
            }
        }

        public string GetTooltip()
        {
            var parts = new List<string>();
            parts.Add(Online ? "Codex 在线" : "Codex 异常");
            if (HasQuotaData)
            {
                parts.Add(GetQuotaLine());
                if (!string.IsNullOrWhiteSpace(Message))
                {
                    parts.Add(Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(Message))
            {
                parts.Add(Message);
            }
            return string.Join(" | ", parts.ToArray());
        }

        public string GetDetail()
        {
            var builder = new StringBuilder();
            builder.AppendLine(Online ? "接口在线" : "接口异常");
            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                builder.AppendLine("来源：" + SourcePath);
            }

            if (HasQuotaData)
            {
                builder.AppendLine(GetQuotaLine());
            }

            if (!string.IsNullOrWhiteSpace(Message))
            {
                builder.AppendLine("信息：" + Message);
            }

            builder.AppendLine("检查时间：" + CheckedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!string.IsNullOrWhiteSpace(RawSummary))
            {
                builder.AppendLine("响应摘要：" + RawSummary);
            }

            return builder.ToString().Trim();
        }

        private string GetQuotaLine()
        {
            if (Quota5hRemaining.HasValue || Quota7dRemaining.HasValue)
            {
                var poolParts = new List<string>();
                if (Quota5hRemaining.HasValue)
                {
                    poolParts.Add("5h " + GetQuota5hText());
                }

                if (Quota7dRemaining.HasValue)
                {
                    poolParts.Add("7d " + GetQuota7dText());
                }

                return "Quota pool " + string.Join(", ", poolParts.ToArray());
            }

            if (Used.HasValue && Limit.HasValue && Limit.Value == 100 && Remaining.HasValue)
            {
                return "Quota used " + Used.Value + "%, remaining " + Remaining.Value + "%";
            }

            var parts = new List<string>();
            if (Used.HasValue)
            {
                parts.Add("已用 " + Used.Value);
            }

            if (Limit.HasValue)
            {
                parts.Add("限额 " + Limit.Value);
            }

            if (Remaining.HasValue)
            {
                parts.Add("剩余 " + Remaining.Value);
            }

            return string.Join("，", parts.ToArray());
        }

        private static string TrimNumber(int value)
        {
            if (value > 999)
            {
                return "999";
            }

            if (value < -99)
            {
                return "-99";
            }

            return value.ToString();
        }

        private static string TrimNumber(long value)
        {
            if (value > 999)
            {
                return "999";
            }

            if (value < -99)
            {
                return "-99";
            }

            return value.ToString();
        }

        private static string FormatLong(long value)
        {
            if (value >= 1000000)
            {
                return (value / 1000000D).ToString("0.#") + "M";
            }

            if (value >= 1000)
            {
                return (value / 1000D).ToString("0.#") + "K";
            }

            return value.ToString();
        }
    }

    internal sealed class StatusWidget : Form
    {
        private const int WidgetWidth = 236;
        private const int WidgetHeight = 46;
        private static readonly Color TransparentBackColor = Color.FromArgb(255, 1, 2, 3);
        private Label _callsLabel;
        private Label _fiveHourPercentLabel;
        private Label _weekPercentLabel;
        private Panel _fiveHourTrack;
        private Panel _weekTrack;
        private Panel _fiveHourFill;
        private Panel _weekFill;
        private readonly string _statePath;
        private readonly WidgetState _state;
        private bool _positionApplied;

        public StatusWidget(ContextMenuStrip menu, string statePath)
        {
            _statePath = statePath;
            _state = WidgetState.Load(_statePath);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = TransparentBackColor;
            TransparencyKey = TransparentBackColor;
            ForeColor = Color.White;
            Opacity = 1.0;
            Width = WidgetWidth;
            Height = WidgetHeight;
            MinimumSize = new Size(WidgetWidth, WidgetHeight);
            MaximumSize = new Size(WidgetWidth, WidgetHeight);
            ContextMenuStrip = menu;

            _callsLabel = new Label
            {
                Left = 7,
                Top = 1,
                Width = 222,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.25F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Text = "Calls --"
            };
            Controls.Add(_callsLabel);

            AddBarRow(menu, "5h", 24, out _fiveHourTrack, out _fiveHourFill, out _fiveHourPercentLabel);
            AddBarRow(menu, "7d", 35, out _weekTrack, out _weekFill, out _weekPercentLabel);

            MouseDown += MoveWithMouse;
            HookMouse(this);
            ApplyInitialPosition();
        }

        public void UpdateStatus(StatusSnapshot status)
        {
            if (status == null)
            {
                _callsLabel.Text = "Calls --";
                _callsLabel.ForeColor = Color.White;
                SetBar(_fiveHourTrack, _fiveHourFill, _fiveHourPercentLabel, null);
                SetBar(_weekTrack, _weekFill, _weekPercentLabel, null);
                KeepTransparentBackground();
            }
            else if (!status.Online)
            {
                _callsLabel.Text = "CPA ERR";
                _callsLabel.ForeColor = Color.FromArgb(255, 95, 86);
                SetBar(_fiveHourTrack, _fiveHourFill, _fiveHourPercentLabel, null);
                SetBar(_weekTrack, _weekFill, _weekPercentLabel, null);
                KeepTransparentBackground();
            }
            else
            {
                _callsLabel.Text = status.GetCompactCallsText();
                _callsLabel.ForeColor = Color.White;
                SetBar(_fiveHourTrack, _fiveHourFill, _fiveHourPercentLabel, status.Quota5hRemaining, status.GetQuota5hText());
                SetBar(_weekTrack, _weekFill, _weekPercentLabel, status.Quota7dRemaining, status.GetQuota7dText());
                KeepTransparentBackground();
            }
        }

        public void SetVisible(bool visible)
        {
            if (visible)
            {
                if (!Visible)
                {
                    ApplyInitialPosition();
                    Show();
                }

                EnsureVisible(true);
            }
            else
            {
                Hide();
            }
        }

        public void EnsureVisible(bool shouldBeVisible)
        {
            EnsureVisibleCore(shouldBeVisible, true);
        }

        public void EnsureVisibleLight(bool shouldBeVisible)
        {
            EnsureVisibleCore(shouldBeVisible, false);
        }

        private void EnsureVisibleCore(bool shouldBeVisible, bool reassertTopMost)
        {
            if (!shouldBeVisible || IsDisposed)
            {
                return;
            }

            if (!Visible)
            {
                ApplyInitialPosition();
                Show();
            }

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            KeepTransparentBackground();
            if (reassertTopMost)
            {
                TopMost = false;
                TopMost = true;
                ShowWindow(Handle, 4);
                SetWindowPos(
                    Handle,
                    new IntPtr(-1),
                    0,
                    0,
                    0,
                    0,
                    0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200);
                Invalidate(true);
                Update();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_positionApplied)
            {
                ApplyInitialPosition();
            }

            EnsureVisible(true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;
                return cp;
            }
        }

        private void ApplyInitialPosition()
        {
            if (_state.HasCustomPosition && IsPositionVisible(_state.Left, _state.Top, Width, Height))
            {
                Left = _state.Left;
                Top = _state.Top;
            }
            else
            {
                PositionNearTaskbar();
            }

            _positionApplied = true;
        }

        private void PositionNearTaskbar()
        {
            var screen = Screen.PrimaryScreen;
            var bounds = screen.Bounds;
            var area = screen.WorkingArea;
            var bottomTaskbarHeight = Math.Max(0, bounds.Bottom - area.Bottom);

            var left = bounds.Right - Width - 140;
            var top = area.Bottom - Height - 4;

            if (bottomTaskbarHeight >= Height / 2)
            {
                top = area.Bottom + Math.Max(0, (bottomTaskbarHeight - Height) / 2);
            }
            else if (area.Top > bounds.Top)
            {
                top = bounds.Top + Math.Max(0, (area.Top - bounds.Top - Height) / 2);
            }

            Left = Clamp(left, bounds.Left + 4, bounds.Right - Width - 4);
            Top = Clamp(top, bounds.Top + 2, bounds.Bottom - Height - 2);
        }

        private void KeepTransparentBackground()
        {
            if (BackColor != TransparentBackColor)
            {
                BackColor = TransparentBackColor;
            }

            if (TransparencyKey != TransparentBackColor)
            {
                TransparencyKey = TransparentBackColor;
            }
        }

        private void AddBarRow(
            ContextMenuStrip menu,
            string label,
            int top,
            out Panel track,
            out Panel fill,
            out Label percentLabel)
        {
            var nameLabel = new Label
            {
                Left = 7,
                Top = top - 6,
                Width = 22,
                Height = 16,
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 7F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                ContextMenuStrip = menu
            };

            track = new Panel
            {
                Left = 30,
                Top = top,
                Width = 146,
                Height = 6,
                BackColor = Color.FromArgb(70, 80, 76),
                ContextMenuStrip = menu
            };

            fill = new Panel
            {
                Left = 0,
                Top = 0,
                Width = 0,
                Height = track.Height,
                BackColor = Color.SeaGreen,
                ContextMenuStrip = menu
            };
            track.Controls.Add(fill);

            percentLabel = new Label
            {
                Left = 180,
                Top = top - 6,
                Width = 49,
                Height = 16,
                Text = "--",
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 7F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                ContextMenuStrip = menu
            };

            Controls.Add(nameLabel);
            Controls.Add(track);
            Controls.Add(percentLabel);
        }

        private static void SetBar(Panel track, Panel fill, Label percentLabel, int? remaining)
        {
            SetBar(track, fill, percentLabel, remaining, null);
        }

        private static void SetBar(Panel track, Panel fill, Label percentLabel, int? remaining, string labelText)
        {
            if (!remaining.HasValue)
            {
                fill.Width = 0;
                fill.BackColor = Color.DimGray;
                percentLabel.Text = "--";
                return;
            }

            var value = Math.Max(0, Math.Min(100, remaining.Value));
            fill.Width = (int)Math.Round(track.Width * value / 100.0);
            fill.BackColor = BarColor(value);
            percentLabel.Text = string.IsNullOrWhiteSpace(labelText) ? value + "%" : labelText;
        }

        private static Color BarColor(int remaining)
        {
            if (remaining <= 10)
            {
                return Color.FromArgb(220, 70, 60);
            }

            if (remaining <= 30)
            {
                return Color.FromArgb(224, 160, 28);
            }

            return Color.FromArgb(38, 190, 112);
        }

        private void HookMouse(Control control)
        {
            control.MouseDown += MoveWithMouse;
            foreach (Control child in control.Controls)
            {
                HookMouse(child);
            }
        }

        private void MoveWithMouse(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ReleaseCapture();
            var oldLocation = Location;
            SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
            if (Location != oldLocation)
            {
                SavePosition();
            }
        }

        private void SavePosition()
        {
            _state.Left = Left;
            _state.Top = Top;
            _state.HasCustomPosition = true;
            _state.Save(_statePath);
        }

        private static bool IsPositionVisible(int left, int top, int width, int height)
        {
            var rect = new Rectangle(left, top, width, height);
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.Bounds.IntersectsWith(rect))
                {
                    return true;
                }
            }

            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
            {
                return min;
            }

            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);
    }

    internal static class IconPainter
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(string text, Color background)
        {
            using (var bitmap = new Bitmap(16, 16))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(background))
            using (var textBrush = new SolidBrush(Color.White))
            using (var borderPen = new Pen(Color.FromArgb(180, Color.White)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(brush, 0, 0, 15, 15);
                graphics.DrawEllipse(borderPen, 0, 0, 15, 15);

                text = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim();
                if (text.Length > 4)
                {
                    text = text.Substring(0, 4);
                }

                var fontSize = text.Length <= 2 ? 7.5f : text.Length == 3 ? 6.5f : 5.5f;
                using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var size = graphics.MeasureString(text, font);
                    var x = (16 - size.Width) / 2f;
                    var y = (16 - size.Height) / 2f - 0.5f;
                    graphics.DrawString(text, font, textBrush, x, y);
                }

                var handle = bitmap.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(handle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }
}
