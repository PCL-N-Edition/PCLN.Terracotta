using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Infrastructure;
using Cn.Pcln.Terracotta.Services;
using Cn.Pcln.Terracotta.Views;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta;

public sealed class PluginEntry : IPclNPlugin, IAsyncDisposable
{
    private TerracottaController? _controller;
    private IPluginContext? _context;
    private IAvaloniaPluginWindowService? _windows;

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        _context = context;
        IPluginSettingsStore settings = context.Services.Require<IPluginSettingsStore>();
        IPluginCommandService commands = context.Services.Require<IPluginCommandService>();
        IPluginTaskService tasks = context.Services.Require<IPluginTaskService>();
        IPluginGameSessionService sessions = context.Services.Require<IPluginGameSessionService>();
        context.Services.TryGet<IPluginGameOutputService>(out IPluginGameOutputService? output);
        IPluginLaunchEventService launchEvents = context.Services.Require<IPluginLaunchEventService>();
        IPluginProcessService processes = context.Services.Require<IPluginProcessService>();
        IPluginLocalizationService localization = context.Services.Require<IPluginLocalizationService>();
        TerracottaLocalizer localizer = new(localization);
        IAvaloniaPluginPageService pages = context.Services.Require<IAvaloniaPluginPageService>();
        context.Services.TryGet<PclUiService>(out PclUiService? pclUi);
        context.Services.TryGet<IPluginBackgroundTaskService>(out IPluginBackgroundTaskService? backgroundTasks);
        context.Services.TryGet<IAvaloniaPluginWindowService>(out IAvaloniaPluginWindowService? windows);
        _windows = windows;
        context.Services.TryGet<IPluginPackageAssetService>(out IPluginPackageAssetService? packageAssets);
        context.Services.TryGet<IPluginSecureStorage>(out IPluginSecureStorage? secureStorage);
        if (secureStorage is null)
        {
            string warning = localizer.Get(
                "terracotta.identity.temporary",
                "安全存储不可用，本次会话将使用临时身份；重启启动器后身份会变化。");
            context.Logger.Warn(warning);
            if (context.Services.TryGet<IPluginNotificationService>(out IPluginNotificationService? notifications) && notifications is not null)
                notifications.ShowWarning(warning);
        }
        HelperProcessManager helperProcess = new(context, tasks, processes, packageAssets);

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
            new HelperRoomGateway(helperProcess, new SecureIdentityStore(secureStorage), tasks),
            helperProcess,
            localizer,
            backgroundTasks);

        context.Lifetime.Track(pages.Register(new AvaloniaPluginPageDescriptor(
            new PluginPageDescriptor(
                PluginIds.PageRegistration,
                PluginIds.PageRoute,
                localizer.Get("terracotta.title", "陶瓦联机"),
                "lucide/network",
                420),
            () => new TerracottaPage(_controller, localizer))));

        if (windows is not null)
        {
            context.Lifetime.Track(windows.Register(new AvaloniaPluginWindowDescriptor(
                PluginIds.DiagnosticsWindowRegistration,
                PluginIds.DiagnosticsWindow,
                () => new TerracottaDiagnosticsWindow(_controller, localizer))));
        }

        if (pclUi is not null)
        {
            context.Lifetime.Track(pclUi.Inject(new PclUiContribution
            {
                OperationId = PluginIds.LaunchContribution,
                Target = new UiTargetId("pcl.page.launch"),
                Slot = "primary-actions.after",
                Title = TerracottaLocalizer.Ui("terracotta.title", "陶瓦联机"),
                Order = 420,
                Content = new PclUiCard
                {
                    Title = TerracottaLocalizer.Ui("terracotta.title", "陶瓦联机"),
                    Content = new PclUiStack
                    {
                        Spacing = 10,
                        Children =
                        [
                            new PclUiText
                            {
                                Text = TerracottaLocalizer.Ui("terracotta.quick.description", "创建或加入陶瓦房间，与好友快速建立 Minecraft 联机。")
                            },
                            new PclUiButton
                            {
                                Text = TerracottaLocalizer.Ui("terracotta.open", "打开陶瓦联机"),
                                Style = PclUiButtonStyle.Primary,
                                CommandId = PluginIds.JoinRoomCommand
                            }
                        ]
                    }
                }
            }));
        }

        _controller.RegisterCommands();
        PluginExportRegistrar.Register(context, _controller);
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
        if (_windows is not null && _context is not null)
        {
            IAvaloniaPluginWindowService windows = _windows;
            await _context.Dispatcher.InvokeAsync(() =>
            {
                foreach (TerracottaDiagnosticsWindow window in windows.ListOpenWindows().OfType<TerracottaDiagnosticsWindow>().ToArray())
                {
                    window.Dispose();
                    window.Close();
                }
            }).ConfigureAwait(false);
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
            _controller = null;
        }

        _windows = null;
        _context = null;
    }
}
