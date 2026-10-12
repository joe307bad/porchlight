using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Porchlight.Server;

/// <summary>
/// Serves the Unity WebGL build (browser/dist) from the same origin as the API. Unity writes files
/// like Build/x.wasm.gz, so they need their real content type plus a Content-Encoding header.
/// </summary>
public static class UnityWebHost
{
    public static void Map(WebApplication app, string? webRootSetting)
    {
        var webRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, webRootSetting ?? "../../browser/dist"));
        Directory.CreateDirectory(webRoot);
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            // Files are resolved per request, so a build that lands later is served without a restart.
            app.Logger.LogWarning(
                "No WebGL build in {WebRoot} yet. The API works; build with ./browser-dev.sh --build or Porchlight > Build > WebGL in Unity",
                webRoot);
        }

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".data"] = "application/octet-stream";
        types.Mappings[".unityweb"] = "application/octet-stream";
        types.Mappings[".gz"] = "application/octet-stream";
        types.Mappings[".br"] = "application/octet-stream";

        var files = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                var name = ctx.File.Name;
                var encoding = name.EndsWith(".gz") ? "gzip" : name.EndsWith(".br") ? "br" : null;
                if (encoding != null)
                {
                    headers[HeaderNames.ContentEncoding] = encoding;
                    var inner = name[..name.LastIndexOf('.')];
                    ctx.Context.Response.ContentType = types.TryGetContentType(inner, out var type) ? type : "application/octet-stream";
                }

                // Dev server: always pick up a fresh build.
                headers[HeaderNames.CacheControl] = "no-cache";
            },
        });

        app.Logger.LogInformation("Serving WebGL build from {WebRoot}", webRoot);
    }
}
