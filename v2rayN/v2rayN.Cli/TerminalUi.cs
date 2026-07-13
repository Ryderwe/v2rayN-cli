using System.Globalization;
using System.Text;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;

namespace v2rayN.Cli;

internal sealed class TerminalUi
{
    private const string EnterAltScreen = "\e[?1049h\e[?25l";
    private const string LeaveAltScreen = "\e[?25h\e[?1049l";
    private const string ClearScreen = "\e[2J\e[H";
    private const string ResetStyle = "\e[0m";
    private const string InverseStyle = "\e[7m";
    private const string BoldCyan = "\e[1;36m";
    private const string Yellow = "\e[33m";

    private readonly Config _config;
    private readonly int[] _selected = new int[4];
    private readonly string[] _filters = new string[4];
    private List<ProfileItem> _nodes = [];
    private List<SubItem> _subscriptions = [];
    private Dictionary<string, string> _subscriptionNames = [];
    private Dictionary<string, int> _delays = [];
    private List<string> _logLines = [];
    private CliRuntimeState _runtimeState = new();
    private UiPage _page;
    private string _message;
    private bool _exit;

    public TerminalUi(Config config)
    {
        _config = config;
        _message = T("就绪。按 ? 查看快捷键。", "Ready. Press ? for shortcuts.");
    }

