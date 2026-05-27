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
            var calls = new List<ToolCall>();
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
                    calls.Add(new ToolCall(chunk.ToolCallDelta.Id, chunk.ToolCallDelta.Name, chunk.ToolCallDelta.ArgumentsJson));
                }

                if (chunk.Usage is not null)
                {
                    _events.OnUsage(chunk.Usage);
                }
            }

            if (calls.Count == 0)
            {
                finalText.Append(text);
                _state.Messages.Add(new Message(MessageRole.Assistant, text.ToString()));
                await _store.SaveAsync(_state, cancellationToken);
                return new TurnResult(finalText.ToString(), doomDetected);
            }

            if (_doomLoop.Observe(calls))
            {
                doomDetected = true;
                _state.Messages.Add(new Message(MessageRole.Assistant, "Doom loop detected: identical tool-call batch repeated 3 times. Breaking the loop."));
                continue;
            }

            _state.Messages.Add(new Message(MessageRole.Assistant, text.ToString(), calls.ToArray()));
            var results = await _turnExecutor.ExecuteBatchAsync(calls, _state, cancellationToken);
            for (var resultIndex = 0; resultIndex < calls.Count; resultIndex++)
            {
                var call = calls[resultIndex];
                var result = results[resultIndex];
                _state.Messages.Add(new Message(MessageRole.Tool, result.Content, ToolCallId: call.Id, ToolName: call.Name));
            }
        }

        await _store.SaveAsync(_state, cancellationToken);
        return new TurnResult(finalText.ToString(), doomDetected);
    }
}
