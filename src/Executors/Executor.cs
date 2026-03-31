using Gauge.Dotnet.Processors;

namespace Gauge.Dotnet.Executors;

internal class Executor : IExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Executor> _logger;

    public Executor(IServiceProvider serviceProvider, ILogger<Executor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<TResult> Execute<TRequest, TResult>(int streamId, TRequest request)
    {
        try
        {
            var processor = _serviceProvider.GetRequiredService<IGaugeProcessor<TRequest, TResult>>();
            return await processor.Process(streamId, request);
        }
        catch (Exception ex)
        {
            _logger.LogError("Execute failed for {RequestType}: {ExceptionType}: {Message}",
                typeof(TRequest).Name, ex.GetType().Name, ex.Message);
            throw;
        }
    }
}
