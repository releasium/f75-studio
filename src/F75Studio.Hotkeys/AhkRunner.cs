using System.Diagnostics;
using System.Text;

namespace F75Studio.Hotkeys;

public sealed class AhkRunner : IAhkRunner, IDisposable
{
    private static readonly TimeSpan ValidateTimeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public AhkRunner(string? ahkExePath) => AhkExePath = ahkExePath;

    public string? AhkExePath { get; set; }

    public event EventHandler<string>? RunningChanged;

    public IReadOnlyCollection<string> RunningScripts
    {
        get { lock (_gate) return _processes.Where(p => !p.Value.HasExited).Select(p => p.Key).ToList(); }
    }

    public async Task<AhkValidationResult> ValidateAsync(string scriptPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(RequireExe())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("/ErrorStdOut=UTF-8");
        psi.ArgumentList.Add("/validate");
        psi.ArgumentList.Add(Path.GetFullPath(scriptPath));

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ValidateTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill();
            return new AhkValidationResult(false, "AutoHotkey did not finish validating within 10 seconds.", null);
        }

        var output = await stderr + await stdout;
        return process.ExitCode == 0 ? AhkValidationResult.Ok : AhkErrorParser.Parse(output);
    }

    public void Start(string scriptPath)
    {
        var exe = RequireExe();
        var full = Path.GetFullPath(scriptPath);
        Stop(full);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(full)! };
        psi.ArgumentList.Add(full);
        var process = Process.Start(psi)!;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                if (_processes.TryGetValue(full, out var current) && current == process) _processes.Remove(full);
            }
            RunningChanged?.Invoke(this, full);
        };

        lock (_gate) _processes[full] = process;
        RunningChanged?.Invoke(this, full);
    }

    public void Stop(string scriptPath)
    {
        var full = Path.GetFullPath(scriptPath);
        Process? process;
        lock (_gate)
        {
            if (!_processes.Remove(full, out process)) return;
        }

        try
        {
            if (!process.HasExited && !(AhkWindow.RequestExit(process.Id) && process.WaitForExit(1500)))
            {
                process.Kill();
                process.WaitForExit(1500);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            process.Dispose();
        }
        RunningChanged?.Invoke(this, full);
    }

    public bool IsRunning(string scriptPath)
    {
        lock (_gate) return _processes.TryGetValue(Path.GetFullPath(scriptPath), out var p) && !p.HasExited;
    }

    public void StopAll()
    {
        List<string> paths;
        lock (_gate) paths = _processes.Keys.ToList();
        foreach (var path in paths) Stop(path);
    }

    public void Dispose() => StopAll();

    private string RequireExe() =>
        AhkExePath is { } exe && File.Exists(exe) ? exe : throw new AhkNotFoundException();
}
