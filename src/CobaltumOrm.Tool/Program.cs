using CobaltumOrm.Tool;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await new ToolApplication(Console.Out, Console.Error, new DotNetProcessRunner())
    .RunAsync(args, cancellation.Token);
