using System.Diagnostics;
using Xunit;

// GoBuildContextTests exercises the sidecar's handling of the *inherited* GOFLAGS, which
// can only be set process-wide (Environment.SetEnvironmentVariable) because the public
// API deliberately offers no environment injection point. xUnit runs separate test
// classes in parallel by default, so an unserialised run would leak that GOFLAGS into the
// concurrent `go mod tidy` / `go build` invocations of GoModuleResolutionTests and friends
// and make them nondeterministic. The suite is process-bound (it spawns go), not
// CPU-bound, so serialising it costs little.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Orika.Go.CodeAnalysis.Tests;

/// <summary>
/// Sidecar wiring for the test run.
///
/// Orika.Go.CodeAnalysis resolves the orika-goc sidecar via OrikaGoToolResolver, whose
/// documented priority chain is: explicit path arg > ORIKA_GOC env var > next to the
/// Orika.Go.CodeAnalysis assembly > PATH. The integrator sets ORIKA_GOC to the built
/// orika-goc executable before running these tests, so the env-var branch of the
/// resolver is what these tests exercise. This guard fails fast with a clear message
/// when ORIKA_GOC is set but points at nothing, instead of letting every test die
/// with a less obvious process-start error.
/// </summary>
internal static class Sidecar
{
    public static void EnsureConfigured()
    {
        string? goc = Environment.GetEnvironmentVariable("ORIKA_GOC");
        if (!string.IsNullOrWhiteSpace(goc) && !File.Exists(goc))
        {
            throw new InvalidOperationException(
                $"ORIKA_GOC is set to '{goc}' but no file exists at that path. " +
                "Point it at the built orika-goc executable (or unset it to fall back " +
                "to assembly-adjacent / PATH resolution).");
        }
    }
}

/// <summary>
/// A throwaway Go module on disk: a temp directory containing a go.mod plus any
/// source files the test writes. Files are written with LF line endings and the
/// exact bytes given, because the tests assert 1-based line/column positions that
/// were verified against go/parser + go/types (go1.21) for these exact sources.
/// </summary>
internal sealed class TempGoModule : IDisposable
{
    public string Directory { get; }

    public TempGoModule(string moduleName = "orikatestmod")
    {
        Directory = Path.Combine(
            Path.GetTempPath(),
            "orika-go-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        WriteFile("go.mod", "module " + moduleName + "\n\ngo 1.21\n");
    }

    /// <summary>Writes exact bytes (UTF-8, no BOM, LF only) and returns the absolute path.</summary>
    public string WriteFile(string relativeName, string content)
    {
        string path = Path.Combine(Directory, relativeName);
        File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(content));
        return path;
    }

    /// <summary>
    /// Runs the real <c>go</c> command inside this module and returns its exit code and
    /// combined output. Used to establish the ground truth a test compares against:
    /// "go build accepts this module" is the yardstick GetDiagnostics must agree with.
    /// </summary>
    public (int ExitCode, string Output) RunGo(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "go",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'go'. Is the Go toolchain on PATH?");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(300_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"'go {string.Join(' ', args)}' did not exit within 300 s.");
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; leaked temp dirs are not a test failure.
        }
    }
}

internal static class ProcessRunner
{
    /// <summary>Runs an executable with stdout/stderr capture; fails the test on timeout.</summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(
        string fileName, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Process '{fileName}' did not exit within {timeoutMs} ms.");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
