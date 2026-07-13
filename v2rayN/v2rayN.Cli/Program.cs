using ServiceLib;

namespace v2rayN.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            var normalized = ConfigureDataDirectory(args);
            return await new CliApplication().RunAsync(normalized);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未处理错误: {ex.Message}");
            return 1;
        }
    }

    private static string[] ConfigureDataDirectory(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir")
            {
                if (++i >= args.Length)
                {
                    throw new CliException("--data-dir 需要一个目录参数。");
                }
                Environment.SetEnvironmentVariable(Global.CustomDataDir, args[i]);
                continue;
            }

            if (args[i] == "--portable")
            {
                Environment.SetEnvironmentVariable(Global.CustomDataDir, AppContext.BaseDirectory);
                continue;
            }

            result.Add(args[i]);
        }

        if (Environment.GetEnvironmentVariable(Global.CustomDataDir) is null
            && Environment.GetEnvironmentVariable(Global.LocalAppData) is null)
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
        }

        return [.. result];
    }
}

internal sealed class CliException(string message, int exitCode = 2) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
