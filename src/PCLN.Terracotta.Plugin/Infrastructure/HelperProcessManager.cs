using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Diagnostics;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(10);

    private readonly IPluginContext _context;
    private readonly IPluginTaskService _tasks;
    private readonly IPluginProcessService _processes;
    private readonly IPluginPackageAssetService? _packageAssets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _processCancellation;
    private IPluginTaskRegistration? _processTask;
    private LocalIpcEndpoint? _endpoint;
    private HelperIpcClient? _client;
    private PluginProcessResult? _lastResult;
    private string? _lastHelperVersion;
    private int _crashCount;
    private DateTimeOffset _crashWindowStart = DateTimeOffset.MinValue;
    private volatile bool _intentionalStop;
    private int _generation;

    public HelperProcessManager(
        IPluginContext context,
        IPluginTaskService tasks,
        IPluginProcessService processes,
        IPluginPackageAssetService? packageAssets = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _packageAssets = packageAssets;
    }

    public PluginProcessResult? LastResult => _lastResult;

    public string? LastHelperVersion => _lastHelperVersion;

    public int CrashCountInWindow => _crashCount;

    /// <summary>
    /// Raised after an unexpected Helper process exit. Arguments: (generation, willAutoRestart).
    /// </summary>
    public event EventHandler<HelperProcessExitEventArgs>? HelperProcessExited;

    public async ValueTask<HelperIpcClient> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync(intentional: true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(intentional: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async ValueTask<HelperIpcClient> StartCoreAsync(CancellationToken cancellationToken)
    {
        string helperPath = await ResolveHelperPathAsync(cancellationToken).ConfigureAwait(false);

        string authenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _endpoint = LocalIpcEndpoint.Create(_context.Directories.Temp);
        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Stopping);
        _intentionalStop = false;
        int generation = Interlocked.Increment(ref _generation);

        PluginProcessRequest request = new()
        {
            FileName = helperPath,
            Arguments =
            [
                "--ipc", _endpoint.Address,
                "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--data-dir", _context.Directories.Data,
                "--log-dir", _context.Directories.Logs,
                "--protocol-version", ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture)
            ],
            WorkingDirectory = _context.Directories.Root,
            CaptureOutput = true,
            StandardInput = authenticationToken,
            Timeout = null
        };

        _processTask = _tasks.Run(PluginIds.Plugin + $".helper.{generation}", async taskCancellationToken =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                taskCancellationToken,
                _processCancellation!.Token);
            try
            {
                _lastResult = await _processes.RunAsync(request, linked.Token).ConfigureAwait(false);
                if (_lastResult.ExitCode != 0)
                {
                    string error = SensitiveDataRedactor.Redact(_lastResult.StandardError);
                    _context.Logger.Warn($"Terracotta Helper exited with code {_lastResult.ExitCode}: {error}");
                }
            }
            finally
            {
                if (!_intentionalStop)
                    await OnProcessEndedAsync(generation).ConfigureAwait(false);
            }
        });

        using CancellationTokenSource startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        Exception? lastError = null;
        while (!startupTimeout.IsCancellationRequested)
        {
            try
            {
                _client = await HelperIpcClient.ConnectAsync(
                    _endpoint.Address,
                    authenticationToken,
                    _context.Plugin.Version.ToString(),
                    _tasks,
                    PluginIds.Plugin + $".helper-ipc-reader.{generation}",
                    startupTimeout.Token).ConfigureAwait(false);
                _lastHelperVersion = _client.HelperVersion;
                return _client;
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or TimeoutException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(80), startupTimeout.Token).ConfigureAwait(false);
            }
        }

        throw new HelperProtocolException(
            $"{ErrorCodeCatalog.HelperDisconnected}: Timed out while connecting to Terracotta Helper.",
            lastError ?? new TimeoutException());
    }

    private async Task OnProcessEndedAsync(int generation)
    {
        bool intentional;
        bool willAutoRestart = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation)
                return;

            intentional = _intentionalStop;
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }

            if (!intentional)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - _crashWindowStart > CrashWindow)
                {
                    _crashWindowStart = now;
                    _crashCount = 0;
                }

                _crashCount++;
                willAutoRestart = _crashCount <= 1;
                _context.Logger.Warn(
                    $"Terracotta Helper exited unexpectedly (crash #{_crashCount} in window). AutoRestart={willAutoRestart}");
            }
        }
        finally
        {
            _gate.Release();
        }

        if (!intentional)
            HelperProcessExited?.Invoke(this, new HelperProcessExitEventArgs(generation, willAutoRestart, _lastResult));
    }

    private async ValueTask<string> ResolveHelperPathAsync(CancellationToken cancellationToken)
    {
        string rid = RuntimePlatformResolver.ResolveCurrentRid();
        if (_packageAssets is not null)
        {
            string relativePath = HelperPackageResolver.GetRelativePath(rid);
            PluginPackageAssetResult result = await _packageAssets
                .ResolveAsync(relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw result.Status switch
                {
                    PluginPackageAssetStatus.IntegrityFailure => new InvalidOperationException(
                        $"{ErrorCodeCatalog.HelperIntegrityFailure}: {result.Message ?? "Terracotta Helper integrity verification failed."}"),
                    PluginPackageAssetStatus.NotFound => new FileNotFoundException(
                        $"{ErrorCodeCatalog.HelperMissing}: Terracotta Helper is not installed for this platform.",
                        relativePath),
                    _ => new InvalidOperationException(
                        $"{ErrorCodeCatalog.HelperMissing}: {result.Message ?? "Terracotta Helper could not be resolved from the signed package."}")
                };
            }

            return result.Asset!.FullPath;
        }

        string assemblyPath = typeof(PluginEntry).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new InvalidOperationException(
                $"{ErrorCodeCatalog.HelperMissing}: The current PCL.Plugin runtime does not expose the signed package asset root.");
        }

        string helperPath = HelperPackageResolver.Resolve(assemblyPath, rid);
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                $"{ErrorCodeCatalog.HelperMissing}: Terracotta Helper is not installed for this platform.",
                helperPath);
        }

        return helperPath;
    }

    private async ValueTask StopCoreAsync(bool intentional, CancellationToken cancellationToken)
    {
        _intentionalStop = intentional;
        if (_client is not null)
        {
            try
            {
                await _client.SendAsync(
                    HelperMessageTypes.Shutdown,
                    new { },
                    HelperMessageTypes.ShutdownAccepted,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HelperProtocolException)
            {
                _context.Logger.Debug($"Helper did not acknowledge shutdown: {exception.Message}");
            }

            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        if (_processTask is not null)
        {
            try
            {
                await _processTask.Completion.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (_processCancellation is not null)
                    await _processCancellation.CancelAsync().ConfigureAwait(false);
            }

            await _processTask.DisposeAsync().ConfigureAwait(false);
            _processTask = null;
        }

        _processCancellation?.Dispose();
        _processCancellation = null;
        if (_endpoint is not null)
        {
            await _endpoint.DisposeAsync().ConfigureAwait(false);
            _endpoint = null;
        }

        if (intentional)
        {
            _crashCount = 0;
            _crashWindowStart = DateTimeOffset.MinValue;
        }
    }
}

public sealed class HelperProcessExitEventArgs(
    int generation,
    bool willAutoRestart,
    PluginProcessResult? result) : EventArgs
{
    public int Generation { get; } = generation;

    public bool WillAutoRestart { get; } = willAutoRestart;

    public PluginProcessResult? Result { get; } = result;
}
