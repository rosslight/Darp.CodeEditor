using ConsumerDependency;
using Darp.CodeEditor.CSharpScript.Runner;

namespace ConsumerApi;

/// <summary>What the consumer's scripts see.</summary>
public sealed class ConsumerGlobals : Base
{
    private readonly IConsumerApp _app;

    internal ConsumerGlobals(IConsumerApp app, ConsumerInput input, CancellationToken stop)
    {
        _app = app;
        Input = input;
        Stop = stop;
    }

    /// <summary>What the app gave this run.</summary>
    public ConsumerInput Input { get; }

    /// <summary>Cancelled when the run is stopped.</summary>
    public CancellationToken Stop { get; }

    /// <summary>A greeting.</summary>
    public static string Hello() => "hello";

    /// <summary>Asks the app, and waits for its answer.</summary>
    public Task<string> AskAsync(string question) => _app.AskAsync(question, Stop);
}

/// <summary>The session object: it answers the app and creates each run's globals.</summary>
public sealed class ConsumerSession
    : IConsumerSession,
        IScriptSessionObject<ConsumerSession, IConsumerApp>,
        IScriptGlobalsFactory<ConsumerGlobals, ConsumerInput>
{
    private readonly IConsumerApp _app;

    private ConsumerSession(IConsumerApp app) => _app = app;

    static ConsumerSession IScriptSessionObject<ConsumerSession, IConsumerApp>.Create(IConsumerApp app) => new(app);

    ConsumerGlobals IScriptGlobalsFactory<ConsumerGlobals, ConsumerInput>.CreateGlobals(
        ConsumerInput input,
        CancellationToken stop
    ) => new(_app, input, stop);

    /// <inheritdoc />
    public Task<string> AskBackAsync(string question, CancellationToken cancellationToken) =>
        _app.AskAsync(question, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetProcessIdAsync(CancellationToken cancellationToken) => Task.FromResult(Environment.ProcessId);
}
