using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Diagnostics;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperProcessManager : IAsyncDisposable
{
    private readonly IPluginContext _context;
    private readonly IPluginTaskService _tasks;
    private readonly IPluginProcessService _processes;
#if TERRACOTTA_PACKAGE_ASSETS
    private readonly IPluginPackageAssetService? _packageAssets;
#endif
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _processCancellation;
    private IPluginTaskRegistration? _processTask;
    private LocalIpcEndpoint? _endpoint;
    private HelperIpcClient? _client;
    private PluginProcessResult? _lastResult;
    private string? _lastHelperVersion;

    public HelperProcessManager(
        IPluginContext context,
        IPluginTaskService tasks,
        IPluginProcessService processes
#if TERRACOTTA_PACKAGE_ASSETS
        , IPluginPackageAssetService? packageAssets = null
#endif
        )
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
#if TERRACOTTA_PACKAGE_ASSETS
        _packageAssets = packageAssets;
#endif
    }

    public PluginProcessResult? LastResult => _lastResult;

    public string? LastHelperVersion => _lastHelperVersion;

    public async ValueTask<HelperIpcClient> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            string helperPath = await ResolveHelperPathAsync(
#if TERRACOTTA_PACKAGE_ASSETS
                _packageAssets,
#endif
                cancellationToken).ConfigureAwait(false);

            string authenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _endpoint = LocalIpcEndpoint.Create(_context.Directories.Temp);
            _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Stopping);

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

            _processTask = _tasks.Run(PluginIds.Plugin + ".helper", async taskCancellationToken =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                    taskCancellationToken,
                    _processCancellation.Token);
                _lastResult = await _processes.RunAsync(request, linked.Token).ConfigureAwait(false);
                if (_lastResult.ExitCode != 0)
                {
                    string error = SensitiveDataRedactor.Redact(_lastResult.StandardError);
                    _context.Logger.Warn($"Terracotta Helper exited with code {_lastResult.ExitCode}: {error}");
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
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
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
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
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

    private static ValueTask<string> ResolveHelperPathAsync(
#if TERRACOTTA_PACKAGE_ASSETS
        IPluginPackageAssetService? packageAssets,
#endif
        CancellationToken cancellationToken)
    {
        string rid = RuntimePlatformResolver.ResolveCurrentRid();
#if TERRACOTTA_PACKAGE_ASSETS
        if (packageAssets is not null)
            return ResolvePackageAssetAsync(packageAssets, rid, cancellationToken);
#endif

        cancellationToken.ThrowIfCancellationRequested();
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

        return ValueTask.FromResult(helperPath);
    }

#if TERRACOTTA_PACKAGE_ASSETS
    private static async ValueTask<string> ResolvePackageAssetAsync(
        IPluginPackageAssetService packageAssets,
        string rid,
        CancellationToken cancellationToken)
    {
        string relativePath = HelperPackageResolver.GetRelativePath(rid);
        PluginPackageAssetResult result = await packageAssets
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
#endif

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
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
    }
}
