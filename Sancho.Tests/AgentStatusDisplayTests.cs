using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sancho.Console;
using Sancho.Console.Agents;
using Sancho.Console.Cli;
using Sancho.Console.Orchestration;
using Xunit;

namespace Sancho.Tests;

[CollectionDefinition("Console capture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;

[Collection("Console capture")]
public class AgentStatusDisplayTests
{
    [Theory]
    [InlineData(AgentStatusKind.Stderr)]
    [InlineData(AgentStatusKind.Info)]
    public async Task DiagnosticsAreDimWhileAssistantTextKeepsItsOwnColor(AgentStatusKind kind)
    {
        const string diagnostic = "ERROR rmcp::transport::worker: worker quit with fatal: Transport channel closed";
        var original = System.Console.Out;
        using var output = new StringWriter();
        using var lifetime = new CancellationTokenSource();
        var display = new Display();
        var orchestrator = new Orchestrator(null!, null!, CliArgs.Parse([]), null, display, null!, NullLogger<Orchestrator>.Instance);
        try
        {
            System.Console.SetOut(output);
            var consume = typeof(Orchestrator).GetMethod("ConsumeAgentEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)consume.Invoke(orchestrator, [Events(lifetime, TestContext.Current.CancellationToken), lifetime])!;
        }
        finally { System.Console.SetOut(original); }
        Assert.Contains(Display.FormatLine(diagnostic, Display.HistoryColor.Dim), output.ToString());
        Assert.Contains(Display.FormatLine("Hey! I'm here and listening.", Display.HistoryColor.Claude), output.ToString());

        async IAsyncEnumerable<AgentEvent> Events(CancellationTokenSource source, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AgentEvent.Status(diagnostic, kind);
            yield return new AgentEvent.AssistantText("Hey! I'm here and listening.");
            await source.CancelAsync();
        }
    }
}
