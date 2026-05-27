using System.Text;

namespace FreeAgent.Kernel;

public sealed class SessionRuntime
{
    private const int MaxIterations = 1000;
    private readonly IProvider _provider;
    private readonly IToolRegistry _tools;
    private readonly TurnExecutor _turnExecutor;
    private readonly IPersistenceStore _store;
    private readonly IEventSink _events;
    private readonly SessionState _state;
    private readonly DoomLoopDetector _doomLoop = new(3);

    public SessionRuntime(
        IProvider provider,
        IToolRegistry tools,
        ToolPipeline pipeline,
        IPersistenceStore store,
        IEventSink events,
        SessionState state)
    {
        _provider = provider;
        _tools = tools;
        _turnExecutor = new TurnExecutor(tools, pipeline);
        _store = store;
        _events = events;
        _state = state;
    }

    public async ValueTask<TurnResult> RunTurnAsync(string userText, CancellationToken cancellationToken)
    {
        _doomLoop.Reset();
        _state.Messages.Add(new Message(MessageRole.User, userText));
        var finalText = new StringBuilder();
        var doomDetected = false;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var text = new StringBuilder();
            var partialCalls = new Dictionary<string, PartialToolCall>();
            var request = new ProviderRequest(_state.Messages.ToArray(), _tools.Definitions);

            await foreach (var chunk in _provider.StreamChatAsync(request, cancellationToken).WithCancellation(cancellationToken))
            {
                if (chunk.ThinkingDelta is not null)
                {
                    _events.OnThinking(chunk.ThinkingDelta);
                }

                if (chunk.TextDelta is not null)
                {
                    text.Append(chunk.TextDelta);
                    _events.OnText(chunk.TextDelta);
                }

                if (chunk.ToolCallDelta is not null)
                {
                    var delta = chunk.ToolCallDelta;
                    if (partialCalls.TryGetValue(delta.Id, out var existing))
                    {
                        existing.ArgumentsJson += delta.ArgumentsJson;
                    }
                    else
                    {
                        partialCalls[delta.Id] = new PartialToolCall(delta.Id, delta.Name, delta.ArgumentsJson);
                    }
                }

                if (chunk.Usage is not null)
                {
                    _events.OnUsage(chunk.Usage);
                }
            }

            var calls = partialCalls.Values
                .Select(p => new ToolCall(p.Id, p.Name, p.ArgumentsJson))
                .ToArray();

            if (calls.Length == 0)
            {
                finalText.Append(text);
                _state.Messages.Add(new Message(MessageRole.Assistant, text.ToString()));
                await _store.SaveAsync(_state, cancellationToken);
                return new TurnResult(finalText.ToString(), doomDetected);
            }

            if (_doomLoop.Observe(calls))
            {
                doomDetected = true;
                _state.Messages.Add(new Message(MessageRole.Assistant, "Doom loop detected: this identical tool-call batch has repeated 3 times and will not be run again. Change your approach or stop."));
                continue;
            }

            _state.Messages.Add(new Message(MessageRole.Assistant, text.ToString(), calls.ToArray()));
            var results = await _turnExecutor.ExecuteBatchAsync(calls, _state, cancellationToken);
            for (var resultIndex = 0; resultIndex < calls.Length; resultIndex++)
            {
                var call = calls[resultIndex];
                var result = results[resultIndex];
                _state.Messages.Add(new Message(MessageRole.Tool, result.Content, ToolCallId: call.Id, ToolName: call.Name));
            }
        }

        await _store.SaveAsync(_state, cancellationToken);
        return new TurnResult(finalText.ToString(), doomDetected);
    }

    /// <summary>
    /// Mutable accumulator for a single tool call's streaming deltas. The provider may split one
    /// logical call across multiple <see cref="ToolCallDelta"/> chunks; we buffer by <see cref="Id"/>
    /// until the stream ends, then emit one <see cref="ToolCall"/> per collected id.
    /// </summary>
    private sealed class PartialToolCall(string id, string name, string argumentsJson)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string ArgumentsJson { get; set; } = argumentsJson;
    }
}
