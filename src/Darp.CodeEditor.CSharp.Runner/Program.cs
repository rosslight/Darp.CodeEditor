using Darp.CodeEditor.CSharp.Execution.Protocol;
using Darp.CodeEditor.CSharp.Runner;
using StreamJsonRpc;

// Bind to the host before anything else: no script may run in a runner whose host is gone.
if (!ParentProcess.Bind(args))
    return ExitCodes.ParentGone;

// stdout carries the protocol. Whatever scripts write to the console goes to stderr, which the host reads as output.
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
Console.SetIn(TextReader.Null);

var shapes = new ShapeProviders();
var rpc = new RunnerJsonRpc(RunnerRpc.CreateMessageHandler(output, input, shapes));
JsonRpc client = rpc;
var service = new ScriptRunnerService(rpc, shapes, client.Attach<IRunnerHost>());
rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IRunnerService>(), service, null);
rpc.StartListening();
try
{
    await rpc.Completion.ConfigureAwait(false);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"The connection to the host failed: {exception}");
}

// The host closed the connection. Scripts may still run on other threads; they end with the process.
Environment.Exit(ExitCodes.Success);
return ExitCodes.Success;
