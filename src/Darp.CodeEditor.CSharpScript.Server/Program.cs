using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharpScript.Server;

// A development build names the folder of .NET's XML docs, the SDK's reference pack. A published app has them next to
// .NET's assemblies.
string frameworkDocs = args is [string folder] ? folder : RuntimeEnvironment.GetRuntimeDirectory();

// Standard input closing ends the server, which is how it ends with an app that was killed.
return await new ScriptServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), frameworkDocs).RunAsync();
