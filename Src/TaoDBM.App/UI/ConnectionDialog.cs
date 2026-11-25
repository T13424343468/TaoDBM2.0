using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDBM.Core;
using Windows.Storage.Pickers;

namespace TaoDBM.UI;

/// <summary>新建 / 编辑连接对话框（代码构建，Ant Design 风格）。</summary>
public class ConnectionDialog
{
    public ConnectionProfile Profile { get; }

    private readonly ComboBox _engine = new() { Header = "数据库类型", HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 480 };
    /// <summary>批次七：引擎下拉项→枚举映射（含国产分组头，索引不再等于枚举值）。</summary>
    private List<DbEngine>? _engineMap;
    private readonly TextBox _name = new() { Header = "连接名称" };
    private readonly TextBox _host = new() { Header = "主机" };
    private readonly TextBox _port = new() { Header = "端口" };
    private readonly TextBox _user = new() { Header = "用户名" };
    private readonly PasswordBox _pass = new() { Header = "密码" };
    private readonly TextBox _database = new() { Header = "默认数据库" };
    private readonly TextBox _file = new() { Header = "数据库文件路径（SQLite）" };
    private readonly TextBox _params = new() { Header = "附加参数（k=v;k2=v2）" };
    private readonly Button _tnsBtn = new() { Content = "从 tnsnames.ora 解析服务名", BorderThickness = new Thickness(1), CornerRadius = AntTokens.CornerRadius(AntTokens.RadiusMd), Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed };
    private readonly ComboBox _tnsList = new() { Header = "TNS 网络服务名（选中后自动填主机/端口/服务名）", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private List<TnsNames.Entry> _tnsEntries = new();
    // 批次四格3（2026-11-23）：ODBC（DSN）——DSN 下拉枚举本机数据源，选中即回填默认库
    private readonly ComboBox _dsnList = new() { Header = "本机数据源（DSN，选中后自动填默认库）", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly CheckBox _savePwd = new() { Content = "记住密码（本机加密保存）", IsChecked = true };
    private readonly TextBlock _hint = new() { Foreground = Ant.TextSub, FontSize = AntTokens.FontMinor, TextWrapping = TextWrapping.Wrap };
    // SSH 隧道（可选）
    private readonly CheckBox _ssh = new() { Content = "通过 SSH 跳板机连接（隧道）" };
    private readonly TextBox _sshHost = new() { Header = "SSH 主机", PlaceholderText = "跳板机地址" };
    private readonly TextBox _sshPort = new() { Header = "SSH 端口", PlaceholderText = "22" };
    private readonly TextBox _sshUser = new() { Header = "SSH 用户名" };
    private readonly PasswordBox _sshPass = new() { Header = "SSH 密码" };
    // 批次三格4：HTTP 隧道（可选；SSH 优先，HTTP 次之）
    private readonly TextBox _tunnelUrl = new() { Header = "HTTP 隧道端点（可选，如 https://主机/tunnel.php）", PlaceholderText = "留空不启用" };
    // 批次三格5：认证方式（账号密码 / Windows 集成）
    private readonly ComboBox _auth = new() { Header = "认证方式", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new List<string> { "账号密码", "Windows 集成（免密）" } };
    // E①：Redis 哨兵模式——直连走主机/端口，哨兵走 Master 名称＋节点列表
    private readonly ComboBox _redisMode = new() { Header = "连接模式", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new List<string> { "直连", "哨兵（主从自动切换）" }, Visibility = Visibility.Collapsed };
    private readonly TextBox _sentinelMaster = new() { Header = "Master 名称", PlaceholderText = "如 mymaster", Visibility = Visibility.Collapsed };
    private readonly TextBox _sentinelNodes = new() { Header = "哨兵节点列表（每行一个 host:port）", AcceptsReturn = true, Height = 84, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public ConnectionDialog(ConnectionProfile profile)
    {
        Profile = profile;
        // UIA 自动化标识（界面自动化走查定位用）
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_engine, "conn-engine");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_name, "conn-name");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_host, "conn-host");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_port, "conn-port");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_user, "conn-user");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_pass, "conn-pass");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_database, "conn-database");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_file, "conn-file");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_params, "conn-params");
        // 批次七：引擎下拉分组——常规引擎在前，「国产数据库」分组头（不可选）＋六家在后；映射表保序防索引错位
        _engineMap = new List<DbEngine>();
        foreach (var e in EngineInfo.All.Where(x => !EngineInfo.IsDomestic(x)))
        {
            _engine.Items.Add(EngineInfo.Name(e));
            _engineMap.Add(e);
        }
        _engine.Items.Add(new ComboBoxItem { Content = "── 国产数据库 ──", IsEnabled = false, IsTabStop = false });
        foreach (var e in EngineInfo.Domestic)
        {
            _engine.Items.Add(EngineInfo.Name(e));
            _engineMap.Add(e);
        }
        _engine.SelectedIndex = _engineMap.IndexOf(profile.Engine);
        _engine.SelectionChanged += (_, _) => OnEngineChanged();
        _name.Text = profile.DisplayName;
        _host.Text = profile.Host;
        _port.Text = profile.Port > 0 ? profile.Port.ToString() : "";
        _user.Text = profile.User;
        _pass.Password = profile.Password;
        _database.Text = profile.Database;
        _file.Text = profile.Database;
        _params.Text = profile.Parameters;
        _savePwd.IsChecked = profile.SavePassword;
        _sshHost.Text = profile.SshHost;
        _sshPort.Text = profile.SshPort > 0 ? profile.SshPort.ToString() : "22";
        _sshUser.Text = profile.SshUser;
        _sshPass.Password = profile.SshPassword;
        _ssh.IsChecked = !string.IsNullOrEmpty(profile.SshHost);
        _tunnelUrl.Text = profile.TunnelUrl;
        _auth.SelectedIndex = profile.AuthMode == "integrated" ? 1 : 0;
        _auth.SelectionChanged += (_, _) => OnAuthChanged();
        OnAuthChanged();
        // E①：Redis 哨兵回填——节点列表分号串还原为每行一个
        _redisMode.SelectedIndex = profile.RedisMode == "sentinel" ? 1 : 0;
        _sentinelMaster.Text = profile.SentinelMaster;
        _sentinelNodes.Text = profile.SentinelNodes.Replace(";", "\r\n");
        _redisMode.SelectionChanged += (_, _) => OnEngineChanged();   // 模式切换复用引擎可见性联动
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_redisMode, "conn-redis-mode");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_sentinelMaster, "conn-sentinel-master");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_sentinelNodes, "conn-sentinel-nodes");
        _dsnList.SelectionChanged += (_, _) =>
        {
            if (_dsnList.SelectedItem is string dsn) _database.Text = dsn;   // 批次四格3：选中即回填
        };
        _tnsList.SelectionChanged += (_, _) =>
        {
            int i = _tnsList.SelectedIndex;
            if (i < 0 || i >= _tnsEntries.Count) return;
            var e = _tnsEntries[i];
            _host.Text = e.Host;
            if (e.Port > 0) _port.Text = e.Port.ToString();
            if (e.Service.Length > 0) _params.Text = e.Service;
        };
        _ssh.Checked += (_, _) => OnSshChanged(true);
        _ssh.Unchecked += (_, _) => OnSshChanged(false);
        OnSshChanged(_ssh.IsChecked == true);
        OnEngineChanged();
    }

    private void OnSshChanged(bool on)
    {
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        _sshHost.Visibility = _sshPort.Visibility = _sshUser.Visibility = _sshPass.Visibility = vis;
    }

    /// <summary>批次三格5：选 Windows 集成认证时隐藏账号密码框（免密语义）。</summary>
    private void OnAuthChanged()
    {
        bool integrated = _auth.SelectedIndex == 1;
        var vis = integrated ? Visibility.Collapsed : Visibility.Visible;
        _user.Visibility = _pass.Visibility = vis;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_auth, "conn-auth");
    }

    private void OnEngineChanged()
    {
        if (_engineMap == null || _engine.SelectedIndex < 0 || _engine.SelectedIndex >= _engineMap.Count) return;
        var e = _engineMap[_engine.SelectedIndex];
        bool fileBased = EngineInfo.IsFileBased(e);
        bool integrated = _auth.SelectedIndex == 1;   // 批次三格5：集成认证隐藏账号密码（与引擎可见性叠加）
        _host.Visibility = fileBased ? Visibility.Collapsed : Visibility.Visible;
        _port.Visibility = fileBased ? Visibility.Collapsed : Visibility.Visible;
        _user.Visibility = fileBased || integrated ? Visibility.Collapsed : Visibility.Visible;
        _pass.Visibility = fileBased || integrated ? Visibility.Collapsed : Visibility.Visible;
        _database.Visibility = fileBased || e == DbEngine.Odbc ? Visibility.Collapsed : Visibility.Visible;
        _file.Visibility = fileBased ? Visibility.Visible : Visibility.Collapsed;
        // 批次四格3：ODBC 用 DSN 下拉替代默认库输入；主机 / 端口对 DSN 连接无意义，隐藏
        _dsnList.Visibility = e == DbEngine.Odbc ? Visibility.Visible : Visibility.Collapsed;
        _host.Visibility = fileBased || e == DbEngine.Odbc ? Visibility.Collapsed : _host.Visibility;
        if (e == DbEngine.Odbc)
        {
            _host.Visibility = Visibility.Collapsed;
            _port.Visibility = Visibility.Collapsed;
            RefreshDsns();
        }
        else
        {
            _host.Visibility = fileBased ? Visibility.Collapsed : Visibility.Visible;
            _port.Visibility = fileBased ? Visibility.Collapsed : Visibility.Visible;
        }
        _params.Visibility = e == DbEngine.Oracle ? Visibility.Visible : Visibility.Visible; // Oracle service name 也走这一行
        _params.Header = e == DbEngine.Oracle ? "服务名（如 orcl）" : "附加参数（k=v;k2=v2）";
        // E①：Redis 哨兵——模式选择可见；哨兵时隐藏主机/端口，改走 Master 名称＋节点列表
        bool isRedis = e == DbEngine.Redis;
        _redisMode.Visibility = isRedis ? Visibility.Visible : Visibility.Collapsed;
        bool sentinel = isRedis && _redisMode.SelectedIndex == 1;
        _sentinelMaster.Visibility = _sentinelNodes.Visibility = sentinel ? Visibility.Visible : Visibility.Collapsed;
        if (sentinel) { _host.Visibility = Visibility.Collapsed; _port.Visibility = Visibility.Collapsed; }
        _tnsBtn.Visibility = e == DbEngine.Oracle ? Visibility.Visible : Visibility.Collapsed;
        if (e != DbEngine.Oracle) _tnsList.Visibility = Visibility.Collapsed;
        if (_port.Text.Length == 0)
        {
            int def = EngineInfo.DefaultPort(e);
            _port.Text = def > 0 ? def.ToString() : "";
        }
        else if (int.TryParse(_port.Text, out var curPort) && curPort > 0
                 && EngineInfo.All.Any(x => EngineInfo.DefaultPort(x) == curPort))
        {
            // 端口还是某个引擎的默认值（未自定义）→ 跟随新引擎换成对应默认端口；自定义端口保持不动
            int def2 = EngineInfo.DefaultPort(e);
            _port.Text = def2 > 0 ? def2.ToString() : "";
        }
        _hint.Text = e switch
        {
            DbEngine.SQLite => "本地文件即数据库，例如 D:\\data\\app.db；不存在时首次连接会自动创建。",
            DbEngine.Redis => _redisMode.SelectedIndex == 1
                ? "哨兵模式：填 Master 名称与哨兵节点列表，主从切换自动跟随；杀主节点自动切从。"
                : "默认扫描 db0-db15；连接后左侧树中可切换库并浏览键。",
            DbEngine.MongoDB => "MongoDB 无需固定表结构，选中集合后可按 JSON 过滤器查询文档。",
            DbEngine.Odbc => "从下拉选择本机已配置的 ODBC 数据源（DSN）；账号密码可选，附加参数可带 k=v 连接串属性。",
            // 批次七：国产六家提示语（说人话口径）
            DbEngine.OceanBase => "走 MySQL 租户协议；用户名格式 用户@租户#集群（如 root@prod#cluster1），单租户直连可只写用户名。",
            DbEngine.GaussDB => "走 PostgreSQL 协议；库与用户双命名空间，对象树按 模式.表 展示。",
            DbEngine.Dameng => "走 Oracle 兼容方言；首次使用需将厂商驱动 DmProvider.dll 放到程序目录 drivers/dameng 下（缺驱动连接时会给出放置路径指引）。",
            DbEngine.Kingbase => "走 PostgreSQL 兼容协议（默认端口 54321）；大小写敏感模式可在附加参数配置。",
            DbEngine.PolarDB => "走 MySQL 协议；主机填集群地址，连接后自动识别只读节点并标注。",
            DbEngine.Tdsql => "走 MySQL 协议；主机填 Proxy 地址，分片表元数据只读展示，执行计划透传。",
            _ => $"默认端口 {EngineInfo.DefaultPort(e)}，可留空自动填充。",
        };
    }

    /// <summary>批次四格3：枚举本机 DSN 填充下拉（失败静默给空提示，不闪退）。</summary>
    private void RefreshDsns()
    {
        try
        {
            var dsns = OdbcCore.ListDsns();
            _dsnList.Items.Clear();
            foreach (var d in dsns) _dsnList.Items.Add(d);
            _dsnList.Header = dsns.Count > 0
                ? $"本机数据源（DSN，共 {dsns.Count} 项，选中后自动填默认库）"
                : "本机数据源（未枚举到 DSN，可在「附加参数」直接写连接串属性）";
            if (Profile.Database.Length > 0 && dsns.Contains(Profile.Database)) _dsnList.SelectedItem = Profile.Database;
        }
        catch (Exception ex)
        {
            _dsnList.Header = HumanError.Say(ex, "本机数据源（枚举失败") + "）";
        }
    }

    /// <summary>挂到窗口显示；用户点保存返回 true。</summary>
    public async Task<bool> ShowAsync(Window owner, bool isNew)
    {
        // 右侧留出滚动条带：浮层滚动条不再压在输入框右缘上（2026-11-03 真机截图反馈）
        // 批次六格2：窄窗断点——弹层 MinWidth 改 MaxWidth 约束，窄窗不横向溢出
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520, Margin = new Thickness(0, 0, 12, 0) };
        panel.Children.Add(_engine);
        panel.Children.Add(_name);
        panel.Children.Add(_host);
        panel.Children.Add(_port);
        panel.Children.Add(_user);
        panel.Children.Add(_pass);
        panel.Children.Add(_database);
        panel.Children.Add(_dsnList);   // 批次四格3：ODBC DSN 下拉
        panel.Children.Add(_file);
        panel.Children.Add(_params);
        panel.Children.Add(_redisMode);   // E①：Redis 连接模式（直连/哨兵）
        panel.Children.Add(_sentinelMaster);   // E①：哨兵 Master 名称
        panel.Children.Add(_sentinelNodes);   // E①：哨兵节点列表
        panel.Children.Add(_tnsBtn);
        panel.Children.Add(_tnsList);
        panel.Children.Add(_auth);   // 批次三格5：认证方式
        _tnsBtn.Click += (_, _) => _ = PickTns(owner);
        panel.Children.Add(_savePwd);
        panel.Children.Add(_hint);

        var sshPanel = new StackPanel { Spacing = 8 };
        sshPanel.Children.Add(new TextBlock { Text = "SSH 隧道（可选，经跳板机连接内网数据库）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        sshPanel.Children.Add(_ssh);
        sshPanel.Children.Add(_sshHost);
        sshPanel.Children.Add(_sshPort);
        sshPanel.Children.Add(_sshUser);
        sshPanel.Children.Add(_sshPass);
        sshPanel.Children.Add(_tunnelUrl);   // 批次三格4：HTTP 隧道端点
        panel.Children.Add(sshPanel);

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var dlg = new ContentDialog
        {
            Title = isNew ? "新建连接" : "编辑连接",
            Content = scroll,
            PrimaryButtonText = "测试连接",
            SecondaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = owner.Content.XamlRoot,
        };

        while (true)
        {
            var result = await dlg.ShowThemedAsync();
            Apply();
            if (result == ContentDialogResult.Primary)
            {
                bool ok = await TestConn();
                if (!ok) continue;   // 失败留在对话框
                // 测试通过后仍停留，用户可继续点保存
                var ok2 = new ContentDialog
                {
                    Title = "连接成功",
                    Content = new TextBlock { Text = "数据库连接正常，点击“保存”保存该连接。" },
                    CloseButtonText = "好的",
                    XamlRoot = owner.Content.XamlRoot,
                };
                await ok2.ShowThemedAsync();
                continue;
            }
            if (result == ContentDialogResult.Secondary)
            {
                // 发现64：保存前必填校验——非文件引擎主机不能为空（哨兵模式改查 Master 名称），留对话框提示
                if (!EngineInfo.IsFileBased(Profile.Engine) && Profile.RedisMode != "sentinel" && Profile.Host.Length == 0)
                {
                    _hint.Text = "主机还没填：填服务器地址（如 127.0.0.1）再保存。";
                    continue;
                }
                if (Profile.RedisMode == "sentinel" && Profile.SentinelMaster.Length == 0)
                {
                    _hint.Text = "Master 名称还没填：填哨兵的 master 名称再保存。";
                    continue;
                }
            }
            return result == ContentDialogResult.Secondary;
        }
    }

    /// <summary>选 tnsnames.ora 文件并解析；条目填进下拉，选中即回填主机/端口/服务名（避免嵌套对话框）。</summary>
    private async Task PickTns(Window owner)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".ora");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
            _tnsEntries = TnsNames.Parse(text);
            _tnsList.Items.Clear();
            if (_tnsEntries.Count == 0)
            {
                _tnsList.Header = "未从该文件解析出网络服务名";
                _tnsList.Visibility = Visibility.Visible;
                return;
            }
            foreach (var e in _tnsEntries)
                _tnsList.Items.Add($"{e.Name} → {e.Host}:{(e.Port > 0 ? e.Port.ToString() : "默认")}/{(e.Service.Length > 0 ? e.Service : "?")}");
            _tnsList.Header = $"TNS 网络服务名（{file.Name}，共 {_tnsEntries.Count} 项）";
            _tnsList.SelectedIndex = -1;
            _tnsList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _hint.Text = HumanError.Say(ex, "tnsnames.ora 解析失败");
        }
    }

    private void Apply()
    {
        Profile.Engine = _engine.SelectedIndex >= 0 && _engine.SelectedIndex < _engineMap!.Count
            ? _engineMap[_engine.SelectedIndex] : DbEngine.MySQL;
        Profile.DisplayName = string.IsNullOrWhiteSpace(_name.Text) ? EngineInfo.Name(Profile.Engine) : _name.Text.Trim();
        Profile.Host = _host.Text.Trim();
        Profile.Port = int.TryParse(_port.Text, out var p) ? p : 0;
        Profile.User = _user.Text.Trim();
        Profile.Password = _pass.Password;
        Profile.SavePassword = _savePwd.IsChecked == true;
        Profile.Parameters = _params.Text.Trim();
        Profile.Database = EngineInfo.IsFileBased(Profile.Engine) ? _file.Text.Trim() : _database.Text.Trim();
        if (Profile.Engine == DbEngine.Oracle && _params.Text.Trim().Length > 0 && !_params.Text.Contains('=')) { /* 服务名 */ }
        Profile.SshHost = _ssh.IsChecked == true ? _sshHost.Text.Trim() : "";
        Profile.SshPort = int.TryParse(_sshPort.Text, out var sp) ? sp : 22;
        Profile.SshUser = _sshUser.Text.Trim();
        Profile.SshPassword = _sshPass.Password;
        Profile.TunnelUrl = _tunnelUrl.Text.Trim();   // 批次三格4
        Profile.AuthMode = _auth.SelectedIndex == 1 ? "integrated" : "password";   // 批次三格5
        // E①：Redis 哨兵——节点列表每行一个，落盘为分号串
        Profile.RedisMode = Profile.Engine == DbEngine.Redis && _redisMode.SelectedIndex == 1 ? "sentinel" : "direct";
        Profile.SentinelMaster = _sentinelMaster.Text.Trim();
        Profile.SentinelNodes = _sentinelNodes.Text.Trim().Replace("\r\n", ";").Replace('\n', ';');
    }

    private async Task<bool> TestConn()
    {
        try
        {
            using var prov = ProviderFactory.Create(Profile);
            await prov.TestConnection();
            Core.AppLog.Write($"[连接] engine={EngineInfo.Name(Profile.Engine)} ok");
            return true;
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title = "连接失败",
                Content = new TextBlock { Text = HumanError.Say(ex, "连接测试"), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
                XamlRoot = App.Shell!.Content.XamlRoot,
            };
            await err.ShowThemedAsync();
            return false;
        }
    }
}
