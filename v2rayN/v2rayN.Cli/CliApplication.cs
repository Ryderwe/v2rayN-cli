using System.Diagnostics;
using System.Text.Json;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Fmt;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;

namespace v2rayN.Cli;

internal sealed class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private Config _config = null!;
    private bool _initialized;

    public async Task<int> RunAsync(string[] rawArgs)
    {
        if (rawArgs.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = rawArgs[0].ToLowerInvariant();
        var args = new CliArguments(rawArgs.Skip(1));
        switch (command)
        {
            case "help":
            case "--help":
            case "-h":
                PrintHelp();
                return 0;
            case "version":
            case "--version":
            case "-v":
                Console.WriteLine($"v2rayN-cli {Utils.GetVersion()}");
                return 0;
            case "paths":
                PrintPaths();
                return 0;
            case "__daemon":
                await InitializeAsync();
                return await RunCoreAsync(args.Count > 0 ? args[0] : null, daemonMode: true);
            case "nodes":
            case "node":
                await InitializeAsync();
                return await NodesAsync(args);
            case "subscriptions":
            case "subscription":
            case "subs":
            case "sub":
                await InitializeAsync();
                return await SubscriptionsAsync(args);
            case "config":
                await InitializeAsync();
                return await ConfigAsync(args);
            case "ui":
            case "tui":
                await InitializeAsync();
                args.EnsureEmpty();
                return await new TerminalUi(_config).RunAsync();
            case "cores":
            case "core":
                await InitializeAsync();
                return Cores(args);
            case "run":
                await InitializeAsync();
                return await RunCoreAsync(args.Count > 0 ? args[0] : null, daemonMode: false);
            case "start":
                await InitializeAsync();
                return await StartAsync(args);
            case "stop":
                args.EnsureEmpty();
                return await CliRuntime.StopAsync();
            case "restart":
                await InitializeAsync();
                return await RestartAsync(args);
            case "status":
                await InitializeAsync();
                return await StatusAsync(args);
            case "logs":
            case "log":
                return await LogsAsync(args);
            default:
                throw new CliException($"未知命令: {command}。运行 v2rayN-cli help 查看帮助。");
        }
    }

    private async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new CliException("v2rayN-cli 当前仅支持 Linux 和 macOS。", 1);
        }
        if (!AppManager.Instance.InitApp())
        {
            throw new CliException("v2rayN 配置初始化失败。", 1);
        }

        AppManager.Instance.InitComponents();
        _config = AppManager.Instance.Config;
        await ConfigHandler.InitBuiltinDNS(_config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
        await ProfileExManager.Instance.Init();
        await CertPemManager.Instance.Init(_config);
        _initialized = true;
    }

    private async Task<int> NodesAsync(CliArguments args)
    {
        var action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        var tail = new CliArguments(args.Values.Skip(args.Count == 0 ? 0 : 1));
        switch (action)
        {
            case "list":
            case "ls":
                return await ListNodesAsync(tail);
            case "add":
            case "import":
                return await AddNodesAsync(tail);
            case "show":
                return await ShowNodeAsync(tail);
            case "select":
            case "use":
                return await SelectNodeAsync(tail);
            case "remove":
            case "rm":
            case "delete":
                return await RemoveNodeAsync(tail);
            case "export":
                return await ExportNodeAsync(tail);
            default:
                throw new CliException($"未知 nodes 子命令: {action}");
        }
    }

    private async Task<int> ListNodesAsync(CliArguments args)
    {
        var json = args.TakeFlag("--json");
        var subSelector = args.TakeOption("--subscription") ?? args.TakeOption("--sub");
        args.EnsureEmpty();

        var subId = string.Empty;
        if (!string.IsNullOrWhiteSpace(subSelector))
        {
            subId = (await ResolveSubscriptionAsync(subSelector)).Id;
        }
        var nodes = await GetNodesAsync(subId);
        var subscriptions = (await AppManager.Instance.SubItems() ?? []).ToDictionary(x => x.Id, x => x.Remarks);
        if (json)
        {
            WriteJson(nodes.Select(x => new
            {
                id = x.IndexId,
                active = x.IndexId == _config.IndexId,
                type = x.ConfigType.ToString(),
                core = x.CoreType?.ToString(),
                name = x.Remarks,
                address = x.Address,
                port = x.Port,
                subscriptionId = x.Subid,
                subscription = subscriptions.GetValueOrDefault(x.Subid),
            }));
            return 0;
        }

        if (nodes.Count == 0)
        {
            Console.WriteLine("没有节点。可用 nodes add <分享链接> 导入。");
            return 0;
        }
        Console.WriteLine("ACT  ID         TYPE          ADDRESS                         SUBSCRIPTION          NAME");
        foreach (var node in nodes)
        {
            var address = node.IsComplex() ? "-" : $"{node.Address}:{node.Port}";
            Console.WriteLine($"{(node.IndexId == _config.IndexId ? " * " : "   ")}  {ShortId(node.IndexId),-10} {node.ConfigType,-13} {Crop(address, 31),-31} {Crop(subscriptions.GetValueOrDefault(node.Subid) ?? "-", 21),-21} {node.Remarks}");
        }
        return 0;
    }

    private async Task<int> AddNodesAsync(CliArguments args)
    {
        var file = args.TakeOption("--file");
        var values = args.Values.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                throw new CliException($"文件不存在: {file}");
            }
            values.Add(await File.ReadAllTextAsync(file));
        }
        if (values.Count == 0 && Console.IsInputRedirected)
        {
            values.Add(await Console.In.ReadToEndAsync());
        }
        if (values.Count == 0)
        {
            throw new CliException("请提供节点分享链接、--file 文件，或通过标准输入传入内容。");
        }

        var imported = await ConfigHandler.AddBatchServers(_config, string.Join(Environment.NewLine, values), _config.SubIndexId, false);
        if (imported <= 0)
        {
            throw new CliException("没有识别到有效节点。", 1);
        }
        await ProfileExManager.Instance.SaveTo();
        Console.WriteLine($"成功导入 {imported} 个节点。");
        return 0;
    }

    private async Task<int> ShowNodeAsync(CliArguments args)
    {
        var selector = args.Require(0, "节点 ID 或名称");
        var uri = args.TakeFlag("--uri");
        var json = args.TakeFlag("--json");
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        var node = await ResolveNodeAsync(selector);
        if (uri)
        {
            Console.WriteLine(FmtHandler.GetShareUri(node) ?? "该节点类型不能导出为分享链接。");
            return 0;
        }
        if (json)
        {
            WriteJson(node);
            return 0;
        }
        Console.WriteLine($"ID:            {node.IndexId}");
        Console.WriteLine($"当前节点:      {(node.IndexId == _config.IndexId ? "是" : "否")}");
        Console.WriteLine($"名称:          {node.Remarks}");
        Console.WriteLine($"协议:          {node.ConfigType}");
        Console.WriteLine($"核心:          {node.CoreType?.ToString() ?? "自动"}");
        Console.WriteLine($"地址:          {node.Address}");
        Console.WriteLine($"端口:          {node.Port}");
        Console.WriteLine($"传输:          {node.GetNetwork()}");
        Console.WriteLine($"传输安全:      {node.StreamSecurity}");
        Console.WriteLine($"订阅 ID:       {node.Subid}");
        return 0;
    }

    private async Task<int> SelectNodeAsync(CliArguments args)
    {
        var selector = args.Require(0, "节点 ID 或名称");
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        var node = await ResolveNodeAsync(selector);
        if (await ConfigHandler.SetDefaultServerIndex(_config, node.IndexId) != 0)
        {
            throw new CliException("节点选择失败。", 1);
        }
        Console.WriteLine($"当前节点已切换为: {node.Remarks} ({ShortId(node.IndexId)})");
        PrintRestartHint();
        return 0;
    }

    private async Task<int> RemoveNodeAsync(CliArguments args)
    {
        var selector = args.Require(0, "节点 ID 或名称");
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        var node = await ResolveNodeAsync(selector);
        await ConfigHandler.RemoveServers(_config, [node]);
        if (_config.IndexId == node.IndexId)
        {
            _config.IndexId = string.Empty;
            await ConfigHandler.GetDefaultServer(_config);
        }
        Console.WriteLine($"已删除节点: {node.Remarks}");
        PrintRestartHint();
        return 0;
    }

    private async Task<int> ExportNodeAsync(CliArguments args)
    {
        var node = await ResolveNodeAsync(args.Require(0, "节点 ID 或名称"));
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        var uri = FmtHandler.GetShareUri(node);
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new CliException("该节点类型不能导出为分享链接。", 1);
        }
        Console.WriteLine(uri);
        return 0;
    }

    private async Task<int> SubscriptionsAsync(CliArguments args)
    {
        var action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        var tail = new CliArguments(args.Values.Skip(args.Count == 0 ? 0 : 1));
        switch (action)
        {
            case "list":
            case "ls":
                return await ListSubscriptionsAsync(tail);
            case "add":
                return await AddSubscriptionAsync(tail);
            case "update":
            case "refresh":
                return await UpdateSubscriptionsAsync(tail);
            case "remove":
            case "rm":
            case "delete":
                return await RemoveSubscriptionAsync(tail);
            case "enable":
                return await SetSubscriptionEnabledAsync(tail, true);
            case "disable":
                return await SetSubscriptionEnabledAsync(tail, false);
            default:
                throw new CliException($"未知 subscriptions 子命令: {action}");
        }
    }

    private async Task<int> ListSubscriptionsAsync(CliArguments args)
    {
        var json = args.TakeFlag("--json");
        args.EnsureEmpty();
        var items = await AppManager.Instance.SubItems() ?? [];
        if (json)
        {
            WriteJson(items.Select(x => new
            {
                id = x.Id,
                name = x.Remarks,
                url = x.Url,
                enabled = x.Enabled,
                autoUpdateMinutes = x.AutoUpdateInterval,
                updatedAt = x.UpdateTime > 0 ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(x.UpdateTime) : null,
            }));
            return 0;
        }
        if (items.Count == 0)
        {
            Console.WriteLine("没有订阅。可用 subscriptions add <URL> 添加。");
            return 0;
        }
        Console.WriteLine("ENABLED  ID         INTERVAL  NAME                     URL");
        foreach (var item in items)
        {
            Console.WriteLine($"{(item.Enabled ? "yes" : "no"),-8} {ShortId(item.Id),-10} {item.AutoUpdateInterval,8}  {Crop(item.Remarks, 24),-24} {item.Url}");
        }
        return 0;
    }

    private async Task<int> AddSubscriptionAsync(CliArguments args)
    {
        var name = args.TakeOption("--name");
        var userAgent = args.TakeOption("--user-agent") ?? string.Empty;
        var intervalText = args.TakeOption("--interval");
        var update = args.TakeFlag("--update");
        var url = args.Require(0, "订阅 URL");
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new CliException("订阅地址必须是有效的 HTTP/HTTPS URL。");
        }
        var interval = 0;
        if (intervalText is not null && (!int.TryParse(intervalText, out interval) || interval < 0))
        {
            throw new CliException("--interval 必须是大于等于 0 的分钟数。");
        }
        var item = new SubItem
        {
            Id = string.Empty,
            Remarks = name ?? GetDefaultSubscriptionName(uri),
            Url = url,
            UserAgent = userAgent,
            Enabled = true,
            AutoUpdateInterval = interval,
        };
        if (await ConfigHandler.AddSubItem(_config, item) != 0)
        {
            throw new CliException("订阅添加失败。", 1);
        }
        Console.WriteLine($"已添加订阅: {item.Remarks} ({ShortId(item.Id)})");
        if (update)
        {
            return await UpdateSubscriptionByIdAsync(item.Id, useProxy: false);
        }
        return 0;
    }

    private async Task<int> UpdateSubscriptionsAsync(CliArguments args)
    {
        var useProxy = args.TakeFlag("--proxy");
        var all = args.TakeFlag("--all");
        string subId;
        if (all || args.Count == 0)
        {
            subId = string.Empty;
        }
        else
        {
            subId = (await ResolveSubscriptionAsync(args[0])).Id;
            args = new CliArguments(args.Values.Skip(1));
        }
        args.EnsureEmpty();
        return await UpdateSubscriptionByIdAsync(subId, useProxy);
    }

    private async Task<int> UpdateSubscriptionByIdAsync(string subId, bool useProxy)
    {
        var success = false;
        await SubscriptionHandler.UpdateProcess(_config, subId, useProxy, (notify, message) =>
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
    }

    private async Task<int> RemoveSubscriptionAsync(CliArguments args)
    {
        var item = await ResolveSubscriptionAsync(args.Require(0, "订阅 ID 或名称"));
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        await ConfigHandler.DeleteSubItem(_config, item.Id);
        Console.WriteLine($"已删除订阅及其节点: {item.Remarks}");
        PrintRestartHint();
        return 0;
    }

    private async Task<int> SetSubscriptionEnabledAsync(CliArguments args, bool enabled)
    {
        var item = await ResolveSubscriptionAsync(args.Require(0, "订阅 ID 或名称"));
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        item.Enabled = enabled;
        if (await ConfigHandler.AddSubItem(_config, item) != 0)
        {
            throw new CliException("订阅状态保存失败。", 1);
        }
        Console.WriteLine($"订阅 {item.Remarks} 已{(enabled ? "启用" : "禁用")}。");
        return 0;
    }

    private async Task<int> ConfigAsync(CliArguments args)
    {
        var action = args.Count == 0 ? "show" : args[0].ToLowerInvariant();
        var tail = new CliArguments(args.Values.Skip(args.Count == 0 ? 0 : 1));
        switch (action)
        {
            case "show":
            case "list":
                return ShowConfig(tail);
            case "get":
                return GetConfig(tail);
            case "set":
                return await SetConfigAsync(tail);
            case "export":
                tail.EnsureEmpty();
                Console.WriteLine(JsonUtils.Serialize(_config, true, true));
                return 0;
            default:
                throw new CliException($"未知 config 子命令: {action}");
        }
    }

    private int ShowConfig(CliArguments args)
    {
        var json = args.TakeFlag("--json");
        args.EnsureEmpty();
        var values = CliSettings.All.ToDictionary(x => x.Key, x => x.Get(_config));
        if (json)
        {
            WriteJson(values);
        }
        else
        {
            foreach (var setting in CliSettings.All)
            {
                Console.WriteLine($"{setting.Key,-28} {setting.Get(_config)}");
            }
        }
        return 0;
    }

    private int GetConfig(CliArguments args)
    {
        var key = args.Require(0, "配置键");
        var remaining = new CliArguments(args.Values.Skip(1));
        remaining.EnsureEmpty();
        var setting = CliSettings.Find(key);
        Console.WriteLine(setting.Get(_config));
        return 0;
    }

    private async Task<int> SetConfigAsync(CliArguments args)
    {
        var key = args.Require(0, "配置键");
        var value = args.Require(1, "配置值");
        var remaining = new CliArguments(args.Values.Skip(2));
        remaining.EnsureEmpty();
        CliSettings.Find(key).Set(_config, value);
        if (await ConfigHandler.SaveConfig(_config) != 0)
        {
            throw new CliException("配置保存失败。", 1);
        }
        AppManager.Instance.Reset();
        Console.WriteLine($"已设置 {key} = {CliSettings.Find(key).Get(_config)}");
        PrintRestartHint();
        return 0;
    }

    private int Cores(CliArguments args)
    {
        var action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        if (action != "list" && action != "ls")
        {
            throw new CliException($"未知 cores 子命令: {action}");
        }
        var tail = new CliArguments(args.Values.Skip(args.Count == 0 ? 0 : 1));
        var json = tail.TakeFlag("--json");
        tail.EnsureEmpty();
        var cores = CoreInfoManager.Instance.GetCoreInfo()
            .Where(x => x.CoreExes is { Count: > 0 })
            .Select(x =>
            {
                var executable = CoreInfoManager.Instance.GetCoreExecFile(x, out _);
                return new
                {
                    type = x.CoreType.ToString(),
                    installed = !string.IsNullOrWhiteSpace(executable),
                    executable,
                    directory = Utils.GetBinPath("", x.CoreType.ToString()),
                    candidates = x.CoreExes,
                };
            }).ToList();
        if (json)
        {
            WriteJson(cores);
        }
        else
        {
            Console.WriteLine("INSTALLED  CORE           EXECUTABLE");
            foreach (var core in cores)
            {
                Console.WriteLine($"{(core.installed ? "yes" : "no"),-10} {core.type,-14} {(core.installed ? core.executable : core.directory)}");
            }
        }
        return 0;
    }

    private async Task<int> RunCoreAsync(string? selector, bool daemonMode)
    {
        var node = string.IsNullOrWhiteSpace(selector)
            ? await ConfigHandler.GetDefaultServer(_config)
            : await ResolveNodeAsync(selector);
        if (node is null)
        {
            throw new CliException("没有可运行的节点。请先导入订阅或单节点。", 1);
        }
        if (_config.IndexId != node.IndexId)
        {
            await ConfigHandler.SetDefaultServerIndex(_config, node.IndexId);
        }
        return await new CliRuntime().RunForegroundAsync(_config, node, daemonMode);
    }

    private async Task<int> StartAsync(CliArguments args)
    {
        var selector = args.Count > 0 ? args[0] : null;
        var remaining = new CliArguments(args.Values.Skip(selector is null ? 0 : 1));
        remaining.EnsureEmpty();
        var node = selector is null ? await ConfigHandler.GetDefaultServer(_config) : await ResolveNodeAsync(selector);
        if (node is null)
        {
            throw new CliException("没有可运行的节点。请先导入订阅或单节点。", 1);
        }
        await ConfigHandler.SetDefaultServerIndex(_config, node.IndexId);
        return await CliRuntime.StartDetachedAsync(node.IndexId);
    }

    private async Task<int> RestartAsync(CliArguments args)
    {
        var selector = args.Count > 0 ? args[0] : null;
        var remaining = new CliArguments(args.Values.Skip(selector is null ? 0 : 1));
        remaining.EnsureEmpty();
        await CliRuntime.StopAsync();
        var node = selector is null ? await ConfigHandler.GetDefaultServer(_config) : await ResolveNodeAsync(selector);
        if (node is null)
        {
            throw new CliException("没有可运行的节点。", 1);
        }
        return await CliRuntime.StartDetachedAsync(node.IndexId);
    }

    private async Task<int> StatusAsync(CliArguments args)
    {
        var json = args.TakeFlag("--json");
        args.EnsureEmpty();
        var state = CliRuntime.ReadState();
        var alive = CliRuntime.IsStateProcessAlive(state);
        var activeNode = await ConfigHandler.GetDefaultServer(_config);
        var result = new
        {
            status = alive ? state.Status : "stopped",
            alive,
            pid = alive ? state.Pid : null,
            corePid = alive ? state.CorePid : null,
            startedAtUtc = alive ? state.StartedAtUtc : null,
            runningNodeId = alive ? state.NodeId : null,
            runningNodeName = alive ? state.NodeName : null,
            selectedNodeId = activeNode?.IndexId,
            selectedNodeName = activeNode?.Remarks,
            mixed = $"127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}",
            secondMixed = Inbound(_config).SecondLocalPortEnabled
                ? $"127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks2)}"
                : null,
            message = state.Message,
        };
        if (json)
        {
            WriteJson(result);
        }
        else
        {
            Console.WriteLine($"状态:          {result.status}");
            Console.WriteLine($"PID:           {result.pid?.ToString() ?? "-"}");
            Console.WriteLine($"核心 PID:      {result.corePid?.ToString() ?? "-"}");
            Console.WriteLine($"运行节点:      {result.runningNodeName ?? "-"}");
            Console.WriteLine($"选中节点:      {result.selectedNodeName ?? "-"}");
            Console.WriteLine($"Mixed 代理:    {result.mixed} (SOCKS5/HTTP)");
            if (result.secondMixed is not null)
            {
                Console.WriteLine($"第二 Mixed:    {result.secondMixed}");
            }
            Console.WriteLine($"数据目录:      {Utils.StartupPath()}");
        }
        return 0;
    }

    private static async Task<int> LogsAsync(CliArguments args)
    {
        var follow = args.TakeFlag("--follow") || args.TakeFlag("-f");
        var linesText = args.TakeOption("--lines") ?? "100";
        args.EnsureEmpty();
        if (!int.TryParse(linesText, out var lineCount) || lineCount < 0)
        {
            throw new CliException("--lines 必须是大于等于 0 的整数。");
        }
        if (!File.Exists(CliRuntime.LogPath))
        {
            Console.WriteLine($"日志文件尚不存在: {CliRuntime.LogPath}");
            return 0;
        }
        foreach (var line in File.ReadLines(CliRuntime.LogPath).TakeLast(lineCount))
        {
            Console.WriteLine(line);
        }
        if (!follow)
        {
            return 0;
        }

        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.TrySetResult();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using var stream = new FileStream(CliRuntime.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(0, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            while (!cancellation.Task.IsCompleted)
            {
                var line = await reader.ReadLineAsync();
                if (line is not null)
                {
                    Console.WriteLine(line);
                }
                else
                {
                    await Task.WhenAny(cancellation.Task, Task.Delay(250));
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
        return 0;
    }

    private async Task<List<ProfileItem>> GetNodesAsync(string subId = "")
    {
        var nodes = await AppManager.Instance.ProfileItems(subId) ?? [];
        return nodes.OrderBy(x => ProfileExManager.Instance.GetSort(x.IndexId))
            .ThenBy(x => x.Remarks, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ProfileItem> ResolveNodeAsync(string selector)
    {
        var nodes = await GetNodesAsync();
        return Resolve(selector, nodes, x => x.IndexId, x => x.Remarks, "节点");
    }

    private async Task<SubItem> ResolveSubscriptionAsync(string selector)
    {
        var items = await AppManager.Instance.SubItems() ?? [];
        return Resolve(selector, items, x => x.Id, x => x.Remarks, "订阅");
    }

    private static T Resolve<T>(string selector, IReadOnlyCollection<T> items, Func<T, string> id, Func<T, string> name, string kind)
    {
        var exactId = items.FirstOrDefault(x => string.Equals(id(x), selector, StringComparison.OrdinalIgnoreCase));
        if (exactId is not null)
        {
            return exactId;
        }
        var matches = items.Where(x => id(x).StartsWith(selector, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(name(x), selector, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"找不到{kind}: {selector}"),
            _ => throw new CliException($"{kind}选择器不唯一: {selector}，请使用更长的 ID 前缀。"),
        };
    }

    private static void PrintPaths()
    {
        Console.WriteLine($"数据目录: {Utils.StartupPath()}");
        Console.WriteLine($"配置目录: {Utils.GetConfigPath()}");
        Console.WriteLine($"核心目录: {Utils.GetBinPath("")}");
        Console.WriteLine($"日志目录: {Utils.GetLogPath()}");
        Console.WriteLine($"CLI 状态: {CliRuntime.StatePath}");
    }

    private static void PrintRestartHint()
    {
        if (CliRuntime.IsStateProcessAlive(CliRuntime.ReadState()))
        {
            Console.WriteLine("提示: v2rayN-cli 正在运行，请执行 restart 应用变更。");
        }
    }

    private static string GetDefaultSubscriptionName(Uri uri)
    {
        return string.IsNullOrWhiteSpace(uri.Host) ? "subscription" : uri.Host;
    }

    private static string ShortId(string? value) => string.IsNullOrEmpty(value) ? "-" : value[..Math.Min(8, value.Length)];
    private static string Crop(string? value, int length) => string.IsNullOrEmpty(value) || value.Length <= length ? value ?? string.Empty : value[..(length - 1)] + "…";
    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static InItem Inbound(Config config) => config.Inbound.First();

    private static void PrintHelp()
    {
        Console.WriteLine("v2rayN-cli - v2rayN 的 Linux/macOS 无图形界面客户端");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  v2rayN-cli [--data-dir DIR] <命令> [参数]");
        Console.WriteLine("  v2rayN-cli ui                         打开全屏终端界面");
        Console.WriteLine();
        Console.WriteLine("节点:");
        Console.WriteLine("  nodes list [--sub ID] [--json]        列出节点");
        Console.WriteLine("  nodes add <分享链接...> [--file FILE] 导入单节点或批量链接");
        Console.WriteLine("  nodes show <ID|名称> [--uri|--json]   查看节点");
        Console.WriteLine("  nodes select <ID|名称>                选择默认节点");
        Console.WriteLine("  nodes remove <ID|名称>                删除节点");
        Console.WriteLine("  nodes export <ID|名称>                导出分享链接");
        Console.WriteLine();
        Console.WriteLine("订阅:");
        Console.WriteLine("  subs list [--json]                    列出订阅");
        Console.WriteLine("  subs add <URL> [--name NAME] [--update]");
        Console.WriteLine("  subs update [ID|名称|--all] [--proxy]");
        Console.WriteLine("  subs enable|disable <ID|名称>");
        Console.WriteLine("  subs remove <ID|名称>");
        Console.WriteLine();
        Console.WriteLine("运行:");
        Console.WriteLine("  run [ID|名称]                          前台运行，Ctrl+C 停止");
        Console.WriteLine("  start [ID|名称]                        后台运行");
        Console.WriteLine("  stop | restart [ID|名称] | status      管理后台实例");
        Console.WriteLine("  logs [-f] [--lines N]                 查看运行日志");
        Console.WriteLine();
        Console.WriteLine("配置与诊断:");
        Console.WriteLine("  config show [--json]                  显示可配置项");
        Console.WriteLine("  config get <KEY>");
        Console.WriteLine("  config set <KEY> <VALUE>");
        Console.WriteLine("  config export                         导出完整 v2rayN 配置 JSON");
        Console.WriteLine("  cores list                            检查核心安装情况");
        Console.WriteLine("  paths | version | help");
        Console.WriteLine();
        Console.WriteLine("全局选项:");
        Console.WriteLine("  --data-dir DIR   使用指定数据目录（适合多实例/服务器部署）");
        Console.WriteLine("  --portable       将数据保存在可执行文件目录");
    }
}
