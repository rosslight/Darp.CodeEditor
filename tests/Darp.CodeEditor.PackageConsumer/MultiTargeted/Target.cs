namespace ConsumerMultiTargeted;

/// <summary>Says which target of this library was deployed.</summary>
public static class Target
{
#if NET10_0
    /// <summary>The target framework.</summary>
    public const string Name = "net10.0";
#else
    /// <summary>The target framework.</summary>
    public const string Name = "net8.0";
#endif
}
