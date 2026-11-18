namespace CxPrint.Service;

// SCM must see a non-zero exit after a hosted worker fails, so recovery actually runs.
public abstract class ServiceWorker : BackgroundService
{
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Environment.ExitCode=1; throw; }
    }
    protected abstract Task RunAsync(CancellationToken stoppingToken);
}
