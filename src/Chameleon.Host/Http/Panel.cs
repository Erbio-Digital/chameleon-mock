using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Chameleon.Host.Http;

/// The live panel (web/), built into wwwroot/ui. Served before the sandbox middleware and the
/// capture, so it never enters the journal and never shadows a connector route: only /ui falls
/// back to index.html, an unknown provider path stays 404.
public static class Panel
{
    public const string Path = "/ui";

    public static void UsePanel(this WebApplication app)
    {
        var root = app.Configuration["PANEL_ROOT"] ?? System.IO.Path.Combine(app.Environment.ContentRootPath, "wwwroot", "ui");

        app.MapWhen(http => http.Request.Path == "/", branch => branch.Run(http =>
        {
            http.Response.Redirect($"{Path}/");
            return Task.CompletedTask;
        }));

        app.Map(Path, branch =>
        {
            if (!File.Exists(System.IO.Path.Combine(root, "index.html")))
            {
                branch.Run(http =>
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return http.Response.WriteAsJsonAsync(new { message = "panel not built: run pnpm build in web/" });
                });
                return;
            }

            var files = new PhysicalFileProvider(root);
            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                // Vite hashes the asset names: cache them for good, never the page.
                OnPrepareResponse = ctx => ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                    ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
            });
            branch.Run(async http =>
            {
                http.Response.ContentType = "text/html; charset=utf-8";
                http.Response.Headers[HeaderNames.CacheControl] = "no-cache";
                await http.Response.SendFileAsync(files.GetFileInfo("index.html"));
            });
        });
    }
}
