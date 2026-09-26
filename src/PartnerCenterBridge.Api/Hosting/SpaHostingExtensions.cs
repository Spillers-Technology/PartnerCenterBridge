using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Serves the compiled SPA from the API process: the Vite dist embedded into the Local Workbench
/// build, or a folder named by <c>Spa:Path</c>. When neither exists (the container image, where
/// nginx serves the SPA) nothing is registered and behavior is unchanged.
/// </summary>
public static class SpaHostingExtensions
{
    /// <summary>Embedded resource root for web/dist in a PcbLocalWorkbench build (see the csproj).</summary>
    public const string EmbeddedRoot = "spa";

    private static readonly string[] ServerPrefixes = { "/api", "/mcp", "/health", "/swagger" };

    public static IFileProvider? ResolveSpaFiles(IConfiguration cfg)
    {
        var folder = cfg["Spa:Path"];
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var full = Path.GetFullPath(folder);
            if (!File.Exists(Path.Combine(full, "index.html")))
                throw new InvalidOperationException($"Spa:Path '{full}' does not contain index.html.");
            return new PhysicalFileProvider(full);
        }

        try
        {
            var embedded = new ManifestEmbeddedFileProvider(typeof(SpaHostingExtensions).Assembly, EmbeddedRoot);
            return embedded.GetFileInfo("index.html").Exists ? embedded : null;
        }
        catch (InvalidOperationException)
        {
            // No embedded manifest, or it has no spa folder: this build carries no SPA.
            return null;
        }
    }

    /// <summary>Static files for the SPA. Call before routing so asset requests never reach endpoints.</summary>
    public static IFileProvider? UseBridgeSpaStaticFiles(this WebApplication app)
    {
        var files = ResolveSpaFiles(app.Configuration);
        if (files is null) return null;

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = context => ApplyCacheHeaders(context.Context)
        });
        return files;
    }

    /// <summary>
    /// Deep links (/people/x/y) get index.html so a refresh survives. Only GET/HEAD for paths that are
    /// not server surfaces and do not look like a file; everything else stays a plain 404.
    /// </summary>
    public static void MapBridgeSpaFallback(this WebApplication app, IFileProvider files)
    {
        // "{*path}" rather than the default "{*path:nonfile}", which would 404 dotted deep links.
        app.MapFallback("{*path}", async context =>
        {
            var path = context.Request.Path;
            var isNavigation = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && !ServerPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                && !LooksLikeAsset(path.Value ?? "");
            var index = files.GetFileInfo("index.html");
            if (!isNavigation || !index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentLength = index.Length;
            if (HttpMethods.IsHead(context.Request.Method)) return;
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }).AllowAnonymous();
    }

    // Only real asset extensions count as "a file that is missing" -- a deep link can legitimately
    // end in something dotted, such as a UPN (/people/t/jane.doe@contoso.com).
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".map", ".json", ".html", ".txt", ".webmanifest", ".xml",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif",
        ".woff", ".woff2", ".ttf", ".otf", ".eot"
    };

    private static bool LooksLikeAsset(string path) => AssetExtensions.Contains(Path.GetExtension(path));

    private static void ApplyCacheHeaders(HttpContext context)
    {
        // Vite fingerprints everything under /assets, so those can be cached forever; index.html
        // and the unhashed root files must be revalidated so a new build is picked up.
        context.Response.Headers[HeaderNames.CacheControl] =
            context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    }
}
