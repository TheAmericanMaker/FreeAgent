namespace FreeAgent.Kernel;

/// <summary>
/// Off-conversation storage for tool outputs that would otherwise bloat the transcript and the
/// model's context. When the pipeline finishes a tool call whose Success result is larger than the
/// configured threshold, it stores the full content here and replaces the result with a short
/// preview plus an opaque reference; the model can pull the full text back via a retrieval tool.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Stores <paramref name="content"/> and returns an opaque reference.</summary>
    string Store(string content);

    /// <summary>Tries to fetch the content stored under <paramref name="reference"/>.</summary>
    bool TryGet(string reference, out string content);
}
