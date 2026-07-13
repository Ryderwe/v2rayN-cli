using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;

namespace v2rayN.Cli;

internal sealed class CliRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StatePath => Utils.GetConfigPath("v2rayN-cli-state.json");
    public static string LockPath => Utils.GetConfigPath("v2rayN-cli.lock");
    public static string LogPath => Utils.GetLogPath("v2rayN-cli.log");

    public static CliRuntimeState ReadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new CliRuntimeState { Status = "stopped" };
            }

            return JsonSerializer.Deserialize<CliRuntimeState>(File.ReadAllText(StatePath), JsonOptions)
                   ?? new CliRuntimeState { Status = "unknown" };
        }
        catch
        {
            return new CliRuntimeState { Status = "unknown", Message = "状态文件无法读取" };
        }
    }

    public static bool IsStateProcessAlive(CliRuntimeState state)
    {
        if (state.Pid is null or <= 0)
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById(state.Pid.Value);
            if (process.HasExited)
            {
                return false;
            }
            if (state.StartedAtUtc is not null)
            {
                var difference = (process.StartTime.ToUniversalTime() - state.StartedAtUtc.Value).Duration();
                if (difference > TimeSpan.FromSeconds(5))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<int> StartDetachedAsync(string nodeId)
    {
        var current = ReadState();
        if (IsStateProcessAlive(current))
        {
            throw new CliException($"v2rayN-cli 已在运行（PID {current.Pid}）。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new CliException("无法确定当前 v2rayN-cli 可执行文件路径。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Utils.StartupPath(),
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec </dev/null >>\"$3\" 2>&1; trap '' HUP; exec \"$1\" __daemon \"$2\"");
        startInfo.ArgumentList.Add("v2rayN-cli");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add(nodeId);
        startInfo.ArgumentList.Add(LogPath);

        var process = Process.Start(startInfo) ?? throw new CliException("后台进程启动失败。");
        var expectedPid = process.Id;
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            var state = ReadState();
            if (state.Pid == expectedPid && state.Status == "running")
            {
                Console.WriteLine($"已启动 v2rayN-cli（PID {expectedPid}，核心 PID {state.CorePid}）。");
                Console.WriteLine($"日志: {LogPath}");
                return 0;
            }
            if (state.Pid == expectedPid && state.Status == "error")
            {
                throw new CliException($"启动失败: {state.Message}。查看日志: {LogPath}", 1);
            }
            if (process.HasExited)
            {
                throw new CliException($"后台进程提前退出（代码 {process.ExitCode}）。查看日志: {LogPath}", 1);
            }
        }

        throw new CliException($"后台进程启动超时。查看日志: {LogPath}", 1);
    }

    public static async Task<int> StopAsync()
    {
        var state = ReadState();
        if (!IsStateProcessAlive(state))
        {
            await WriteStateAsync(new CliRuntimeState { Status = "stopped", Message = "未运行" });
            Console.WriteLine("v2rayN-cli 当前未运行。");
            return 0;
        }

        var process = Process.GetProcessById(state.Pid!.Value);
        using (var signal = Process.Start(new ProcessStartInfo
               {
                   FileName = "/bin/kill",
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   ArgumentList = { "-TERM", state.Pid.Value.ToString() },
               }))
        {
            if (signal is not null)
            {
                await signal.WaitForExitAsync();
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            await Task.Delay(150);
            process.Refresh();
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        await WriteStateAsync(new CliRuntimeState { Status = "stopped", Message = "已停止" });
        Console.WriteLine("v2rayN-cli 已停止。");
        return 0;
    }

    public async Task<int> RunForegroundAsync(Config config, ProfileItem node, bool daemonMode)
    {
        FileStream? instanceLock = null;
        try
        {
            instanceLock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException)
        {
            throw new CliException("另一个 v2rayN-cli 运行实例已占用锁文件。", 1);
        }

        await using (instanceLock)
        {
            var startedAt = DateTime.UtcNow;
            var pid = Environment.ProcessId;
            await WriteStateAsync(new CliRuntimeState
            {
                Status = "starting",
                Pid = pid,
                StartedAtUtc = startedAt,
                NodeId = node.IndexId,
                NodeName = node.Remarks,
            });

            var stopSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopSource.TrySetResult();
            };
            Console.CancelKeyPress += cancelHandler;

            PosixSignalRegistration? sigTerm = null;
            if (!OperatingSystem.IsWindows())
            {
                sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    stopSource.TrySetResult();
                });
            }

            try
            {
                var coreStarted = false;
                await CoreManager.Instance.Init(config, (notify, message) =>
                {
                    if (notify)
                    {
                        coreStarted = true;
                    }
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        Console.WriteLine(message.TrimEnd());
                    }
                    return Task.CompletedTask;
                });

                var build = await CoreConfigContextBuilder.BuildAll(config, node);
                foreach (var warning in build.CombinedValidatorResult.Warnings)
                {
                    Console.WriteLine($"警告: {warning}");
                }
                if (!build.Success)
                {
                    var message = string.Join("; ", build.CombinedValidatorResult.Errors);
                    await WriteErrorAsync(pid, startedAt, node, message);
                    Console.Error.WriteLine($"配置校验失败: {message}");
                    return 1;
                }

                await CoreManager.Instance.LoadCore(build.MainResult.Context, build.PreSocksResult?.Context);
                if (!coreStarted || !CoreManager.Instance.IsRunning)
                {
                    const string message = "代理核心未能启动，请确认发布目录中包含对应核心文件";
                    await WriteErrorAsync(pid, startedAt, node, message);
                    Console.Error.WriteLine(message);
                    return 1;
                }

                await WriteStateAsync(new CliRuntimeState
                {
                    Status = "running",
                    Pid = pid,
                    CorePid = CoreManager.Instance.ProcessId,
                    StartedAtUtc = startedAt,
                    NodeId = node.IndexId,
                    NodeName = node.Remarks,
                    MixedPort = AppManager.Instance.GetLocalPort(ServiceLib.Enums.EInboundProtocol.socks),
                    SecondMixedPort = config.Inbound.First().SecondLocalPortEnabled
                        ? AppManager.Instance.GetLocalPort(ServiceLib.Enums.EInboundProtocol.socks2)
                        : null,
                    Message = daemonMode ? "后台运行" : "前台运行",
                });

                Console.WriteLine($"Mixed (SOCKS5/HTTP): 127.0.0.1:{AppManager.Instance.GetLocalPort(ServiceLib.Enums.EInboundProtocol.socks)}");
                if (config.Inbound.First().SecondLocalPortEnabled)
                {
                    Console.WriteLine($"Second mixed:         127.0.0.1:{AppManager.Instance.GetLocalPort(ServiceLib.Enums.EInboundProtocol.socks2)}");
                }
                if (!daemonMode)
                {
                    Console.WriteLine("按 Ctrl+C 停止。");
                }

                while (!stopSource.Task.IsCompleted)
                {
                    var delay = Task.Delay(1000);
                    await Task.WhenAny(stopSource.Task, delay);
                    if (!CoreManager.Instance.IsRunning)
                    {
                        await WriteErrorAsync(pid, startedAt, node, "代理核心意外退出");
                        Console.Error.WriteLine("代理核心意外退出。");
                        return 1;
                    }
                }

                return 0;
            }
            finally
            {
                sigTerm?.Dispose();
                Console.CancelKeyPress -= cancelHandler;
                await CoreManager.Instance.CoreStop();
                var current = ReadState();
                if (current.Pid == Environment.ProcessId && current.Status != "error")
                {
                    await WriteStateAsync(new CliRuntimeState { Status = "stopped", Message = "已停止" });
                }
            }
        }
    }

    private static Task WriteErrorAsync(int pid, DateTime startedAt, ProfileItem node, string message)
    {
        return WriteStateAsync(new CliRuntimeState
        {
            Status = "error",
            Pid = pid,
            StartedAtUtc = startedAt,
            NodeId = node.IndexId,
            NodeName = node.Remarks,
            Message = message,
        });
    }

    public static async Task WriteStateAsync(CliRuntimeState state)
    {
        var temporary = StatePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, StatePath, true);
    }
}

internal sealed class CliRuntimeState
{
    public string Status { get; set; } = "unknown";
    public int? Pid { get; set; }
    public int? CorePid { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public string? NodeId { get; set; }
    public string? NodeName { get; set; }
    public int? MixedPort { get; set; }
    public int? SecondMixedPort { get; set; }
    public string? Message { get; set; }
}
