using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace MmiConverter.Services;

internal sealed class NativeTools : IDisposable
{
    private string? directory;
    private readonly object gate = new();

    internal static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing application resource: {name}");

    private string Executable(string name)
    {
        lock (gate)
        {
            directory ??= Directory.CreateTempSubdirectory("MMIConverter-").FullName;
            string destination = Path.Combine(directory, name);
            if (!File.Exists(destination))
            {
                using Stream source = Resource("Native." + name);
                using var target = File.Open(destination, FileMode.CreateNew);
                source.CopyTo(target);
            }
            return destination;
        }
    }

    internal async Task<string> RunAsync(string name, IEnumerable<string> arguments, CancellationToken token,
        Action<string>? progress = null, Stream? input = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Executable(name))
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {name}");
        using var abort = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = progress is null ? process.StandardOutput.ReadToEndAsync()
            : ReadProgressAsync(process.StandardOutput, progress);
        Exception? pipeFailure = null;
        if (input is not null)
        {
            try { await input.CopyToAsync(process.StandardInput.BaseStream, token).ConfigureAwait(false); }
            catch (IOException ex) { pipeFailure = ex; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { try { process.StandardInput.Close(); } catch (IOException) { } }
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await output.ConfigureAwait(false);
        string stderr = (await errors.ConfigureAwait(false)).Trim();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || pipeFailure is not null)
            throw new InvalidDataException(stderr.Length == 0 ? $"{name} could not process this file" : stderr, pipeFailure);
        return stdout;
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<string> progress)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) progress(line);
        return "";
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (directory is null) return;
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            directory = null;
        }
    }
}
