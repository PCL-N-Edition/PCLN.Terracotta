using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Diagnostics;
using Cn.Pcln.Terracotta.Infrastructure;
using Cn.Pcln.Terracotta.Models;
using Cn.Pcln.Terracotta.Services;
using PCL.N.Plugin;
using System.Globalization;
using System.Text;

namespace Cn.Pcln.Terracotta.Application;

public sealed class TerracottaController :
    ITerracottaRoomService,
    ITerracottaNetworkStatusService,
    ITerracottaSessionService,
    ITerracottaDiagnosticsService,
    IAsyncDisposable
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly IPluginContext _context;
    private readonly PluginStateStore _stateStore;
    private readonly IPluginCommandService _commands;
    private readonly IPluginTaskService _tasks;
    private readonly GameSessionCoordinator _sessions;
    private readonly IPluginGameOutputService? _output;
    private readonly IPluginLaunchEventService _launchEvents;
    private readonly IPluginNavigationService _navigation;
    private readonly IAvaloniaPluginWindowService? _windows;
    private readonly HelperRoomGateway _helper;
    private readonly HelperProcessManager _helperProcess;
    private readonly TerracottaLocalizer _localizer;
    private readonly IPluginBackgroundTaskService? _backgroundTasks;
    private readonly RoomStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _taskSync = new();
    private readonly List<IPluginTaskRegistration> _ownedTasks = [];
    private long _operationSequence;
    private TerracottaRoomSnapshot _snapshot = TerracottaRoomSnapshot.Idle;
    private TerracottaCreateRoomRequest? _pendingCreate;
    private TerracottaSettings _settings = new();
    private bool _started;
    private int _disposed;
    private CancellationTokenSource? _statusPollCts;
    private RecoveryIntent? _recovery;

    public TerracottaController(
        IPluginContext context,
        PluginStateStore stateStore,
        IPluginCommandService commands,
        IPluginTaskService tasks,
        GameSessionCoordinator sessions,
        IPluginGameOutputService? output,
        IPluginLaunchEventService launchEvents,
        IPluginNavigationService navigation,
        IAvaloniaPluginWindowService? windows,
        HelperRoomGateway helper,
        HelperProcessManager helperProcess,
        TerracottaLocalizer localizer,
        IPluginBackgroundTaskService? backgroundTasks = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _output = output;
        _launchEvents = launchEvents ?? throw new ArgumentNullException(nameof(launchEvents));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _windows = windows;
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _helperProcess = helperProcess ?? throw new ArgumentNullException(nameof(helperProcess));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _backgroundTasks = backgroundTasks;
        _helper.PushEventReceived += OnHelperPushEvent;
        _helperProcess.HelperProcessExited += OnHelperProcessExited;
    }

    public event EventHandler<TerracottaRoomSnapshot>? SnapshotChanged;

    public TerracottaRoomSnapshot CurrentRoom => _snapshot;

    public TerracottaNetworkStatus? CurrentNetwork => _snapshot.Network;

    public string? BoundGameSessionId => _snapshot.GameSessionId ?? _pendingCreate?.GameSessionId;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        if (_output is not null)
            _context.Lifetime.Track(_output.Subscribe(OnGameOutput));
        _context.Lifetime.Track(_launchEvents.Subscribe(OnLaunchEvent));
        QueueOperation("load-settings", LoadSettingsAsync);
    }

    public void RegisterCommands()
    {
        TrackCommand(PluginIds.CreateRoomCommand, _localizer.Get("terracotta.createRoom", "创建房间"), token =>
            RunConnectionTaskAsync(inner => CreateAsync(new TerracottaCreateRoomRequest(), inner).AsTask(), token));
        TrackCommand(PluginIds.JoinRoomCommand, _localizer.Get("terracotta.joinRoom", "加入房间"), token =>
            _navigation.NavigateAsync(PluginIds.PageRoute, token).AsTask());
        TrackCommand(PluginIds.LeaveRoomCommand, _localizer.Get("terracotta.leaveRoom", "退出房间"), token => LeaveAsync(token).AsTask());
        TrackCommand(PluginIds.CopyRoomCodeCommand, _localizer.Get("terracotta.copyRoomCode", "复制房间码"), CopyRoomCodeAsync);
        TrackCommand(PluginIds.CopyConnectAddressCommand, _localizer.Get("terracotta.copyAddress", "复制联机地址"), CopyConnectAddressAsync);
        TrackCommand(PluginIds.OpenDiagnosticsCommand, _localizer.Get("terracotta.openDiagnostics", "打开陶瓦诊断"), OpenDiagnosticsAsync);
        TrackCommand(PluginIds.ExportDiagnosticsCommand, _localizer.Get("terracotta.exportDiagnostics", "导出陶瓦诊断"), ExportDiagnosticsAsync);
        TrackCommand(PluginIds.RestartHelperCommand, _localizer.Get("terracotta.restartHelper", "重启陶瓦核心"), RestartHelperAsync);
        TrackCommand(PluginIds.OpenHelpCommand, _localizer.Get("terracotta.openHelp", "打开陶瓦帮助"), OpenHelpAsync);
    }

    public void QueueCreate() =>
        QueueOperation("create-room", token => RunConnectionTaskAsync(
            inner => CreateAsync(new TerracottaCreateRoomRequest(), inner).AsTask(), token));

    public void QueueJoin(string roomCode) =>
        QueueOperation("join-room", token => RunConnectionTaskAsync(
            inner => JoinAsync(new TerracottaJoinRoomRequest(roomCode), inner).AsTask(), token));

    public void QueueLeave() => QueueOperation("leave-room", token => LeaveAsync(token).AsTask());

    public void QueueCopyRoomCode() => QueueOperation("copy-room-code", CopyRoomCodeAsync);

    public void QueueDiagnose() => QueueOperation("diagnose", token => RunBackgroundTaskAsync(
        "terracotta.task.diagnose", "陶瓦网络诊断", RunDiagnoseCommandAsync, token));

    public void QueueCopyDiagnostics() => QueueOperation("copy-diagnostics", CopyDiagnosticsAsync);

    public void QueueExportDiagnostics() => QueueOperation("export-diagnostics", token => RunBackgroundTaskAsync(
        "terracotta.task.export", "导出陶瓦诊断", ExportDiagnosticsAsync, token));

    public string CreateDiagnosticReportJson() => DiagnosticCollector.CreateJson(
        _context.Plugin.Version.ToString(),
        _helperProcess.LastHelperVersion,
        _snapshot,
        _helperProcess.LastResult);

    public async ValueTask<TerracottaRoomSnapshot> CreateAsync(
        TerracottaCreateRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetFaultIfNeeded();
            if (_stateMachine.State is not (TerracottaRoomState.Idle or TerracottaRoomState.WaitingForGame or TerracottaRoomState.WaitingForLan))
                throw new InvalidOperationException("A Terracotta room operation is already active.");

            GameSessionSelection selection = _sessions.Select(
                request.GameSessionId,
                _settings.LastSelectedInstanceId,
                _settings.AutoDetectGameSession);
            if (selection.Selected is null)
            {
                _pendingCreate = request;
                TransitionAndPublish(
                    TerracottaRoomState.WaitingForGame,
                    errorCode: ErrorCodeCatalog.NoRunningGame,
                    errorMessage: selection.Reason);
                return _snapshot;
            }

            int? lanPort = request.LanPort ?? ResolveLanPort(selection.Selected);
            if (lanPort is null)
            {
                _pendingCreate = request with { GameSessionId = selection.Selected.SessionId };
                TransitionAndPublish(
                    TerracottaRoomState.WaitingForLan,
                    gameSessionId: selection.Selected.SessionId,
                    errorCode: ErrorCodeCatalog.LanPortUnavailable,
                    errorMessage: "请在 Minecraft 中打开局域网世界，陶瓦会自动继续。" );
                return _snapshot;
            }

            _pendingCreate = null;
            _stateMachine.TransitionTo(TerracottaRoomState.Creating);
            Publish(new TerracottaRoomSnapshot(
                TerracottaRoomState.Creating,
                TerracottaRoomRole.Host,
                null,
                LanAddressResolver.ToLoopbackAddress(lanPort.Value),
                selection.Selected.SessionId,
                null,
                []));

            TerracottaRoomSnapshot connected = await _helper.CreateAsync(
                selection.Selected.SessionId,
                LanAddressResolver.ToLoopbackAddress(lanPort.Value),
                request.PreferDirectConnection,
                request.AllowRelay,
                cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Connected);
            Publish(connected with { State = TerracottaRoomState.Connected });
            RememberRecovery(
                TerracottaRoomRole.Host,
                connected.RoomCode,
                selection.Selected.SessionId,
                LanAddressResolver.ToLoopbackAddress(lanPort.Value),
                request.PreferDirectConnection,
                request.AllowRelay);
            StartStatusPolling();
            await PersistSelectedSessionAsync(selection.Selected.SessionId, cancellationToken).ConfigureAwait(false);
            if (_settings.AutoCopyRoomCode)
                await CopyRoomCodeAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<TerracottaRoomSnapshot> JoinAsync(
        TerracottaJoinRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RoomCode.TryParse(request.RoomCode, out RoomCode parsedRoomCode))
        {
            PublishFault(ErrorCodeCatalog.InvalidRoomCode, "房间码格式应为 XXXX-XXXX-XXXX。");
            return _snapshot;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetFaultIfNeeded();
            if (_stateMachine.State != TerracottaRoomState.Idle)
                throw new InvalidOperationException("A Terracotta room operation is already active.");
            _stateMachine.TransitionTo(TerracottaRoomState.Joining);
            Publish(new TerracottaRoomSnapshot(
                TerracottaRoomState.Joining,
                TerracottaRoomRole.Member,
                parsedRoomCode.ToString(),
                null,
                request.GameSessionId,
                null,
                []));

            TerracottaRoomSnapshot connected = await _helper.JoinAsync(
                parsedRoomCode.ToString(),
                request.GameSessionId,
                cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Connected);
            Publish(connected with { State = TerracottaRoomState.Connected });
            RememberRecovery(
                TerracottaRoomRole.Member,
                connected.RoomCode ?? parsedRoomCode.ToString(),
                connected.GameSessionId ?? request.GameSessionId,
                connected.LocalAddress,
                preferDirect: true,
                allowRelay: true);
            StartStatusPolling();
            if (connected.GameSessionId is not null)
                await PersistSelectedSessionAsync(connected.GameSessionId, cancellationToken).ConfigureAwait(false);
            if (request.AutoCopyConnectAddress && _settings.AutoCopyConnectAddress)
                await CopyConnectAddressAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask LeaveAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopStatusPolling();
            _recovery = null;
            _pendingCreate = null;
            if (_stateMachine.State == TerracottaRoomState.Idle)
                return;
            if (_stateMachine.State is TerracottaRoomState.WaitingForGame or TerracottaRoomState.WaitingForLan or TerracottaRoomState.Faulted)
            {
                _stateMachine.TransitionTo(TerracottaRoomState.Idle);
                Publish(TerracottaRoomSnapshot.Idle);
                await _helper.StopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _stateMachine.TransitionTo(TerracottaRoomState.Leaving);
            Publish(_snapshot with { State = TerracottaRoomState.Leaving });
            await _helper.LeaveAsync(cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Idle);
            Publish(TerracottaRoomSnapshot.Idle);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<TerracottaRoomSnapshot> RefreshStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
            return _snapshot;

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TerracottaRoomSnapshot helperSnapshot = await _helper.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (helperSnapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
            {
                Publish(helperSnapshot with
                {
                    // Keep plugin-facing lifecycle unless helper reports a hard fault.
                    State = _stateMachine.State == TerracottaRoomState.Diagnosing
                        ? TerracottaRoomState.Diagnosing
                        : helperSnapshot.State
                });
            }
            else if (helperSnapshot.State == TerracottaRoomState.Faulted)
            {
                StopStatusPolling();
                _stateMachine.TransitionTo(TerracottaRoomState.Faulted);
                Publish(helperSnapshot);
            }
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Soft-fail status polling so a transient IPC blip does not drop the room.
            _context.Logger.Warn($"Terracotta status refresh failed: {exception.Message}");
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public TerracottaSessionSelection SelectRunningSession(
        string? explicitSessionId = null,
        string? preferredInstanceId = null,
        bool selectMostRecent = true)
    {
        GameSessionSelection selection = _sessions.Select(
            explicitSessionId,
            preferredInstanceId ?? _settings.LastSelectedInstanceId,
            selectMostRecent);
        return new TerracottaSessionSelection(
            selection.Selected?.SessionId,
            selection.Selected?.InstanceId,
            selection.Selected?.InstanceId,
            selection.Candidates.Count,
            selection.Reason ?? "No selection");
    }

    public async ValueTask<TerracottaNetworkStatus> DiagnoseAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
                throw new InvalidOperationException("Network diagnostics require an active Terracotta room.");

            TerracottaRoomState previous = _stateMachine.State;
            if (previous != TerracottaRoomState.Diagnosing)
            {
                _stateMachine.TransitionTo(TerracottaRoomState.Diagnosing);
                Publish(_snapshot with { State = TerracottaRoomState.Diagnosing });
            }

            TerracottaNetworkStatus network = await _helper.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            if (_stateMachine.State == TerracottaRoomState.Diagnosing && previous != TerracottaRoomState.Diagnosing)
                _stateMachine.TransitionTo(previous == TerracottaRoomState.Reconnecting
                    ? TerracottaRoomState.Reconnecting
                    : TerracottaRoomState.Connected);
            Publish(_snapshot with
            {
                State = _stateMachine.State,
                Network = network
            });
            return network;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<string?> ExportDiagnosticReportAsync(CancellationToken cancellationToken = default)
    {
        if (!_context.Services.TryGet(out IPluginFileService? files) || files is null)
            return null;

        string report = CreateDiagnosticReportJson();
        byte[] content = Encoding.UTF8.GetBytes(report);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string relativePath = $"diagnostics/terracotta-{timestamp}.json";
        await files.WriteAsync(relativePath, content, cancellationToken).ConfigureAwait(false);
        await files.WriteAsync("diagnostics/latest.json", content, cancellationToken).ConfigureAwait(false);
        return relativePath;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        StopStatusPolling();
        _helper.PushEventReceived -= OnHelperPushEvent;
        _helperProcess.HelperProcessExited -= OnHelperProcessExited;
        SnapshotChanged = null;

        IPluginTaskRegistration[] tasks;
        lock (_taskSync)
        {
            tasks = _ownedTasks.ToArray();
            _ownedTasks.Clear();
        }

        foreach (IPluginTaskRegistration task in tasks)
        {
            try
            {
                await task.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _context.Logger.Warn($"Terracotta task cleanup failed: {SensitiveDataRedactor.Redact(exception.Message)}");
            }
        }

        try
        {
            await LeaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _helperProcess.DisposeAsync().ConfigureAwait(false);
            _operationGate.Dispose();
            _shutdown.Dispose();
        }
    }

    private void OnHelperProcessExited(object? sender, HelperProcessExitEventArgs args)
    {
        if (_snapshot.State is TerracottaRoomState.Idle or TerracottaRoomState.Leaving)
            return;

        StopStatusPolling();
        if (_stateMachine.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing)
        {
            try
            {
                if (_stateMachine.State != TerracottaRoomState.Reconnecting)
                    _stateMachine.TransitionTo(TerracottaRoomState.Reconnecting);
            }
            catch (InvalidOperationException)
            {
                // keep current state
            }
        }

        Publish(_snapshot with
        {
            State = TerracottaRoomState.Reconnecting,
            ErrorCode = ErrorCodeCatalog.HelperDisconnected,
            ErrorMessage = args.WillAutoRestart
                ? "陶瓦核心异常退出，正在尝试自动恢复房间。"
                : "陶瓦核心连续异常退出，请手动重启联机。"
        });

        if (args.WillAutoRestart && _recovery is not null)
            QueueOperation("recover-helper-room", RecoverAfterHelperCrashAsync);
        else
            PublishFault(ErrorCodeCatalog.HelperDisconnected, "陶瓦核心不可用，房间已中断。");
    }

    private async Task RecoverAfterHelperCrashAsync(CancellationToken cancellationToken)
    {
        RecoveryIntent? intent = _recovery;
        if (intent is null)
            return;

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _helper.StopAsync(cancellationToken).ConfigureAwait(false);
            if (intent.Role == TerracottaRoomRole.Host)
            {
                if (string.IsNullOrWhiteSpace(intent.GameSessionId) || string.IsNullOrWhiteSpace(intent.LanAddress))
                    throw new InvalidOperationException("Host recovery requires a session and LAN address.");
                TerracottaRoomSnapshot connected = await _helper.CreateAsync(
                    intent.GameSessionId,
                    intent.LanAddress,
                    intent.PreferDirect,
                    intent.AllowRelay,
                    cancellationToken).ConfigureAwait(false);
                if (_stateMachine.State != TerracottaRoomState.Connected)
                    _stateMachine.TransitionTo(TerracottaRoomState.Connected);
                Publish(connected with
                {
                    State = TerracottaRoomState.Connected,
                    ErrorCode = null,
                    ErrorMessage = null
                });
                StartStatusPolling();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(intent.RoomCode))
                    throw new InvalidOperationException("Member recovery requires a room code.");
                TerracottaRoomSnapshot connected = await _helper.JoinAsync(
                    intent.RoomCode,
                    intent.GameSessionId,
                    cancellationToken).ConfigureAwait(false);
                if (_stateMachine.State != TerracottaRoomState.Connected)
                    _stateMachine.TransitionTo(TerracottaRoomState.Connected);
                Publish(connected with
                {
                    State = TerracottaRoomState.Connected,
                    ErrorCode = null,
                    ErrorMessage = null
                });
                StartStatusPolling();
            }

            if (_context.Services.TryGet(out IPluginNotificationService? notifications) && notifications is not null)
                notifications.ShowInformation("陶瓦核心已恢复，房间重新建立。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _recovery = null;
            Fault(exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void RememberRecovery(
        TerracottaRoomRole role,
        string? roomCode,
        string? gameSessionId,
        string? lanAddress,
        bool preferDirect,
        bool allowRelay)
    {
        _recovery = new RecoveryIntent(role, roomCode, gameSessionId, lanAddress, preferDirect, allowRelay);
    }

    private sealed record RecoveryIntent(
        TerracottaRoomRole Role,
        string? RoomCode,
        string? GameSessionId,
        string? LanAddress,
        bool PreferDirect,
        bool AllowRelay);

    private void OnHelperPushEvent(object? sender, HelperPushEvent push)
    {
        try
        {
            switch (push.Type)
            {
                case HelperMessageTypes.RoomStateChanged:
                {
                    TerracottaRoomSnapshot snapshot = push.Envelope.ReadPayload<TerracottaRoomSnapshot>();
                    ApplyHelperSnapshot(snapshot);
                    break;
                }
                case HelperMessageTypes.NetworkUpdated:
                {
                    TerracottaNetworkStatus network = push.Envelope.ReadPayload<TerracottaNetworkStatus>();
                    if (_snapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing)
                    {
                        TerracottaRoomState next = network.IsHealthy
                            ? TerracottaRoomState.Connected
                            : TerracottaRoomState.Reconnecting;
                        if (_stateMachine.State != next &&
                            _stateMachine.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
                        {
                            _stateMachine.TransitionTo(next);
                        }
                        Publish(_snapshot with { State = _stateMachine.State, Network = network });
                    }
                    break;
                }
                case HelperMessageTypes.PeerJoined:
                case HelperMessageTypes.PeerUpdated:
                case HelperMessageTypes.PeerLeft:
                    // Membership changes are reconciled on the next status poll / state-changed push.
                    QueueOperation("status-from-peer-event", token => RefreshStatusAsync(token).AsTask());
                    break;
            }
        }
        catch (Exception exception)
        {
            _context.Logger.Warn($"Ignored malformed Helper push event {push.Type}: {exception.Message}");
        }
    }

    private void ApplyHelperSnapshot(TerracottaRoomSnapshot snapshot)
    {
        if (snapshot.State == TerracottaRoomState.Idle)
        {
            StopStatusPolling();
            _stateMachine.ResetToIdle();
            Publish(TerracottaRoomSnapshot.Idle);
            return;
        }

        if (snapshot.State == TerracottaRoomState.Faulted)
        {
            StopStatusPolling();
            if (_stateMachine.State != TerracottaRoomState.Faulted)
                _stateMachine.TransitionTo(TerracottaRoomState.Faulted);
            Publish(snapshot);
            return;
        }

        if (snapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
        {
            if (_stateMachine.State != snapshot.State &&
                _stateMachine.CanTransitionTo(snapshot.State))
            {
                try
                {
                    _stateMachine.TransitionTo(snapshot.State);
                }
                catch (InvalidOperationException)
                {
                    // Keep local lifecycle if transition is not currently legal.
                }
            }
            Publish(snapshot with { State = _stateMachine.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing
                ? (_stateMachine.State == TerracottaRoomState.Diagnosing ? TerracottaRoomState.Diagnosing : snapshot.State)
                : snapshot.State });
            if (_statusPollCts is null)
                StartStatusPolling();
        }
    }

    private async Task RunConnectionTaskAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (_backgroundTasks is null)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        using IPluginBackgroundTask task = _backgroundTasks.Begin(
            _localizer.Get("terracotta.task.connect", "建立陶瓦联机"));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        try
        {
            task.Report(new PluginBackgroundTaskProgress(
                _localizer.Get("terracotta.task.startHelper", "正在启动陶瓦核心"), Progress: 0.1));
            task.Report(new PluginBackgroundTaskProgress(
                _localizer.Get("terracotta.task.network", "正在建立网络"), Progress: 0.35));
            task.Report(new PluginBackgroundTaskProgress(
                _localizer.Get("terracotta.task.nat", "正在执行 NAT 穿透"), Progress: 0.65));
            await operation(linked.Token).ConfigureAwait(false);
            task.Report(new PluginBackgroundTaskProgress(
                _localizer.Get("terracotta.task.connectHost", "正在连接房主"), Progress: 0.9));
            task.Complete(_localizer.Get("terracotta.task.connected", "连接完成"));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            task.Fail("Canceled", canceled: true);
            throw;
        }
        catch (Exception exception)
        {
            task.Fail(SensitiveDataRedactor.Redact(exception.Message));
            throw;
        }
    }

    private async Task RunBackgroundTaskAsync(
        string titleKey,
        string titleFallback,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (_backgroundTasks is null)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        using IPluginBackgroundTask task = _backgroundTasks.Begin(_localizer.Get(titleKey, titleFallback));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        try
        {
            task.Report(new PluginBackgroundTaskProgress(titleFallback, Progress: 0.1));
            await operation(linked.Token).ConfigureAwait(false);
            task.Complete(_localizer.Get("terracotta.task.completed", "已完成"));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            task.Fail("Canceled", canceled: true);
            throw;
        }
        catch (Exception exception)
        {
            task.Fail(SensitiveDataRedactor.Redact(exception.Message));
            throw;
        }
    }

    private void TrackCommand(string id, string title, Func<CancellationToken, Task> executeAsync) =>
        _context.Lifetime.Track(_commands.Register(new PluginCommandDescriptor(id, title, executeAsync)));

    private void QueueOperation(string name, Func<CancellationToken, Task> operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        long sequence = Interlocked.Increment(ref _operationSequence);
        TrackOwnedTask(_tasks.Run($"{PluginIds.Plugin}.{name}.{sequence}", async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            await operation(linked.Token).ConfigureAwait(false);
        }));
    }

    private void TrackOwnedTask(IPluginTaskRegistration task)
    {
        lock (_taskSync)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ownedTasks.Add(task);
                _context.Lifetime.Track(task);
                return;
            }
        }

        task.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        _settings = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSelectedSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _settings = _settings with { LastSelectedInstanceId = sessionId };
        await _stateStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    private int? ResolveLanPort(PluginGameSessionSnapshot session)
    {
        if (LanAddressResolver.TryResolvePort(session.LanAddress, out int snapshotPort))
            return snapshotPort;

        if (_output is null)
            return null;

        foreach (PluginGameProcessOutput line in _output.Read(session.SessionId, 0, 2048).Reverse())
        {
            if (LanAddressResolver.TryResolvePort(line.Text, out int outputPort))
                return outputPort;
        }

        return null;
    }

    private void OnGameOutput(PluginGameProcessOutput output)
    {
        if (_stateMachine.State != TerracottaRoomState.WaitingForLan ||
            _pendingCreate?.GameSessionId is not { } sessionId ||
            !string.Equals(sessionId, output.SessionId, StringComparison.Ordinal) ||
            !LanAddressResolver.TryResolvePort(output.Text, out int port))
        {
            return;
        }

        TerracottaCreateRoomRequest pending = _pendingCreate with { LanPort = port };
        QueueOperation("resume-create-room", token => CreateAsync(pending, token).AsTask());
    }

    private void OnLaunchEvent(PluginLaunchEvent launchEvent)
    {
        if (launchEvent.Session.State == PluginGameSessionState.Running &&
            _stateMachine.State == TerracottaRoomState.WaitingForGame &&
            _pendingCreate is not null)
        {
            TerracottaCreateRoomRequest pending = _pendingCreate with { GameSessionId = launchEvent.SessionId };
            QueueOperation("resume-create-room", token => CreateAsync(pending, token).AsTask());
            return;
        }

        if (_settings.AutoCloseOnGameExit &&
            _snapshot.GameSessionId is { } activeSession &&
            string.Equals(activeSession, launchEvent.SessionId, StringComparison.Ordinal) &&
            launchEvent.Session.State is PluginGameSessionState.Exited or PluginGameSessionState.Crashed or PluginGameSessionState.Terminated)
        {
            QueueLeave();
        }
    }

    private void ResetFaultIfNeeded()
    {
        if (_stateMachine.State == TerracottaRoomState.Faulted)
            _stateMachine.TransitionTo(TerracottaRoomState.Idle);
    }

    private void TransitionAndPublish(
        TerracottaRoomState state,
        string? gameSessionId = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _stateMachine.TransitionTo(state);
        Publish(new TerracottaRoomSnapshot(
            state,
            TerracottaRoomRole.None,
            null,
            null,
            gameSessionId,
            null,
            [],
            errorCode,
            errorMessage));
    }

    private void Fault(Exception exception)
    {
        string code = exception switch
        {
            SecureIdentityException => ErrorCodeCatalog.SecureStorageUnavailable,
            FileNotFoundException => ErrorCodeCatalog.HelperMissing,
            HelperProtocolException protocol => MapHelperErrorCode(protocol.Code),
            _ => ErrorCodeCatalog.NetworkUnavailable
        };
        PublishFault(code, SensitiveDataRedactor.Redact(exception.Message));
        _context.Logger.LogError($"Terracotta operation failed ({code}).", exception);
    }

    private static string MapHelperErrorCode(string? helperCode) => helperCode switch
    {
        "network.easytier-missing" => ErrorCodeCatalog.EasyTierMissing,
        "network.easytier-start-failed" or "network.easytier-stop-failed" => ErrorCodeCatalog.EasyTierStartFailed,
        "network.peer-unreachable" => ErrorCodeCatalog.PeerUnreachable,
        "network.mesh-ingress-failed" => ErrorCodeCatalog.MeshIngressFailed,
        "room.invalid-code" => ErrorCodeCatalog.InvalidRoomCode,
        "identity.not-initialized" or "identity.invalid-key" => ErrorCodeCatalog.SecureStorageUnavailable,
        null or "" => ErrorCodeCatalog.HelperDisconnected,
        _ when helperCode.StartsWith("network.", StringComparison.Ordinal) => ErrorCodeCatalog.NetworkUnavailable,
        _ when helperCode.StartsWith("ipc.", StringComparison.Ordinal) => ErrorCodeCatalog.HelperDisconnected,
        _ => ErrorCodeCatalog.NetworkUnavailable
    };

    private void PublishFault(string code, string message)
    {
        if (_stateMachine.State != TerracottaRoomState.Faulted)
            _stateMachine.TransitionTo(TerracottaRoomState.Faulted);
        Publish(_snapshot with
        {
            State = TerracottaRoomState.Faulted,
            ErrorCode = code,
            ErrorMessage = message
        });
        if (_context.Services.TryGet<IPluginNotificationService>(out IPluginNotificationService? notifications) && notifications is not null)
            notifications.ShowWarning($"陶瓦联机：{message} ({code})");
    }

    private void Publish(TerracottaRoomSnapshot snapshot)
    {
        _snapshot = snapshot;
        _context.Dispatcher.Post(() => SnapshotChanged?.Invoke(this, snapshot));
    }

    private async Task CopyRoomCodeAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.RoomCode is null)
            return;
        if (_context.Services.TryGet<IPluginClipboardService>(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(_snapshot.RoomCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyConnectAddressAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.LocalAddress is null)
            return;
        if (_context.Services.TryGet<IPluginClipboardService>(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(_snapshot.LocalAddress, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_windows is not null)
        {
            await _windows.ShowAsync(PluginIds.DiagnosticsWindow, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_context.Services.TryGet<IPluginNotificationService>(out IPluginNotificationService? notifications) && notifications is not null)
            notifications.ShowWarning("当前宿主未提供插件窗口服务，可使用“导出陶瓦诊断”命令生成报告。");
    }

    private async Task RunDiagnoseCommandAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
        {
            if (_context.Services.TryGet(out IPluginNotificationService? unavailable) && unavailable is not null)
                unavailable.ShowInformation("进入陶瓦房间后才能运行网络诊断。");
            return;
        }

        try
        {
            TerracottaNetworkStatus network = await DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            if (_context.Services.TryGet(out IPluginNotificationService? completed) && completed is not null)
                completed.ShowInformation(network.IsHealthy ? "陶瓦网络状态正常。" : "陶瓦网络存在异常，请导出诊断报告。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fault already applied by the public DiagnoseAsync implementation.
            if (_snapshot.State != TerracottaRoomState.Faulted)
                Fault(exception);
        }
    }

    private async Task CopyDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_context.Services.TryGet(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(CreateDiagnosticReportJson(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        string? path = await ExportDiagnosticReportAsync(cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            if (_context.Services.TryGet(out IPluginNotificationService? unavailable) && unavailable is not null)
                unavailable.ShowWarning("当前宿主未提供插件文件服务，无法保存诊断报告。");
            return;
        }

        if (_context.Services.TryGet(out IPluginNotificationService? completed) && completed is not null)
            completed.ShowInformation($"陶瓦诊断已保存到插件数据目录：{path}");
    }

    private void StartStatusPolling()
    {
        StopStatusPolling();
        CancellationTokenSource cts = new();
        _statusPollCts = cts;
        TrackOwnedTask(_tasks.Run($"{PluginIds.Plugin}.status-poll.{Interlocked.Increment(ref _operationSequence)}", async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(StatusPollInterval, linked.Token).ConfigureAwait(false);
                    if (_snapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
                        await RefreshStatusAsync(linked.Token).ConfigureAwait(false);
                    else
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }));
    }

    private void StopStatusPolling()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _statusPollCts, null);
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RestartHelperAsync(CancellationToken cancellationToken)
    {
        await _helper.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_stateMachine.State != TerracottaRoomState.Idle)
            PublishFault(ErrorCodeCatalog.HelperDisconnected, "陶瓦核心已重启，请重新进入房间。");
    }

    private async Task OpenHelpAsync(CancellationToken cancellationToken)
    {
        if (_context.Services.TryGet<IPluginUriLauncher>(out IPluginUriLauncher? launcher) && launcher is not null)
            await launcher.OpenAsync(new Uri("https://docs.pcln.top/plugins/terracotta/"), cancellationToken).ConfigureAwait(false);
    }
}
