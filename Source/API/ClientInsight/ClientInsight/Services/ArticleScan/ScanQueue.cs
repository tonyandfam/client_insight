using System.Threading.Channels;

namespace ClientInsightAPI.Services.ArticleScan;

public record ScanWorkItem(Guid JobId, Guid CompanyId);

public sealed class ScanQueue
{
    private readonly Channel<ScanWorkItem> _channel;

    public ScanQueue(Channel<ScanWorkItem> channel) => _channel = channel;

    public ValueTask EnqueueAsync(ScanWorkItem item, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(item, ct);

    public IAsyncEnumerable<ScanWorkItem> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
