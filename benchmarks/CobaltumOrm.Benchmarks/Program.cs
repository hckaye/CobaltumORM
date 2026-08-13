using BenchmarkDotNet.Running;

namespace CobaltumOrm.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var benchmarkArguments = args.Length == 0 ? new[] { "--filter", "*" } : args;
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        if (OnlyReadsBenchmarkMetadata(args))
        {
            switcher.Run(benchmarkArguments);
            return;
        }

        var previousConnectionString = Environment.GetEnvironmentVariable(
            BenchmarkDatabase.ConnectionStringEnvironmentVariable);
        var previousPreparedValue = Environment.GetEnvironmentVariable(
            BenchmarkDatabase.PreparedEnvironmentVariable);
        await using var database = new BenchmarkDatabase();
        await database.StartAsync().ConfigureAwait(false);
        try
        {
            Environment.SetEnvironmentVariable(
                BenchmarkDatabase.ConnectionStringEnvironmentVariable,
                database.ConnectionString);
            Environment.SetEnvironmentVariable(
                BenchmarkDatabase.PreparedEnvironmentVariable,
                "1");
            switcher.Run(benchmarkArguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                BenchmarkDatabase.ConnectionStringEnvironmentVariable,
                previousConnectionString);
            Environment.SetEnvironmentVariable(
                BenchmarkDatabase.PreparedEnvironmentVariable,
                previousPreparedValue);
        }
    }

    private static bool OnlyReadsBenchmarkMetadata(string[] args) =>
        args.Any(argument =>
            argument is "--list" or "--help" or "-h" or "--version");
}
