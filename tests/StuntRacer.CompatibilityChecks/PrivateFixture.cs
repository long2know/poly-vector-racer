using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StuntRacer.CompatibilityChecks;

internal sealed class PrivateFixture : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<string> _directories = [];

    public PrivateFixture()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "scripts", "dotnet.sh")))
            current = current.Parent;
        RepositoryRoot = current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        DirectoryPath = Path.Combine(RepositoryRoot, "artifacts", "amiga-compat", $"synthetic-checks-{Guid.NewGuid():N}");
        Check.Require(!Directory.Exists(DirectoryPath) && !File.Exists(DirectoryPath), "private fixture path already exists");
        Directory.CreateDirectory(DirectoryPath);
        _directories.Add(DirectoryPath);
    }

    public string RepositoryRoot { get; }
    public string DirectoryPath { get; }
    public static int InvocationCount { get; private set; }

    public string Output(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        Check.Require(Path.GetFullPath(path).StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "fixture files must remain within their private directory");
        _files.Add(path);
        return path;
    }

    public string Write(string name, byte[] bytes)
    {
        var path = Output(name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
        return path;
    }

    public string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    public string Write(string name, JsonNode json) => Write(name, json.ToJsonString());

    public string EscapingOutput()
    {
        var name = $"synthetic-escape-{Guid.NewGuid():N}.track.json";
        var resolved = Path.Combine(RepositoryRoot, "artifacts", name);
        Check.Require(!File.Exists(resolved), "escape-test destination already exists");
        _files.Add(resolved);
        return Path.Combine(DirectoryPath, "..", "..", name);
    }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        Check.Require(Path.GetDirectoryName(Path.GetFullPath(path)) == DirectoryPath,
            "fixture subdirectories must be explicit children");
        Check.Require(!System.IO.Directory.Exists(path) && !File.Exists(path), "fixture subdirectory already exists");
        System.IO.Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    public string LinkFile(string name, string target)
    {
        var path = Output(name);
        File.CreateSymbolicLink(path, target);
        return path;
    }

    public string LinkDirectory(string name, string target)
    {
        var path = Path.Combine(DirectoryPath, name);
        Check.Require(Path.GetDirectoryName(Path.GetFullPath(path)) == DirectoryPath, "fixture directory link escaped");
        System.IO.Directory.CreateSymbolicLink(path, target);
        _directories.Add(path);
        return path;
    }

    public CommandResult Invoke(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                            ?? throw new InvalidOperationException("Build configuration not found.");
        var tool = Path.Combine(RepositoryRoot, "tools", "AmigaTrackImport", "bin", configuration,
            "net10.0", "AmigaTrackImport.dll");
        Check.Require(File.Exists(tool), $"Build the actual importer before CLI checks: {tool}");
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", "dotnet.sh"));
        start.ArgumentList.Add(tool);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Importer process did not start.");
        InvocationCount++;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Importer CLI timed out after 30 seconds.");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        return new(process.ExitCode, stdout.Result, stderr.Result);
    }

    public void Dispose()
    {
        foreach (var path in _files.AsEnumerable().Reverse().Distinct())
            File.Delete(path);
        foreach (var path in _directories.AsEnumerable().Reverse())
            System.IO.Directory.Delete(path);
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public JsonDocument Json(string context)
    {
        try
        {
            return JsonDocument.Parse(StandardOutput);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{context}: stdout was not one JSON document: {StandardOutput}; stderr: {StandardError}", exception);
        }
    }
}
