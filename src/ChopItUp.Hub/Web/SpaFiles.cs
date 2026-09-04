using Microsoft.Extensions.FileProviders;

namespace ChopItUp.Hub.Web;

/// <summary>Serves the built web client (brief D3: the client is built into <c>wwwroot</c> and served
/// by the hub, so M4 publishes one exe with no separate front-end deploy step).
///
/// The web root is resolved beside the executable rather than from the process's current directory,
/// because the hub is launched by MCP hosts whose working directory is not ours.
///
/// Two rules the fallback must not break. First, a client route such as <c>/rooms/general</c> has no
/// endpoint and must still get the shell. Second, <c>/api</c>, <c>/hub</c>, <c>/mcp</c> and
/// <c>/health</c> must never be answered by the shell: their registered endpoints already win over a
/// fallback, but an unknown path *under* one of them would otherwise return HTML with a 200, which
/// turns a client typo into a mystery instead of a 404.</summary>
public static class SpaFiles
{
    public static string ResolveWebRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : configured;

    /// <summary>Wires static files and the SPA fallback. A missing web root is not an error: a build
    /// on a machine without Node.js produces no client, and the MCP surface must keep working.</summary>
    public static void UseSpaClient(this WebApplication app, string webRoot)
    {
        PhysicalFileProvider? files = null;
        if (Directory.Exists(webRoot))
        {
            files = new PhysicalFileProvider(webRoot);
            app.Lifetime.ApplicationStopped.Register(files.Dispose);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        app.MapFallback(context => Fallback(context, files));
    }

    private static Task Fallback(HttpContext context, IFileProvider? files)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        if (IsReserved(context.Request.Path)) return Task.CompletedTask;

        if (files?.GetFileInfo("index.html") is { Exists: true } index)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            // The shell names hash-stamped asset files, so a cached copy of it points at assets that
            // no longer exist after a rebuild. Assets themselves stay cacheable.
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return context.Response.SendFileAsync(index, context.RequestAborted);
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(
            "No web client is built. Run `npm run build` in src/ChopItUp.Hub/client, or `dotnet build` on a machine with Node.js installed.",
            context.RequestAborted);
    }

    private static bool IsReserved(PathString path) =>
        path.StartsWithSegments("/api")
        || path.StartsWithSegments("/hub")
        || path.StartsWithSegments("/mcp")
        || path.StartsWithSegments("/health");
}
