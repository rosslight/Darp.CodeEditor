using Darp.CodeEditor.CSharpScript.Server;

// Standard input closing ends the server, which is how it ends with an app that was killed.
return await new ScriptServer(Console.OpenStandardInput(), Console.OpenStandardOutput()).RunAsync();
