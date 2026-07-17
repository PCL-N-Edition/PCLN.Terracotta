using System.Runtime.InteropServices;
using System.Text.Json;
using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Diagnostics;
using Cn.Pcln.Terracotta.Infrastructure;
using Cn.Pcln.Terracotta.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Plugin.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public void ManifestDeclaresRuntimeLaunchContribution()
    {
        string manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PCLN.Terracotta.Plugin", "plugin.json"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement target = document.RootElement.GetProperty("ui").GetProperty("targets")
            .EnumerateArray()
            .Single(item => item.GetProperty("target").GetString() == "pcl.page.launch");
        JsonElement operation = target.GetProperty("operations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == PluginIds.LaunchContribution);

        CollectionAssert.Contains(
            target.GetProperty("access").EnumerateArray().Select(item => item.GetString()).ToArray(),
            "inject");
        Assert.AreEqual("inject", operation.GetProperty("kind").GetString());
        Assert.AreEqual("primary-actions.after", operation.GetProperty("slot").GetString());
        Assert.IsFalse(operation.GetProperty("required").GetBoolean());
        Assert.AreEqual("disable-feature", operation.GetProperty("fallback").GetString());
    }

    [TestMethod]
    public void RoomStateMachineAcceptsDocumentedHappyPath()
    {
        RoomStateMachine machine = new();

        machine.TransitionTo(TerracottaRoomState.WaitingForLan);
        machine.TransitionTo(TerracottaRoomState.Creating);
        machine.TransitionTo(TerracottaRoomState.Connected);
        machine.TransitionTo(TerracottaRoomState.Diagnosing);
        machine.TransitionTo(TerracottaRoomState.Connected);
        machine.TransitionTo(TerracottaRoomState.Leaving);
        machine.TransitionTo(TerracottaRoomState.Idle);

        Assert.AreEqual(TerracottaRoomState.Idle, machine.State);
    }

    [TestMethod]
    public void RoomStateMachineAllowsReconnectAndRecoveryPath()
    {
        RoomStateMachine machine = new();
        machine.TransitionTo(TerracottaRoomState.Creating);
        machine.TransitionTo(TerracottaRoomState.Connected);
        machine.TransitionTo(TerracottaRoomState.Reconnecting);
        machine.TransitionTo(TerracottaRoomState.Connected);
        machine.TransitionTo(TerracottaRoomState.Reconnecting);
        machine.TransitionTo(TerracottaRoomState.Leaving);
        machine.TransitionTo(TerracottaRoomState.Idle);
        Assert.AreEqual(TerracottaRoomState.Idle, machine.State);
    }

    [TestMethod]
    public void RoomStateMachineResetToIdleFromConnected()
    {
        RoomStateMachine machine = new();
        machine.TransitionTo(TerracottaRoomState.Joining);
        machine.TransitionTo(TerracottaRoomState.Connected);
        machine.ResetToIdle();
        Assert.AreEqual(TerracottaRoomState.Idle, machine.State);
    }

    [TestMethod]
    public void ExportNamesMatchStablePluginContractIds()
    {
        // Compare through variables so analyzers do not fold const-to-const assertions.
        string[] expected = ["room-service", "session-service", "network-status", "diagnostics"];
        string[] actual =
        [
            TerracottaExportNames.RoomService,
            TerracottaExportNames.SessionService,
            TerracottaExportNames.NetworkStatus,
            TerracottaExportNames.Diagnostics
        ];
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(PluginIds.ExportRoomService, actual[0]);
        Assert.AreEqual(PluginIds.ExportSessionService, actual[1]);
        Assert.AreEqual(PluginIds.ExportNetworkStatus, actual[2]);
        Assert.AreEqual(PluginIds.ExportDiagnostics, actual[3]);
    }

    [TestMethod]
    public void RoomStateMachineRejectsInvalidTransition()
    {
        RoomStateMachine machine = new();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            machine.TransitionTo(TerracottaRoomState.Connected));
    }

    [TestMethod]
    [DataRow("Local game hosted on port 12345", 12345)]
    [DataRow("本地游戏已在端口 25565 上开放", 25565)]
    [DataRow("Started serving on 54321", 54321)]
    [DataRow("127.0.0.1:19132", 19132)]
    [DataRow("[::1]:24454", 24454)]
    public void LanAddressResolverAcceptsTrustedLoopbackInputs(string value, int expected)
    {
        Assert.IsTrue(LanAddressResolver.TryResolvePort(value, out int port));
        Assert.AreEqual(expected, port);
    }

    [TestMethod]
    [DataRow("192.168.1.2:25565")]
    [DataRow("example.com:25565")]
    [DataRow("Local game hosted on port 70000")]
    public void LanAddressResolverRejectsUntrustedOrInvalidInputs(string value) =>
        Assert.IsFalse(LanAddressResolver.TryResolvePort(value, out _));

    [TestMethod]
    public void SensitiveDataRedactorRemovesCredentials()
    {
        string source = "Bearer secret.value auth-token=abc123 private-key:xyz";

        string redacted = SensitiveDataRedactor.Redact(source);

        Assert.IsFalse(redacted.Contains("secret.value", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("xyz", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DiagnosticCollectorExcludesRoomCodeAndRedactsHelperOutput()
    {
        TerracottaRoomSnapshot snapshot = new(
            TerracottaRoomState.Faulted,
            TerracottaRoomRole.Host,
            "AB12-CD34-EF56",
            "127.0.0.1:25565",
            "session-1",
            null,
            [],
            "TC-NET-001",
            "Bearer secret.value");
        PluginProcessResult process = new(1, "token=abc123", "private-key:xyz");

        string json = DiagnosticCollector.CreateJson(
            "0.1.0",
            "0.1.0",
            snapshot,
            process,
            DateTimeOffset.UnixEpoch);

        Assert.IsFalse(json.Contains("AB12-CD34-EF56", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("secret.value", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("xyz", StringComparison.Ordinal));
        StringAssert.Contains(json, "[REDACTED]");
        StringAssert.Contains(json, "\"memberCount\": 0");
    }

    [TestMethod]
    public async Task SecureIdentityStoreUsesStableSessionIdentityWithoutHostStorage()
    {
        SecureIdentityStore store = new(null);

        byte[] created = await store.GetOrCreateAsync();
        byte[] reused = await store.GetOrCreateAsync();

        Assert.IsTrue(store.UsesTemporaryIdentity);
        Assert.HasCount(32, created);
        CollectionAssert.AreEqual(created, reused);
    }

    [TestMethod]
    public async Task SecureIdentityStoreCreatesAndReusesThirtyTwoByteIdentity()
    {
        TestSecureStorage storage = new();
        SecureIdentityStore store = new(storage);

        byte[] created = await store.GetOrCreateAsync();
        byte[] loaded = await store.GetOrCreateAsync();

        Assert.HasCount(32, created);
        CollectionAssert.AreEqual(created, loaded);
        Assert.AreEqual(1, storage.WriteCount);
    }

    [TestMethod]
    public async Task SecureIdentityStoreRejectsCorruptIdentity()
    {
        TestSecureStorage storage = new([1, 2, 3]);
        SecureIdentityStore store = new(storage);

        await Assert.ThrowsExactlyAsync<SecureIdentityException>(async () =>
            await store.GetOrCreateAsync());
        Assert.AreEqual(0, storage.WriteCount);
    }

    [TestMethod]
    public void RuntimePlatformResolverMapsSupportedTargets()
    {
        Assert.AreEqual("win-x64", RuntimePlatformResolver.Resolve(true, false, false, Architecture.X64));
        Assert.AreEqual("linux-arm64", RuntimePlatformResolver.Resolve(false, true, false, Architecture.Arm64));
        Assert.AreEqual("osx-arm64", RuntimePlatformResolver.Resolve(false, false, true, Architecture.Arm64));
    }

    private sealed class TestSecureStorage(byte[]? initialValue = null) : IPluginSecureStorage
    {
        private byte[]? _value = initialValue;

        public PluginServiceId Id => PluginServiceIds.SecureStorage;

        public PluginApiVersion Version => PluginApiVersion.Parse("0.1");

        public int WriteCount { get; private set; }

        public ValueTask<PluginSecretReadResult> ReadAsync(
            PluginSecretKey key,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_value is null
                ? new PluginSecretReadResult(PluginSecureStorageStatus.NotFound)
                : new PluginSecretReadResult(PluginSecureStorageStatus.Success, _value.ToArray()));

        public ValueTask<PluginSecretOperationResult> WriteAsync(
            PluginSecretKey key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            _value = value.ToArray();
            WriteCount++;
            return ValueTask.FromResult(new PluginSecretOperationResult(PluginSecureStorageStatus.Success));
        }

        public ValueTask<PluginSecretOperationResult> DeleteAsync(
            PluginSecretKey key,
            CancellationToken cancellationToken = default)
        {
            _value = null;
            return ValueTask.FromResult(new PluginSecretOperationResult(PluginSecureStorageStatus.Success));
        }
    }
}