    public async Task<int> RunAsync()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            throw new CliException(T("终端界面需要可交互的 TTY，不能重定向输入或输出。", "The terminal UI requires an interactive TTY; redirected input/output is not supported."), 1);
        }
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(T("当前终端不支持全屏 ANSI 界面（TERM=dumb）。", "This terminal does not support the full-screen ANSI UI (TERM=dumb)."), 1);
        }

        EnterScreen();
        try
        {
            await RefreshAsync();
            Render();
            var lastRefresh = DateTime.UtcNow;
            while (!_exit)
            {
                if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 1)
                {
                    await RefreshAsync();
                    Render();
                    lastRefresh = DateTime.UtcNow;
                }

                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                await HandleKeyAsync(key);
                await RefreshAsync();
                Render();
                lastRefresh = DateTime.UtcNow;
            }
        }
        finally
        {
            LeaveScreen();
        }
        return 0;
    }

    private async Task RefreshAsync()
    {
        _nodes = (await AppManager.Instance.ProfileItems(string.Empty) ?? [])
            .OrderBy(x => ProfileExManager.Instance.GetSort(x.IndexId))
            .ThenBy(x => x.Remarks, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _subscriptions = await AppManager.Instance.SubItems() ?? [];
        _subscriptionNames = _subscriptions.ToDictionary(x => x.Id, x => x.Remarks);
        _delays = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(x => x.IndexId, x => x.Delay);
        _runtimeState = CliRuntime.ReadState();

        if (File.Exists(CliRuntime.LogPath))
        {
            try
            {
                _logLines = File.ReadLines(CliRuntime.LogPath).TakeLast(500).ToList();
            }
            catch
            {
                _logLines = [T("日志文件暂时不可读。", "The log file is temporarily unavailable.")];
            }
        }
        else
        {
            _logLines = [T("日志文件尚未创建。", "The log file has not been created yet.")];
        }
        ClampSelection();
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            _exit = true;
            return;
        }
        if (key.Key == ConsoleKey.Tab || key.Key == ConsoleKey.RightArrow)
        {
            _page = (UiPage)(((int)_page + 1) % 4);
            return;
        }
        if (key.Key == ConsoleKey.LeftArrow)
        {
            _page = (UiPage)(((int)_page + 3) % 4);
            return;
        }
        if (key.KeyChar is >= '1' and <= '4')
        {
            _page = (UiPage)(key.KeyChar - '1');
            return;
        }
        if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.K)
        {
            MoveSelection(-1);
            return;
        }
        if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.J)
        {
            MoveSelection(1);
            return;
        }
        if (key.Key == ConsoleKey.Home)
        {
            _selected[(int)_page] = 0;
            return;
        }
        if (key.Key == ConsoleKey.End)
        {
            _selected[(int)_page] = Math.Max(0, CurrentItemCount() - 1);
            return;
        }
        if (key.Key == ConsoleKey.F5)
        {
            _message = T("已刷新。", "Refreshed.");
            return;
        }
        if (key.KeyChar == '?')
        {
            _message = T(
                "Tab/←→ 页面  ↑↓/jk 选择  Enter 操作  / 搜索  a 添加  d 删除  s 启停  R 重启  l 语言  q 退出",
                "Tab/←→ pages  ↑↓/jk select  Enter action  / search  a add  d delete  s start/stop  R restart  l language  q quit");
            return;
        }
        if (key.KeyChar == '/')
        {
            var value = Prompt(T("搜索（留空清除）", "Search (empty clears)"), _filters[(int)_page]);
            _filters[(int)_page] = value ?? _filters[(int)_page];
            _selected[(int)_page] = 0;
            _message = _filters[(int)_page].Length == 0
                ? T("已清除搜索。", "Search cleared.")
                : T($"搜索: {_filters[(int)_page]}", $"Search: {_filters[(int)_page]}");
            return;
        }
        if (key.Key == ConsoleKey.L)
        {
            await ToggleLanguageAsync();
            return;
        }
        if (key.Key == ConsoleKey.S)
        {
            await ToggleServiceAsync();
            return;
        }
        if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            await RestartServiceAsync();
            return;
        }

        switch (_page)
        {
            case UiPage.Nodes:
                await HandleNodesKeyAsync(key);
                break;
            case UiPage.Subscriptions:
                await HandleSubscriptionsKeyAsync(key);
                break;
            case UiPage.Settings:
                await HandleSettingsKeyAsync(key);
                break;
            case UiPage.Logs:
                if (key.Key == ConsoleKey.R)
                {
                    _message = T("日志已刷新。", "Logs refreshed.");
                }
                break;
        }
    }

    private async Task HandleNodesKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
        {
            var node = SelectedNode();
            if (node is null)
            {
                _message = T("没有可选择的节点。", "No node is available for selection.");
                return;
            }
            await ConfigHandler.SetDefaultServerIndex(_config, node.IndexId);
            _message = T($"已选择节点: {node.Remarks}", $"Selected node: {node.Remarks}")
                       + (IsRunning() ? T("；按 Shift+R 重启后生效。", "; press Shift+R to apply it.") : string.Empty);
            return;
        }
        if (key.Key == ConsoleKey.A)
        {
            var value = Prompt(T("输入节点分享链接或 Base64 内容", "Enter a node share link or Base64 content"));
            if (string.IsNullOrWhiteSpace(value))
            {
                _message = T("已取消导入。", "Import cancelled.");
                return;
            }
            var imported = await ConfigHandler.AddBatchServers(_config, value, _config.SubIndexId, false);
            await ProfileExManager.Instance.SaveTo();
            _message = imported > 0
                ? T($"成功导入 {imported} 个节点。", $"Imported {imported} node(s).")
                : T("没有识别到有效节点。", "No valid node was recognized.");
            return;
        }
        if (key.Key == ConsoleKey.D || key.Key == ConsoleKey.Delete)
        {
            var node = SelectedNode();
            if (node is null || !Confirm(T($"删除节点“{node.Remarks}”", $"Delete node \"{node.Remarks}\"")))
            {
                _message = T("已取消删除。", "Delete cancelled.");
                return;
            }
            await ConfigHandler.RemoveServers(_config, [node]);
            if (_config.IndexId == node.IndexId)
            {
                _config.IndexId = string.Empty;
                await ConfigHandler.GetDefaultServer(_config);
            }
            _message = T($"已删除节点: {node.Remarks}", $"Deleted node: {node.Remarks}")
                       + (IsRunning() ? T("；运行实例需要重启。", "; restart the running instance.") : string.Empty);
        }
    }

    private async Task HandleSubscriptionsKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.A)
        {
            var url = Prompt(T("订阅 URL", "Subscription URL"));
            if (string.IsNullOrWhiteSpace(url))
            {
                _message = T("已取消添加订阅。", "Adding the subscription was cancelled.");
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                _message = T("订阅必须是有效的 HTTP/HTTPS URL。", "The subscription must be a valid HTTP/HTTPS URL.");
                return;
            }
            var name = Prompt(T("订阅名称", "Subscription name"), uri.Host);
            var item = new SubItem
            {
                Id = string.Empty,
                Remarks = string.IsNullOrWhiteSpace(name) ? uri.Host : name,
                Url = url,
                Enabled = true,
            };
            var result = await ConfigHandler.AddSubItem(_config, item);
            _message = result == 0
                ? T($"已添加订阅: {item.Remarks}", $"Added subscription: {item.Remarks}")
                : T("订阅添加失败。", "Failed to add the subscription.");
            return;
        }

        var subscription = SelectedSubscription();
        if (subscription is null)
        {
            _message = T("没有可操作的订阅。", "No subscription is available.");
            return;
        }
        if (key.Key is ConsoleKey.Enter or ConsoleKey.U)
        {
            _message = await CaptureOutputAsync(async () =>
            {
                var success = false;
                await SubscriptionHandler.UpdateProcess(_config, subscription.Id, IsRunning(), (notify, message) =>
                {
                    success |= notify;
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        Console.WriteLine(message);
                    }
                    return Task.CompletedTask;
                });
                await ProfileExManager.Instance.SaveTo();
                return success ? 0 : 1;
            }, T($"订阅更新完成: {subscription.Remarks}", $"Subscription updated: {subscription.Remarks}"));
            if (IsRunning())
            {
                _message += T("；按 Shift+R 重启以应用新节点。", "; press Shift+R to apply updated nodes.");
            }
            return;
        }
        if (key.Key == ConsoleKey.Spacebar)
        {
            subscription.Enabled = !subscription.Enabled;
            await ConfigHandler.AddSubItem(_config, subscription);
            _message = T(
                $"订阅 {subscription.Remarks} 已{(subscription.Enabled ? "启用" : "禁用")}。",
                $"Subscription {subscription.Remarks} {(subscription.Enabled ? "enabled" : "disabled")}.");
            return;
        }
        if (key.Key == ConsoleKey.D || key.Key == ConsoleKey.Delete)
        {
            if (!Confirm(T($"删除订阅“{subscription.Remarks}”及其全部节点", $"Delete subscription \"{subscription.Remarks}\" and all its nodes")))
            {
                _message = T("已取消删除。", "Delete cancelled.");
                return;
            }
            await ConfigHandler.DeleteSubItem(_config, subscription.Id);
            _message = T($"已删除订阅: {subscription.Remarks}", $"Deleted subscription: {subscription.Remarks}")
                       + (IsRunning() ? T("；运行实例需要重启。", "; restart the running instance.") : string.Empty);
        }
    }

    private async Task HandleSettingsKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar))
        {
            return;
        }
        var setting = SelectedSetting();
        if (setting is null)
        {
            return;
        }
        var current = setting.Get(_config);
        var value = Prompt(T($"设置 {setting.Key}", $"Set {setting.Key}"), current);
        if (value is null)
        {
            _message = T("已取消修改。", "Edit cancelled.");
            return;
        }
        try
        {
            setting.Set(_config, value);
            if (await ConfigHandler.SaveConfig(_config) != 0)
            {
                _message = T("配置保存失败。", "Failed to save the configuration.");
                return;
            }
            AppManager.Instance.Reset();
            _message = T($"已设置 {setting.Key} = {setting.Get(_config)}", $"Set {setting.Key} = {setting.Get(_config)}")
                       + (IsRunning() ? T("；按 Shift+R 重启后生效。", "; press Shift+R to apply it.") : string.Empty);
        }
        catch (Exception ex)
        {
            _message = ex.Message;
        }
    }

    private async Task ToggleServiceAsync()
    {
        if (IsRunning())
        {
            _message = await CaptureOutputAsync(CliRuntime.StopAsync, T("代理已停止。", "Proxy stopped."));
            return;
        }
        var node = await ConfigHandler.GetDefaultServer(_config);
        if (node is null)
        {
            _message = T("没有可运行的节点。", "No runnable node is available.");
            return;
        }
        _message = await CaptureOutputAsync(
            () => CliRuntime.StartDetachedAsync(node.IndexId),
            T($"代理已启动: {node.Remarks}", $"Proxy started: {node.Remarks}"));
    }

    private async Task RestartServiceAsync()
    {
        var node = await ConfigHandler.GetDefaultServer(_config);
        if (node is null)
        {
            _message = T("没有可运行的节点。", "No runnable node is available.");
            return;
        }
        _message = await CaptureOutputAsync(async () =>
        {
            await CliRuntime.StopAsync();
            return await CliRuntime.StartDetachedAsync(node.IndexId);
        }, T($"代理已重启: {node.Remarks}", $"Proxy restarted: {node.Remarks}"));
    }

    private async Task ToggleLanguageAsync()
    {
        _config.UiItem.CurrentLanguage = IsEnglish ? "zh-Hans" : "en";
        var culture = new CultureInfo(_config.UiItem.CurrentLanguage);
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (await ConfigHandler.SaveConfig(_config) != 0)
        {
            _message = T("语言设置保存失败。", "Failed to save the language setting.");
            return;
        }
        _message = T("已切换为中文。", "Switched to English.");
    }

    private async Task<string> CaptureOutputAsync(Func<Task<int>> action, string successMessage)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(writer);
        try
        {
            var code = await action();
            var lastLine = writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return code == 0 ? successMessage : lastLine ?? T("操作失败。", "Operation failed.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private void Render()
    {
        var width = GetConsoleWidth();
        var height = GetConsoleHeight();
        var builder = new StringBuilder(ClearScreen);
        if (width < 68 || height < 18)
        {
            builder.AppendLine(T(
                "终端窗口太小，请调整到至少 68x18。按 q 退出。",
                "The terminal is too small. Resize it to at least 68x18. Press q to quit."));
            Console.Write(builder.ToString());
            return;
        }

        var alive = IsRunning();
        var stateText = alive ? $"RUN {_runtimeState.Pid}" : "STOP";
        var activeNode = _nodes.FirstOrDefault(x => x.IndexId == _config.IndexId)?.Remarks ?? T("未选择", "Not selected");
        var port = _config.Inbound.First().LocalPort;
        builder.Append(BoldCyan).Append(Fit($" v2rayN-cli TUI  │  {stateText}  │  {Crop(activeNode, 28)}  │  Mixed :{port}", width)).Append(ResetStyle).AppendLine();
        builder.AppendLine(Fit("─".PadRight(width, '─'), width));
        builder.AppendLine(RenderTabs(width));
        builder.AppendLine(Fit("─".PadRight(width, '─'), width));

        var contentHeight = height - 8;
        switch (_page)
        {
            case UiPage.Nodes:
                RenderNodes(builder, width, contentHeight);
                break;
            case UiPage.Subscriptions:
                RenderSubscriptions(builder, width, contentHeight);
                break;
            case UiPage.Settings:
                RenderSettings(builder, width, contentHeight);
                break;
            case UiPage.Logs:
                RenderLogs(builder, width, contentHeight);
                break;
        }

        builder.AppendLine(Fit("─".PadRight(width, '─'), width));
        builder.Append(Yellow).Append(Fit($" {_message}", width)).Append(ResetStyle).AppendLine();
        builder.Append(Fit(T(
            " Tab 页面  ↑↓ 选择  Enter 操作  a 添加  / 搜索  s 启停  R 重启  l English  q 退出",
            " Tab pages  ↑↓ select  Enter action  a add  / search  s start/stop  R restart  l 中文  q quit"), width));
        Console.Write(builder.ToString());
    }

    private string RenderTabs(int width)
    {
        var tabs = IsEnglish
            ? new[] { "1 Nodes", "2 Subscriptions", "3 Settings", "4 Logs" }
            : new[] { "1 节点", "2 订阅", "3 配置", "4 日志" };
        var builder = new StringBuilder(" ");
        for (var i = 0; i < tabs.Length; i++)
        {
            builder.Append(i == (int)_page ? InverseStyle : string.Empty)
                .Append(' ').Append(tabs[i]).Append(' ')
                .Append(i == (int)_page ? ResetStyle : string.Empty)
                .Append("   ");
        }
        var filter = _filters[(int)_page];
        if (!string.IsNullOrEmpty(filter))
        {
            builder.Append(T($"搜索: {filter}", $"Search: {filter}"));
        }
        return FitAnsi(builder.ToString(), width);
    }

    private void RenderNodes(StringBuilder builder, int width, int height)
    {
        var nodes = FilteredNodes();
        builder.AppendLine(Fit(T(
            " [a 添加/导入节点]  [Enter 选择]  [d 删除]  [/ 搜索]  [s 启停代理]",
            " [a Add/import node]  [Enter Select]  [d Delete]  [/ Search]  [s Start/stop]"), width));
        builder.AppendLine(Fit(T(
            " ACT  TYPE          DELAY   NAME                             ADDRESS / SUBSCRIPTION",
            " ACT  TYPE          DELAY   NAME                             ADDRESS / SUBSCRIPTION"), width));
        if (nodes.Count == 0)
        {
            RenderEmpty(builder, width, height - 2, T(
                "没有节点。按 a 添加 VLESS/VMess/Trojan/SS 等分享链接。",
                "No nodes. Press a to add a VLESS/VMess/Trojan/SS share link."));
            return;
        }
        var selected = _selected[(int)UiPage.Nodes];
        var start = Math.Max(0, selected - height + 3);
        for (var row = 0; row < height - 2; row++)
        {
            var index = start + row;
            if (index >= nodes.Count)
            {
                builder.AppendLine(new string(' ', width));
                continue;
            }
            var node = nodes[index];
            var address = node.IsComplex() ? "-" : $"{node.Address}:{node.Port}";
            var subscription = _subscriptionNames.GetValueOrDefault(node.Subid, "-");
            var delay = _delays.GetValueOrDefault(node.IndexId);
            var line = $" {(node.IndexId == _config.IndexId ? "●" : " "),3}  {node.ConfigType,-13} {(delay > 0 ? $"{delay}ms" : "-"),-7} {Crop(node.Remarks, 32),-32} {address}  [{subscription}]";
            AppendSelectableLine(builder, line, width, index == selected);
        }
    }

    private void RenderSubscriptions(StringBuilder builder, int width, int height)
    {
        var items = FilteredSubscriptions();
        builder.AppendLine(Fit(T(
            " [a 添加/导入订阅]  [Enter/u 更新]  [Space 启用/禁用]  [d 删除]  [/ 搜索]",
            " [a Add/import subscription]  [Enter/u Update]  [Space Enable/disable]  [d Delete]"), width));
        builder.AppendLine(Fit(T(
            " EN   INTERVAL   NAME                           URL",
            " EN   INTERVAL   NAME                           URL"), width));
        if (items.Count == 0)
        {
            RenderEmpty(builder, width, height - 2, T(
                "没有订阅。按 a 粘贴订阅链接，添加后按 Enter 更新。",
                "No subscriptions. Press a to paste a subscription URL, then Enter to update."));
            return;
        }
        var selected = _selected[(int)UiPage.Subscriptions];
        var start = Math.Max(0, selected - height + 3);
        for (var row = 0; row < height - 2; row++)
        {
            var index = start + row;
            if (index >= items.Count)
            {
                builder.AppendLine(new string(' ', width));
                continue;
            }
            var item = items[index];
            var line = $" {(item.Enabled ? "●" : "○"),2}   {item.AutoUpdateInterval,5} min   {Crop(item.Remarks, 30),-30} {DisplaySubscriptionUrl(item.Url)}";
            AppendSelectableLine(builder, line, width, index == selected);
        }
    }

    private void RenderSettings(StringBuilder builder, int width, int height)
    {
        var settings = FilteredSettings();
        builder.AppendLine(Fit(T(
            " [Enter 编辑配置]  [/ 搜索]  [l English]  [Shift+R 应用并重启]",
            " [Enter Edit setting]  [/ Search]  [l 中文]  [Shift+R Apply and restart]"), width));
        builder.AppendLine(Fit(T(
            " CONFIGURATION KEY                         VALUE",
            " CONFIGURATION KEY                         VALUE"), width));
        if (settings.Count == 0)
        {
            RenderEmpty(builder, width, height - 2, T(
                "没有匹配的配置项。按 / 清除搜索。",
                "No matching settings. Press / to clear the search."));
            return;
        }
        var selected = _selected[(int)UiPage.Settings];
        var start = Math.Max(0, selected - height + 3);
        for (var row = 0; row < height - 2; row++)
        {
            var index = start + row;
            if (index >= settings.Count)
            {
                builder.AppendLine(new string(' ', width));
                continue;
            }
            var setting = settings[index];
            var line = $" {setting.Key,-42} {setting.Get(_config)}";
            AppendSelectableLine(builder, line, width, index == selected);
        }
    }

    private void RenderLogs(StringBuilder builder, int width, int height)
    {
        var filter = _filters[(int)UiPage.Logs];
        var lines = string.IsNullOrWhiteSpace(filter)
            ? _logLines
            : _logLines.Where(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        builder.AppendLine(Fit(T(
            " [F5/r 刷新日志]  [/ 搜索日志]",
            " [F5/r Refresh logs]  [/ Search logs]"), width));
        builder.AppendLine(Fit($" {CliRuntime.LogPath}", width));
        foreach (var line in lines.TakeLast(height - 2))
        {
            builder.AppendLine(Fit(" " + StripControlCharacters(line), width));
        }
        for (var i = Math.Min(lines.Count, height - 2); i < height - 2; i++)
        {
            builder.AppendLine(new string(' ', width));
        }
    }

    private void RenderEmpty(StringBuilder builder, int width, int height, string text)
    {
        builder.AppendLine(Fit(" " + text, width));
        for (var i = 1; i < height; i++)
        {
            builder.AppendLine(new string(' ', width));
        }
    }

    private static void AppendSelectableLine(StringBuilder builder, string line, int width, bool selected)
    {
        if (selected)
        {
            builder.Append(InverseStyle).Append(Fit(line, width)).Append(ResetStyle).AppendLine();
        }
        else
        {
            builder.AppendLine(Fit(line, width));
        }
    }

    private void MoveSelection(int delta)
    {
        var count = CurrentItemCount();
        if (count == 0)
        {
            _selected[(int)_page] = 0;
            return;
        }
        _selected[(int)_page] = Math.Clamp(_selected[(int)_page] + delta, 0, count - 1);
    }

    private void ClampSelection()
    {
        for (var i = 0; i < _selected.Length; i++)
        {
            var page = _page;
            _page = (UiPage)i;
            _selected[i] = Math.Clamp(_selected[i], 0, Math.Max(0, CurrentItemCount() - 1));
            _page = page;
        }
    }

    private int CurrentItemCount()
    {
        return _page switch
        {
            UiPage.Nodes => FilteredNodes().Count,
            UiPage.Subscriptions => FilteredSubscriptions().Count,
            UiPage.Settings => FilteredSettings().Count,
            UiPage.Logs => _logLines.Count,
            _ => 0,
        };
    }

    private List<ProfileItem> FilteredNodes()
    {
        var filter = _filters[(int)UiPage.Nodes];
        return string.IsNullOrWhiteSpace(filter)
            ? _nodes
            : _nodes.Where(x => x.Remarks.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || x.Address.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || x.ConfigType.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private List<SubItem> FilteredSubscriptions()
    {
        var filter = _filters[(int)UiPage.Subscriptions];
        return string.IsNullOrWhiteSpace(filter)
            ? _subscriptions
            : _subscriptions.Where(x => x.Remarks.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                        || x.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<ConfigSetting> FilteredSettings()
    {
        var filter = _filters[(int)UiPage.Settings];
        return string.IsNullOrWhiteSpace(filter)
            ? [.. CliSettings.All]
            : CliSettings.All.Where(x => x.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private ProfileItem? SelectedNode()
    {
        var items = FilteredNodes();
        return items.Count == 0 ? null : items[Math.Clamp(_selected[(int)UiPage.Nodes], 0, items.Count - 1)];
    }

    private SubItem? SelectedSubscription()
    {
        var items = FilteredSubscriptions();
        return items.Count == 0 ? null : items[Math.Clamp(_selected[(int)UiPage.Subscriptions], 0, items.Count - 1)];
    }

    private ConfigSetting? SelectedSetting()
    {
        var items = FilteredSettings();
        return items.Count == 0 ? null : items[Math.Clamp(_selected[(int)UiPage.Settings], 0, items.Count - 1)];
    }

    private bool IsRunning() => CliRuntime.IsStateProcessAlive(_runtimeState);

    private string? Prompt(string label, string? defaultValue = null)
    {
        LeaveScreen();
        try
        {
            Console.Write($"{label}{(defaultValue is null ? string.Empty : $" [{defaultValue}]")}: ");
            var value = Console.ReadLine();
            if (value is null)
            {
                return null;
            }
            return value.Length == 0 && defaultValue is not null ? defaultValue : value;
        }
        finally
        {
            EnterScreen();
        }
    }

    private bool Confirm(string prompt)
    {
        var answer = Prompt(T($"{prompt}？输入 y 确认", $"{prompt}? Enter y to confirm"), "n");
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
               || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsEnglish => _config.UiItem.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private string T(string chinese, string english) => IsEnglish ? english : chinese;

    private static void EnterScreen()
    {
        Console.Write(EnterAltScreen + ClearScreen);
    }

    private static void LeaveScreen()
    {
        Console.Write(ResetStyle + LeaveAltScreen);
    }

    private static int GetConsoleWidth()
    {
        try { return Math.Max(1, Console.WindowWidth); }
        catch { return 100; }
    }

    private static int GetConsoleHeight()
    {
        try { return Math.Max(1, Console.WindowHeight); }
        catch { return 30; }
    }

    private static string Crop(string? value, int width)
    {
        value ??= string.Empty;
        if (DisplayWidth(value) <= width)
        {
            return value;
        }
        return Fit(value, Math.Max(1, width - 1)).TrimEnd() + "…";
    }

    private static string Fit(string value, int width)
    {
        value = StripControlCharacters(value);
        var builder = new StringBuilder();
        var currentWidth = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var characterWidth = IsWide(rune) ? 2 : 1;
            if (currentWidth + characterWidth > width)
            {
                break;
            }
            builder.Append(rune.ToString());
            currentWidth += characterWidth;
        }
        if (currentWidth < width)
        {
            builder.Append(' ', width - currentWidth);
        }
        return builder.ToString();
    }

    private static string FitAnsi(string value, int width)
    {
        var plainWidth = DisplayWidth(StripAnsi(value));
        return plainWidth < width ? value + new string(' ', width - plainWidth) : value;
    }

    private static int DisplayWidth(string value) => value.EnumerateRunes().Sum(rune => IsWide(rune) ? 2 : 1);
    private static bool IsWide(Rune rune) => rune.Value >= 0x2e80 && rune.Value is not (0xfeff or 0xff61);

    private static string DisplaySubscriptionUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "(invalid URL)";
        }
        return uri.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(uri.Query) ? string.Empty : "?…");
    }

    private static string StripControlCharacters(string value)
    {
        return new string(value.Where(character => character is '\t' || !char.IsControl(character)).ToArray()).Replace('\t', ' ');
    }

    private static string StripAnsi(string value)
    {
        var builder = new StringBuilder();
        var inEscape = false;
        foreach (var character in value)
        {
            if (character == '\e')
            {
                inEscape = true;
                continue;
            }
            if (inEscape)
            {
                if (char.IsLetter(character))
                {
                    inEscape = false;
                }
                continue;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private enum UiPage
    {
        Nodes,
        Subscriptions,
        Settings,
        Logs,
    }
}
