using System.Diagnostics;

namespace CobaltumOrm.Tool;

internal interface IProcessRunner
{
    Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

internal sealed class DotNetProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dotnet process could not be started.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return process.ExitCode;
    }
}
