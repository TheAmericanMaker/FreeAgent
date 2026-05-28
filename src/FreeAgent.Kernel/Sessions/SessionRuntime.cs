using System.Diagnostics;
using System.Text;

namespace FreeAgent.Kernel;

public sealed class SessionRuntime
{
    /// <summary>OpenTelemetry/diagnostics source — wrap turn + tool activity in spans without forcing a tracing library.</summary>
    public static readonly ActivitySource ActivitySource = new("FreeAgent.Kernel.Session", "0.1.0");

    // Hard ceiling on agentic iterations within a single turn. Matches the Hermes Agent default of
    // 90; a genuinely stuck turn that escapes the doom-loop guard still terminates here.
    private const int MaxIterations = 90;

    // After the doom-loop guard first trips, the model is re-prompted (with the repeat suppressed)
    // up to this many times to give it a chance to recover. Once the budget is spent and it is still
    // looping, the turn halts rather than re-prompting indefinitely.
    private const int DoomRecoveryBudget = 3;

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
        using var turnActivity = ActivitySource.StartActivity("Session.RunTurn");
        turnActivity?.SetTag("session.id", _state.SessionId);
        turnActivity?.SetTag("user.text.length", userText.Length);

        _doomLoop.Reset();

        // If the previous turn pushed us past the compaction threshold, drop older turns before
        // appending this turn's user message and contacting the provider. We ask the provider for
        // a short summary of the dropped portion; on any error the runner falls back to a non-LLM
        // notice (see Compactor.CompactWithSummaryAsync).
        if (Compactor.ShouldCompact(_state.LastInputTokens, _state.ContextWindow))
        {
            var compacted = await Compactor.CompactWithSummaryAsync(_state.Messages, _provider, cancellationToken: cancellationToken);
            _state.Messages.Clear();
            _state.Messages.AddRange(compacted);
        }

        _state.Messages.Add(new Message(MessageRole.User, userText));
        var finalText = new StringBuilder();
        var doomDetected = false;
        var doomReprompts = 0;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Whole-session iteration cap (in addition to the per-turn MaxIterations). Off by default
            // (null = no whole-session limit); a host can set state.SessionIterationLimit.
            _state.TotalIterations++;
            if (_state.SessionIterationLimit is { } sessionCap && _state.TotalIterations > sessionCap)
            {
                _state.Messages.Add(new Message(MessageRole.Assistant,
                    $"Session iteration limit ({sessionCap}) reached. Halting the turn."));
                break;
            }

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
                    if (chunk.Usage.InputTokens > 0)
                        _state.LastInputTokens = chunk.Usage.InputTokens;
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
                doomReprompts++;

                // Budget spent and still looping: stop running the repeat and end the turn so the
                // user can intervene, rather than re-prompting up to the iteration ceiling.
                if (doomReprompts > DoomRecoveryBudget)
                {
                    _state.Messages.Add(new Message(MessageRole.Assistant, $"Doom loop detected: the identical tool-call batch persisted through {DoomRecoveryBudget} recovery attempts. Halting the turn."));
                    break;
                }

                _state.Messages.Add(new Message(MessageRole.Assistant, $"Doom loop detected: this identical tool-call batch has repeated and will not be run again (recovery attempt {doomReprompts} of {DoomRecoveryBudget}). Change your approach or stop."));
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
