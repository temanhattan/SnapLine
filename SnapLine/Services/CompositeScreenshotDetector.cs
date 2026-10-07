namespace SnapLine.Services;

/// <summary>Combines independent capture providers behind one clothesline-facing detector.</summary>
public sealed class CompositeScreenshotDetector(IEnumerable<IScreenshotDetector> providers) : IScreenshotDetector
{
    private readonly IScreenshotDetector[] _providers = providers.ToArray();

    public event ScreenshotDetectedHandler? ScreenshotDetected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            provider.ScreenshotDetected += ForwardScreenshotAsync;
            await provider.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            provider.ScreenshotDetected -= ForwardScreenshotAsync;
            await provider.StopAsync(cancellationToken);
        }
    }

    private async Task ForwardScreenshotAsync(object sender, ScreenshotDetectedEventArgs args)
    {
        var handler = ScreenshotDetected;
        if (handler is null) return;
        foreach (ScreenshotDetectedHandler subscriber in handler.GetInvocationList())
            await subscriber(this, args);
    }
}
