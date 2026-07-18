namespace Cn.Pcln.Terracotta;

public static class PluginIds
{
    public const string Plugin = "cn.pcln.terracotta";
    public const string PageRegistration = Plugin + ".page-registration";
    public const string PageRoute = Plugin + ".page";
    public const string DiagnosticsWindowRegistration = Plugin + ".diagnostics-window-registration";
    public const string DiagnosticsWindow = Plugin + ".diagnostics-window";

    public const string CreateRoomCommand = Plugin + ".create-room";
    public const string JoinRoomCommand = Plugin + ".join-room";
    public const string LeaveRoomCommand = Plugin + ".leave-room";
    public const string CopyRoomCodeCommand = Plugin + ".copy-room-code";
    public const string CopyConnectAddressCommand = Plugin + ".copy-connect-address";
    public const string OpenDiagnosticsCommand = Plugin + ".open-diagnostics";
    public const string ExportDiagnosticsCommand = Plugin + ".export-diagnostics";
    public const string RestartHelperCommand = Plugin + ".restart-helper";
    public const string OpenHelpCommand = Plugin + ".open-help";

    public const string ExportRoomService = "room-service";
    public const string ExportSessionService = "session-service";
    public const string ExportNetworkStatus = "network-status";
    public const string ExportDiagnostics = "diagnostics";
}
