using Darp.CodeEditor.CSharp.Server;

// stdout carries the protocol. Anything else that writes to the console goes to stderr.
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

using var server = new ScriptLanguageServer(input, output, ServerOptions.Parse(args));
await server.Run().ConfigureAwait(false);
return server.ExitCode;
