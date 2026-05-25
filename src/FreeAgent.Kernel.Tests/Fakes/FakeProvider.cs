using System.Runtime.CompilerServices;
using FreeAgent.Kernel;

namespace FreeAgent.Kernel.Tests;

public sealed class FakeProvider : IProvider
{
    private readonly Queue<IReadOnlyList<StreamChunk>> _scripts;
    public List<ProviderRequest> Requests { get; } = [];

    public FakeProvider(IEnumerable<IReadOnlyList<StreamChunk>> scripts) => _scripts = new Queue<IReadOnlyList<StreamChunk>>(scripts);

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_scripts.Count == 0)
        {
            yield break;
        }

        foreach (var chunk in _scripts.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }
}
