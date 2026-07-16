using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cn.Pcln.Terracotta.Contracts;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Diagnostics;

public static class DiagnosticCollector
{
    private const int MaximumProcessOutputLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string CreateJson(
        string pluginVersion,
        string? helperVersion,
        TerracottaRoomSnapshot snapshot,
        PluginProcessResult? helperResult,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginVersion);
        ArgumentNullException.ThrowIfNull(snapshot);

        TerracottaDiagnosticReport report = new(
            timestamp ?? DateTimeOffset.UtcNow,
            pluginVersion,
            helperVersion,
            ProtocolVersion.Current,
            "legacy-v1-compatible",
            "2.6.x-sidecar",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            snapshot.State,
            snapshot.Role,
            snapshot.GameSessionId,
            snapshot.LocalAddress,
            snapshot.Members.Count,
            snapshot.Network,
            snapshot.ErrorCode,
            SensitiveDataRedactor.Redact(snapshot.ErrorMessage),
            helperResult is null
                ? null
                : new HelperProcessDiagnostic(
                    helperResult.ExitCode,
                    LimitAndRedact(helperResult.StandardOutput),
                    LimitAndRedact(helperResult.StandardError)));

        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomState>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomRole>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaConnectionMode>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string? LimitAndRedact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string redacted = SensitiveDataRedactor.Redact(value);
        return redacted.Length <= MaximumProcessOutputLength
            ? redacted
            : redacted[^MaximumProcessOutputLength..];
    }

    private sealed record TerracottaDiagnosticReport(
        DateTimeOffset Timestamp,
        string PluginVersion,
        string? HelperVersion,
        int IpcProtocolVersion,
        string ScaffoldingProtocolVersion,
        string EasyTierVersion,
        string OperatingSystem,
        string Architecture,
        TerracottaRoomState RoomState,
        TerracottaRoomRole RoomRole,
        string? GameSessionId,
        string? LanAddress,
        int MemberCount,
        TerracottaNetworkStatus? Network,
        string? ErrorCode,
        string? ErrorMessage,
        HelperProcessDiagnostic? HelperProcess);

    private sealed record HelperProcessDiagnostic(int ExitCode, string? StandardOutput, string? StandardError);
}
