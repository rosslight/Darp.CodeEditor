namespace ConsumerCompatibleB;

/// <summary>Needs Newtonsoft.Json 13.0.3 or later.</summary>
public static class Json
{
    /// <summary>Writes through the one version of the dependency that the payload has.</summary>
    public static string Write() => Newtonsoft.Json.JsonConvert.SerializeObject(new { from = "B" });
}
