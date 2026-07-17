using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Services;

namespace Cn.Pcln.Terracotta.Views;

public sealed class TerracottaPage : UserControl, IDisposable
{
    private readonly TerracottaController _controller;
    private readonly TerracottaLocalizer _localizer;
    private readonly TextBlock _stateText;
    private readonly TextBlock _detailText;
    private readonly TextBox _roomCodeInput;
    private readonly StackPanel _idleActions;
    private readonly StackPanel _roomActions;
    private readonly TextBlock _roomCodeText;
    private readonly TextBlock _addressText;
    private int _detached;

    public TerracottaPage(TerracottaController controller, TerracottaLocalizer localizer)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _stateText = CreateText(_localizer.Get("terracotta.state.idle", "未连接"), 18, FontWeight.SemiBold);
        _detailText = CreateText(_localizer.Get("terracotta.description.idle", "创建房间，或输入 12 位房间码加入。"), 13);
        _roomCodeInput = new TextBox
        {
            PlaceholderText = "XXXX-XXXX-XXXX",
            MaxLength = 14,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _roomCodeText = CreateText("—", 22, FontWeight.Bold);
        _addressText = CreateText("—", 14);
        _idleActions = BuildIdleActions();
        _roomActions = BuildRoomActions();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(32),
                Spacing = 20,
                Children =
                {
                    CreateText(_localizer.Get("terracotta.title", "陶瓦联机"), 30, FontWeight.Bold),
                    CreateText(_localizer.Get("terracotta.subtitle", "安全、跨平台的 Minecraft P2P 联机"), 14),
                    BuildStatusCard(),
                    _idleActions,
                    _roomActions
                }
            }
        };

        _controller.SnapshotChanged += OnSnapshotChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Render(_controller.CurrentRoom);
    }

    private Border BuildStatusCard() => new()
    {
        Padding = new Thickness(20),
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromArgb(26, 127, 127, 127)),
        Child = new StackPanel
        {
            Spacing = 6,
            Children = { _stateText, _detailText }
        }
    };

    private StackPanel BuildIdleActions()
    {
        Button create = new() { Content = _localizer.Get("terracotta.createRoom", "创建房间"), MinWidth = 120 };
        create.Click += (_, _) => _controller.QueueCreate();
        Button join = new() { Content = _localizer.Get("terracotta.joinRoom", "加入房间"), MinWidth = 120 };
        join.Click += (_, _) => _controller.QueueJoin(_roomCodeInput.Text ?? string.Empty);

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateText(_localizer.Get("terracotta.joinExisting", "加入已有房间"), 17, FontWeight.SemiBold),
                _roomCodeInput,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { create, join }
                },
                CreateText(_localizer.Get("terracotta.lanHint", "创建房间前，请先启动 Minecraft 并在单人世界中打开局域网。"), 12)
            }
        };
    }

    private StackPanel BuildRoomActions()
    {
        Button copyCode = new() { Content = _localizer.Get("terracotta.copyRoomCode", "复制房间码") };
        copyCode.Click += (_, _) => _controller.QueueCopyRoomCode();
        Button leave = new() { Content = _localizer.Get("terracotta.leaveRoom", "退出房间") };
        leave.Click += (_, _) => _controller.QueueLeave();

        return new StackPanel
        {
            IsVisible = false,
            Spacing = 12,
            Children =
            {
                CreateText(_localizer.Get("terracotta.currentRoom", "当前房间"), 17, FontWeight.SemiBold),
                _roomCodeText,
                _addressText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { copyCode, leave }
                }
            }
        };
    }

    private void OnSnapshotChanged(object? sender, TerracottaRoomSnapshot snapshot) => Render(snapshot);

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0)
            return;
        _controller.SnapshotChanged -= OnSnapshotChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
    }

    private void Render(TerracottaRoomSnapshot snapshot)
    {
        _stateText.Text = StateLabel(snapshot.State);
        _detailText.Text = snapshot.ErrorMessage ?? StateDescription(snapshot.State);
        bool hasRoom = snapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Leaving;
        _idleActions.IsVisible = !hasRoom;
        _roomActions.IsVisible = hasRoom;
        _roomCodeText.Text = snapshot.RoomCode ?? _localizer.Get("terracotta.roomCodeLoading", "正在获取房间码…");
        _addressText.Text = _localizer.Format("terracotta.address", "联机地址：{0}", snapshot.LocalAddress ?? "—");
    }

    // Avalonia rejects FontWeight 0 ("Font weight must be > 0"). Never use default(FontWeight).
    private static TextBlock CreateText(string text, double size, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap
    };

    private string StateLabel(TerracottaRoomState state) => state switch
    {
        TerracottaRoomState.Idle => _localizer.Get("terracotta.state.idle", "未连接"),
        TerracottaRoomState.WaitingForGame => _localizer.Get("terracotta.state.waitingGame", "等待 Minecraft 启动"),
        TerracottaRoomState.WaitingForLan => _localizer.Get("terracotta.state.waitingLan", "等待打开局域网"),
        TerracottaRoomState.Creating => _localizer.Get("terracotta.state.creating", "正在创建房间"),
        TerracottaRoomState.Joining => _localizer.Get("terracotta.state.joining", "正在加入房间"),
        TerracottaRoomState.Connected => _localizer.Get("terracotta.state.connected", "已连接"),
        TerracottaRoomState.Reconnecting => _localizer.Get("terracotta.state.reconnecting", "正在重连"),
        TerracottaRoomState.Leaving => _localizer.Get("terracotta.state.leaving", "正在退出"),
        TerracottaRoomState.Faulted => _localizer.Get("terracotta.state.faulted", "连接失败"),
        TerracottaRoomState.Diagnosing => _localizer.Get("terracotta.state.diagnosing", "正在诊断"),
        _ => state.ToString()
    };

    private string StateDescription(TerracottaRoomState state) => state switch
    {
        TerracottaRoomState.Idle => _localizer.Get("terracotta.description.idle", "创建房间，或输入 12 位房间码加入。"),
        TerracottaRoomState.WaitingForGame => _localizer.Get("terracotta.description.waitingGame", "启动 Minecraft 后会自动继续。"),
        TerracottaRoomState.WaitingForLan => _localizer.Get("terracotta.description.waitingLan", "在暂停菜单中选择“对局域网开放”。"),
        TerracottaRoomState.Connected => _localizer.Get("terracotta.description.connected", "陶瓦网络已建立，可以邀请好友加入。"),
        TerracottaRoomState.Reconnecting => _localizer.Get("terracotta.description.reconnecting", "网络暂时中断，正在尝试恢复。"),
        _ => _localizer.Get("terracotta.description.wait", "请稍候…")
    };
}
