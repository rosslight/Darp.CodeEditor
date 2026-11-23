using System.Reflection;
using System.Runtime.Loader;

// Takes the published folder of an app, and loads its helpers/script-api through one AssemblyDependencyResolver.
string payload = Path.Combine(Path.GetFullPath(args[0]), "helpers", "script-api");
var resolver = new AssemblyDependencyResolver(Path.Combine(payload, "CSharpScriptApiRoot.dll"));
var context = new PayloadContext(resolver);

object? Call(string assembly, string type, string method) =>
    context
        .LoadFromAssemblyName(new AssemblyName(assembly))
        .GetType(type, throwOnError: true)!
        .GetMethod(method)!
        .Invoke(null, null);

Expect(Call("ScriptApi", "ConsumerApi.ConsumerGlobals", "Hello"), "hello");
Expect(Call("CompatibleA", "ConsumerCompatibleA.Json", "Write"), """{"from":"A"}""");
Expect(Call("CompatibleB", "ConsumerCompatibleB.Json", "Write"), """{"from":"B"}""");
Expect(
    context
        .LoadFromAssemblyName(new AssemblyName("MultiTargeted"))
        .GetType("ConsumerMultiTargeted.Target", throwOnError: true)!
        .GetField("Name")!
        .GetRawConstantValue(),
    "net10.0"
);

// A marked package's native library, really called.
Type connectionType = context
    .LoadFromAssemblyName(new AssemblyName("Microsoft.Data.Sqlite"))
    .GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)!;
using (var connection = (IDisposable)Activator.CreateInstance(connectionType, "Data Source=:memory:")!)
{
    connectionType.GetMethod("Open", Type.EmptyTypes)!.Invoke(connection, null);
    object command = connectionType.GetMethod("CreateCommand", Type.EmptyTypes)!.Invoke(connection, null)!;
    command.GetType().GetProperty("CommandText")!.SetValue(command, "select 40 + 2");
    Expect(command.GetType().GetMethod("ExecuteScalar", Type.EmptyTypes)!.Invoke(command, null), 42L);
}

// Every assembly came from the payload, and one version of the shared dependency loaded.
foreach (Assembly assembly in context.Assemblies)
{
    if (Path.GetDirectoryName(assembly.Location) != payload)
        throw new InvalidOperationException($"{assembly.GetName().Name} loaded from {assembly.Location}.");
}
if (context.Assemblies.Count(a => a.GetName().Name == "Newtonsoft.Json") != 1)
    throw new InvalidOperationException("The shared dependency did not load once.");
Console.WriteLine("Passed.");

static void Expect(object? actual, object expected)
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

sealed class PayloadContext(AssemblyDependencyResolver resolver) : AssemblyLoadContext("payload")
{
    private readonly AssemblyDependencyResolver _resolver = resolver;

    protected override Assembly? Load(AssemblyName name) =>
        _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;

    protected override nint LoadUnmanagedDll(string name) =>
        _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}
