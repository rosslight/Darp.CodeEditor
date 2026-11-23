using ConsumerDependency;

namespace ConsumerApi;

/// <summary>What the consumer's scripts see.</summary>
public class ConsumerGlobals : Base
{
    /// <summary>A greeting.</summary>
    public static string Hello() => "hello";
}
