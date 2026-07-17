using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Services;

namespace Cn.Pcln.Terracotta.Views;

public sealed class TerracottaDiagnosticsWindow : Window, IDisposable
{
    private readonly TerracottaController _controller;
    private readonly TextBox _report;
    private int _detached;

    public TerracottaDiagnosticsWindow(TerracottaController controller, TerracottaLocalizer localizer)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        ArgumentNullException.ThrowIfNull(localizer);
        Title = localizer.Get("terracotta.diagnostics.title", "陶瓦联机诊断");
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        CanResize = true;

        _report = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _report,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            _report,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        Button diagnose = new() { Content = localizer.Get("terracotta.diagnostics.run", "运行网络诊断") };
        diagnose.Click += (_, _) => _controller.QueueDiagnose();
        Button refresh = new() { Content = localizer.Get("terracotta.diagnostics.refresh", "刷新") };
        refresh.Click += (_, _) => Render();
        Button copy = new() { Content = localizer.Get("terracotta.diagnostics.copy", "复制报告") };
        copy.Click += (_, _) => _controller.QueueCopyDiagnostics();
        Button export = new() { Content = localizer.Get("terracotta.diagnostics.save", "保存报告") };
        export.Click += (_, _) => _controller.QueueExportDiagnostics();

        Grid layout = new()
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        TextBlock heading = new()
        {
            Text = localizer.Get("terracotta.diagnostics.notice", "诊断报告不会自动上传，Token、密钥和认证信息会在生成前脱敏。"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        Grid.SetRow(heading, 0);
        Grid.SetRow(_report, 1);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { diagnose, refresh, copy, export }
        };
        Grid.SetRow(actions, 2);
        layout.Children.Add(heading);
        layout.Children.Add(_report);
        layout.Children.Add(actions);
        Content = layout;

        _controller.SnapshotChanged += OnSnapshotChanged;
        Closed += OnClosed;
        Render();
    }

    private void OnSnapshotChanged(object? sender, TerracottaRoomSnapshot snapshot) => Render();

    private void OnClosed(object? sender, EventArgs eventArgs) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _detached, 1) != 0)
            return;
        _controller.SnapshotChanged -= OnSnapshotChanged;
        Closed -= OnClosed;
    }

    private void Render() => _report.Text = _controller.CreateDiagnosticReportJson();
}
