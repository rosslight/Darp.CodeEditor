using Darp.CodeEditor.CSharp.Execution.Protocol;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Darp.CodeEditor.CSharp.Runner;

/// <summary>The runner's end of the connection.</summary>
internal sealed class RunnerJsonRpc(IJsonRpcMessageHandler messageHandler) : JsonRpc(messageHandler)
{
    /// <summary>
    /// Sends a <see cref="RunnerValue"/> as its value with the type it names, so the formatter marshals a script's
    /// handler as the handler interface although no method declares that type.
    /// </summary>
    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken
    )
    {
        JsonRpcMessage message = await base.DispatchRequestAsync(request, targetMethod, cancellationToken)
            .ConfigureAwait(false);
        if (message is JsonRpcResult { Result: RunnerValue value } result)
        {
            result.Result = value.Value;
            result.ResultDeclaredType = value.DeclaredType;
        }
        return message;
    }
}
