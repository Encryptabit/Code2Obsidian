using Code2Obsidian.Enrichment.Config;

namespace Code2Obsidian.Tests.TestSupport;

public sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Func<CommandRunnerRequest, CancellationToken, Task<CommandRunnerResult>> _handler;

    public FakeCommandRunner(Func<CommandRunnerRequest, CancellationToken, Task<CommandRunnerResult>> handler)
    {
        _handler = handler;
    }

    public List<CommandRunnerRequest> Requests { get; } = [];

    public Task<CommandRunnerResult> RunAsync(CommandRunnerRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _handler(request, cancellationToken);
    }

    public static FakeCommandRunner FromResult(CommandRunnerResult result) =>
        new((_, _) => Task.FromResult(result));

    public static FakeCommandRunner FromException(Exception exception) =>
        new((_, _) => Task.FromException<CommandRunnerResult>(exception));
}
