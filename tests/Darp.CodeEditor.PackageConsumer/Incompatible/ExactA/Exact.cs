namespace ConsumerExactA;

/// <summary>Needs exactly Newtonsoft.Json 13.0.1.</summary>
public static class Exact
{
    /// <summary>Uses the dependency.</summary>
    public static string Write() => Newtonsoft.Json.JsonConvert.SerializeObject(1);
}
