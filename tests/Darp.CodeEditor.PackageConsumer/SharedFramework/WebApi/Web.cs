namespace ConsumerWebApi;

/// <summary>Needs ASP.NET Core, which the runner's runtime doesn't have.</summary>
public static class Web
{
    /// <summary>Uses the shared framework.</summary>
    public static string Name() => typeof(Microsoft.AspNetCore.Http.HttpContext).FullName ?? "";
}
