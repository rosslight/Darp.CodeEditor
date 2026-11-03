using Darp.CodeEditor.CSharp.Server;
using Darp.CodeEditor.Lsp;

// stdout carries the protocol. Anything else that writes to the console goes to stderr.
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

var session = new ScriptSession(
    ServerOptions.Parse(args),
    // The client's process ended, but another process, such as a child of the client, still holds the input open.
    clientExited: processId =>
    {
        Console.Error.WriteLine($"The client process {processId} ended; exiting.");
        Environment.Exit(1);
    }
);
var server = new LanguageServer(input, output, session.InitializeAsync, session.ShutdownAsync, Console.Error.WriteLine);
session.Connect(server.Connection);

// Not disposed: disposing waits for the read loop, which never ends while the client keeps its end of the pipe open.
return await server.RunAsync().ConfigureAwait(false);
