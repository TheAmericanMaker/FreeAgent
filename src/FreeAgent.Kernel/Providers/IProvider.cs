namespace FreeAgent.Kernel;

public interface IProvider
{
    IAsyncEnumerable<StreamChunk> StreamChatAsync(ProviderRequest request, CancellationToken cancellationToken);
}
