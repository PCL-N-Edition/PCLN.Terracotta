using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Infrastructure;
using Cn.Pcln.Terracotta.Services;
using Cn.Pcln.Terracotta.Views;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta;

public sealed class PluginEntry : IPclNPlugin, IAsyncDisposable
{
    private TerracottaController? _controller;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IPluginSettingsStore settings = context.Services.Require<IPluginSettingsStore>();
        IPluginCommandService commands = context.Services.Require<IPluginCommandService>();
        IPluginTaskService tasks = context.Services.Require<IPluginTaskService>();
        IPluginGameSessionService sessions = context.Services.Require<IPluginGameSessionService>();
        IPluginGameOutputService output = context.Services.Require<IPluginGameOutputService>();
        IPluginLaunchEventService launchEvents = context.Services.Require<IPluginLaunchEventService>();
        IPluginProcessService processes = context.Services.Require<IPluginProcessService>();
        IAvaloniaPluginPageService pages = context.Services.Require<IAvaloniaPluginPageService>();
        context.Services.TryGet<IAvaloniaPluginWindowService>(out IAvaloniaPluginWindowService? windows);
#if TERRACOTTA_PACKAGE_ASSETS
        context.Services.TryGet<IPluginPackageAssetService>(out IPluginPackageAssetService? packageAssets);
#endif
        context.Services.TryGet<IPluginSecureStorage>(out IPluginSecureStorage? secureStorage);
        HelperProcessManager helperProcess = new(
            context,
            tasks,
            processes
#if TERRACOTTA_PACKAGE_ASSETS
            , packageAssets
#endif
            );

        _controller = new TerracottaController(
            context,
            new PluginStateStore(settings),
            commands,
            tasks,
            new GameSessionCoordinator(sessions),
            output,
            launchEvents,
            pages,
            windows,
            new HelperRoomGateway(helperProcess, new SecureIdentityStore(secureStorage)),
            helperProcess);

        context.Lifetime.Track(pages.Register(new AvaloniaPluginPageDescriptor(
            new PluginPageDescriptor(
                PluginIds.PageRegistration,
                PluginIds.PageRoute,
                "陶瓦联机",
                "lucide/network",
                420),
            () => new TerracottaPage(_controller))));

        if (windows is not null)
        {
            context.Lifetime.Track(windows.Register(new AvaloniaPluginWindowDescriptor(
                PluginIds.DiagnosticsWindowRegistration,
                PluginIds.DiagnosticsWindow,
                () => new TerracottaDiagnosticsWindow(_controller))));
        }

        _controller.RegisterCommands();
        _controller.Start();
        context.Logger.Info("Terracotta plugin initialized.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
            _controller = null;
        }
    }
}
