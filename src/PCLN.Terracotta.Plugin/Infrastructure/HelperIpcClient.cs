using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cn.Pcln.Terracotta.Contracts;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperIpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HelperIpcClient(Stream stream)
    {
        _stream = stream;
    }

    public string? HelperVersion { get; private set; }

    public IReadOnlyList<string> Capabilities { get; private set; } = [];

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomState>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomRole>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaConnectionMode>(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static async ValueTask<HelperIpcClient> ConnectAsync(
        string endpoint,
        string authenticationToken,
        string pluginVersion,
        CancellationToken cancellationToken = default)
    {
        Stream stream = await ConnectStreamAsync(endpoint, cancellationToken).ConfigureAwait(false);
        HelperIpcClient client = new(stream);
        try
        {
            HelperHelloResponse response = await client.SendAsync<HelperHelloRequest, HelperHelloResponse>(
                HelperMessageTypes.Hello,
                new HelperHelloRequest(authenticationToken, "pcln", pluginVersion),
                HelperMessageTypes.HelloAccepted,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.HelperVersion))
                throw new HelperProtocolException("Helper handshake omitted its version.");
            client.HelperVersion = response.HelperVersion;
            client.Capabilities = response.Capabilities.ToArray();
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        string messageType,
        TRequest request,
        string expectedResponseType,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IpcEnvelope outbound = IpcEnvelope.Create(messageType, request, options: JsonOptions);
            await IpcFraming.WriteAsync(_stream, outbound, JsonOptions, cancellationToken).ConfigureAwait(false);
            IpcEnvelope inbound = await IpcFraming.ReadAsync(_stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (inbound.Protocol != ProtocolVersion.Current)
                throw new HelperProtocolException($"Unsupported Helper protocol version: {inbound.Protocol}.");
            if (!string.Equals(inbound.Id, outbound.Id, StringComparison.Ordinal))
                throw new HelperProtocolException("Helper response request ID did not match.");
            if (string.Equals(inbound.Type, HelperMessageTypes.Error, StringComparison.Ordinal))
            {
                HelperError error = inbound.ReadPayload<HelperError>(JsonOptions);
                throw new HelperProtocolException(error.Code, error.Message);
            }
            if (!string.Equals(inbound.Type, expectedResponseType, StringComparison.Ordinal))
                throw new HelperProtocolException($"Unexpected Helper response type: {inbound.Type}.");

            return inbound.ReadPayload<TResponse>(JsonOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IpcEnvelope> SendAsync(
        string messageType,
        object request,
        string expectedResponseType,
        CancellationToken cancellationToken = default) =>
        SendEnvelopeAsync(messageType, request, expectedResponseType, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static async ValueTask<Stream> ConnectStreamAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\";
            if (!endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Windows IPC endpoint must be a local named pipe.", nameof(endpoint));
            string name = endpoint[prefix.Length..];
            NamedPipeClientStream pipe = new(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async ValueTask<IpcEnvelope> SendEnvelopeAsync(
        string messageType,
        object request,
        string expectedResponseType,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IpcEnvelope outbound = IpcEnvelope.Create(messageType, request, options: JsonOptions);
            await IpcFraming.WriteAsync(_stream, outbound, JsonOptions, cancellationToken).ConfigureAwait(false);
            IpcEnvelope inbound = await IpcFraming.ReadAsync(_stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (inbound.Protocol != ProtocolVersion.Current ||
                !string.Equals(inbound.Id, outbound.Id, StringComparison.Ordinal) ||
                !string.Equals(inbound.Type, expectedResponseType, StringComparison.Ordinal))
            {
                throw new HelperProtocolException("Helper response envelope did not match the request.");
            }

            return inbound;
        }
        finally
        {
            _gate.Release();
        }
    }
}
