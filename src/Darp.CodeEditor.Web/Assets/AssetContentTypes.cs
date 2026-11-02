namespace Darp.CodeEditor.Web.Assets;

internal static class AssetContentTypes
{
    public static string Get(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".js" or ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".json" or ".map" => "application/json",
            ".ttf" => "font/ttf",
            ".woff2" => "font/woff2",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
}
