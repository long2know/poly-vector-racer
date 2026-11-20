using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;

var bundle = Environment.GetEnvironmentVariable("STUNT_BUNDLE");
if (string.IsNullOrWhiteSpace(bundle))
{
    bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../StuntRacer.Browser/bin/Release/net10.0/browser-wasm/AppBundle"));
}
if (!File.Exists(Path.Combine(bundle, "index.html")))
{
    throw new DirectoryNotFoundException(
        $"The playable WASM bundle is missing at {bundle}. Run bash scripts/build.sh first.");
}

var trackCatalog = ExternalTrackCatalog.Load(
    Environment.GetEnvironmentVariable("STUNT_TRACK_CATALOG"),
    Environment.GetEnvironmentVariable("STUNT_AMIGA_PACKAGE"), bundle);
var flightPackagePath = Environment.GetEnvironmentVariable("STUNT_FLIGHT_PACKAGE");
var flightPackage = string.IsNullOrWhiteSpace(flightPackagePath) ? null :
    ExternalSourceData.ReadJson(flightPackagePath, bundle, "fa18-amiga-runtime");
var worldFlightPath = Environment.GetEnvironmentVariable("STUNT_WORLD_FLIGHT");
var worldFlight = string.IsNullOrWhiteSpace(worldFlightPath) ? null : WorldFlightCatalog.Load(worldFlightPath, bundle);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = bundle
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/wasm", "application/octet-stream"]);
});
var app = builder.Build();
var files = new PhysicalFileProvider(bundle);
var types = new FileExtensionContentTypeProvider();
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";
types.Mappings[".symbols"] = "application/octet-stream";

var mount = (Environment.GetEnvironmentVariable("BASE_URL") ?? "").TrimEnd('/');
if (mount.Length > 0 && (!mount.StartsWith('/') || mount.Split('/').Skip(1)
    .Any(segment => segment.Length == 0 || segment is "." or ".." ||
        segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~'))))
{
    throw new ArgumentException("BASE_URL must be an absolute URL path such as /stunt-car-racer.");
}

app.UseResponseCompression();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/healthz")
    {
        await context.Response.WriteAsJsonAsync(new { status = "ready", game = "poly-vector-racer", baseUrl = mount });
        return;
    }
    if (mount.Length > 0)
    {
        if (context.Request.Path == mount)
        {
            context.Response.Redirect(mount + "/" + context.Request.QueryString, permanent: true, preserveMethod: true);
            return;
        }
        if (!context.Request.Path.StartsWithSegments(mount))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }
    await next();
});
if (mount.Length > 0)
{
    app.UsePathBase(mount);
}
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    ContentTypeProvider = types,
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapGet("/runtime-config.json", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        sourcePackageAvailable = trackCatalog.DefaultId is not null,
        sourcePackageUrl = trackCatalog.DefaultId is null ? null :
            $"./source-package.json?id={Uri.EscapeDataString(trackCatalog.DefaultId)}",
        flightPackageAvailable = flightPackage is not null,
        flightPackageUrl = flightPackage is null ? null : "./flight-package.json",
        worldFlightUrl = worldFlight is null ? null : "./world-flight/manifest.json",
        tracks = trackCatalog.Entries.Select(track => new
        {
            track.Id, track.Name, track.Group, track.Status, track.Reason,
            sourcePackageUrl = track.Status == "available" ? $"./source-package.json?id={Uri.EscapeDataString(track.Id)}" : null
        })
    });
});
app.MapGet("/source-package.json", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var package = trackCatalog.Find(context.Request.Query["id"].FirstOrDefault());
    return package is null ? Results.NotFound() : Results.Bytes(package, "application/json");
});
app.MapGet("/flight-package.json", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return flightPackage is null ? Results.NotFound() : Results.Bytes(flightPackage, "application/json");
});
app.MapGet("/world-flight/manifest.json", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return worldFlight is null ? Results.NotFound() : Results.Bytes(worldFlight.Manifest, "application/json");
});
app.MapGet("/world-flight/{revision}/tiles/{region}/{level:int}/{x:int}/{z:int}.bin",
    (HttpContext context, string revision, string region, int level, int x, int z) =>
    {
        if (worldFlight is null || !worldFlight.TryTile(revision, region, level, x, z, out var path))
            return Results.NotFound();
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(path!, "application/octet-stream");
    });
app.MapGet("/world-flight/{revision}/{city}-buildings.json", (HttpContext context, string revision, string city) =>
{
    var path = worldFlight?.BuildingsPath(revision, city);
    if (path is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(path, "application/json");
});
app.Run();
