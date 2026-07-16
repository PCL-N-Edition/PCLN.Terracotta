use std::collections::HashSet;

use serde::{Deserialize, Serialize};
use subtle::ConstantTimeEq;
use tokio::io::{AsyncRead, AsyncWrite};
use zeroize::{Zeroize, Zeroizing};

use crate::{
    bootstrap::SecretToken,
    protocol::{
        Envelope, ErrorResponse, HelloAccepted, HelloRequest, PROTOCOL_VERSION,
        framing::{FrameError, read_frame, write_frame},
    },
    room::{CreateRoomRequest, JoinRoomRequest, RoomError, RoomService, SetLanAddressRequest},
};

const MAX_REQUEST_IDS: usize = 4096;
const CAPABILITIES: &[&str] = &[
    "identity.initialize",
    "room.create",
    "room.join",
    "room.leave",
    "room.status",
    "room.set-lan-address",
    "network.diagnose",
    "shutdown",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionOutcome {
    ShutdownRequested,
    PeerDisconnected,
}

pub async fn run<S>(
    stream: &mut S,
    secret: &SecretToken,
    room: &RoomService,
) -> Result<SessionOutcome, FrameError>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    let first = tokio::time::timeout(std::time::Duration::from_secs(5), read_frame(stream))
        .await
        .map_err(|_| {
            FrameError::Io(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "IPC handshake timed out",
            ))
        })??;
    let first_id = first.id.clone();
    if !valid_request_id(&first.id)
        || first.protocol != PROTOCOL_VERSION
        || first.message_type != "hello"
    {
        send_error(
            stream,
            first_id,
            "ipc.invalid-handshake",
            "The first IPC message must be a valid hello request.",
            false,
        )
        .await?;
        return Ok(SessionOutcome::PeerDisconnected);
    }

    let mut hello: HelloRequest = match serde_json::from_value(first.payload) {
        Ok(value) => value,
        Err(_) => {
            send_error(
                stream,
                first_id,
                "ipc.invalid-handshake",
                "The hello payload is invalid.",
                false,
            )
            .await?;
            return Ok(SessionOutcome::PeerDisconnected);
        }
    };
    let authenticated = hello
        .auth_token
        .as_bytes()
        .ct_eq(secret.as_bytes())
        .unwrap_u8()
        == 1;
    hello.auth_token.zeroize();
    if !authenticated || hello.client != "pcln" || hello.client_version.trim().is_empty() {
        send_error(
            stream,
            first_id,
            "ipc.authentication-failed",
            "IPC authentication failed.",
            false,
        )
        .await?;
        return Ok(SessionOutcome::PeerDisconnected);
    }

    write_frame(
        stream,
        &Envelope::response(
            first.id.clone(),
            "hello.accepted",
            HelloAccepted {
                helper_version: env!("CARGO_PKG_VERSION"),
                capabilities: CAPABILITIES,
            },
        )?,
    )
    .await?;

    let mut request_ids = HashSet::from([first.id]);
    let mut identity = None;
    loop {
        let request = match read_frame(stream).await {
            Ok(value) => value,
            Err(FrameError::EndOfStream) => return Ok(SessionOutcome::PeerDisconnected),
            Err(error) => return Err(error),
        };
        if request.protocol != PROTOCOL_VERSION {
            send_error(
                stream,
                request.id,
                "ipc.protocol-mismatch",
                "The IPC protocol version is not supported.",
                false,
            )
            .await?;
            continue;
        }
        if !valid_request_id(&request.id) {
            send_error(
                stream,
                request.id,
                "ipc.invalid-request-id",
                "The request ID is invalid.",
                false,
            )
            .await?;
            continue;
        }
        if request_ids.len() >= MAX_REQUEST_IDS {
            send_error(
                stream,
                request.id,
                "ipc.request-limit",
                "The IPC session request limit was reached.",
                false,
            )
            .await?;
            return Ok(SessionOutcome::PeerDisconnected);
        }
        if !request_ids.insert(request.id.clone()) {
            send_error(
                stream,
                request.id,
                "ipc.duplicate-request-id",
                "The request ID was already used in this session.",
                false,
            )
            .await?;
            continue;
        }

        if matches!(request.message_type.as_str(), "room.create" | "room.join")
            && identity.is_none()
        {
            send_error(
                stream,
                request.id,
                "identity.not-initialized",
                "Initialize the secure identity before entering a room.",
                false,
            )
            .await?;
            continue;
        }

        match request.message_type.as_str() {
            "identity.initialize" => {
                match serde_json::from_value::<IdentityInitializeRequest>(request.payload) {
                    Ok(payload) => match decode_identity(payload) {
                        Some(private_key) => {
                            room.initialize_identity(private_key.clone()).await;
                            identity = Some(private_key);
                            write_response(
                                stream,
                                request.id,
                                "identity.initialized",
                                IdentityInitializeResponse { initialized: true },
                            )
                            .await?
                        }
                        None => {
                            send_error(
                                stream,
                                request.id,
                                "identity.invalid-key",
                                "The identity private key must be a 32-byte hexadecimal value.",
                                false,
                            )
                            .await?;
                        }
                    },
                    Err(_) => {
                        send_error(
                            stream,
                            request.id,
                            "identity.invalid-request",
                            "The identity.initialize payload is invalid.",
                            false,
                        )
                        .await?;
                    }
                }
            }
            "room.status" => {
                write_response(
                    stream,
                    request.id,
                    "room.status.result",
                    room.status().await,
                )
                .await?;
            }
            "room.leave" => match room.leave().await {
                Ok(snapshot) => write_response(stream, request.id, "room.left", snapshot).await?,
                Err(error) => send_room_error(stream, request.id, error).await?,
            },
            "room.create" => match serde_json::from_value::<CreateRoomRequest>(request.payload) {
                Ok(payload) => match room.create(payload).await {
                    Ok(snapshot) => {
                        write_response(stream, request.id, "room.created", snapshot).await?
                    }
                    Err(error) => send_room_error(stream, request.id, error).await?,
                },
                Err(_) => {
                    send_error(
                        stream,
                        request.id,
                        "room.invalid-request",
                        "The room.create payload is invalid.",
                        false,
                    )
                    .await?;
                }
            },
            "room.join" => match serde_json::from_value::<JoinRoomRequest>(request.payload) {
                Ok(payload) => match room.join(payload).await {
                    Ok(snapshot) => {
                        write_response(stream, request.id, "room.joined", snapshot).await?
                    }
                    Err(error) => send_room_error(stream, request.id, error).await?,
                },
                Err(_) => {
                    send_error(
                        stream,
                        request.id,
                        "room.invalid-request",
                        "The room.join payload is invalid.",
                        false,
                    )
                    .await?;
                }
            },
            "room.set-lan-address" => {
                match serde_json::from_value::<SetLanAddressRequest>(request.payload) {
                    Ok(payload) => match room.set_lan_address(payload).await {
                        Ok(snapshot) => {
                            write_response(stream, request.id, "room.state-changed", snapshot)
                                .await?
                        }
                        Err(error) => send_room_error(stream, request.id, error).await?,
                    },
                    Err(_) => {
                        send_error(
                            stream,
                            request.id,
                            "room.invalid-request",
                            "The room.set-lan-address payload is invalid.",
                            false,
                        )
                        .await?;
                    }
                }
            }
            "network.diagnose" => match room.diagnose().await {
                Ok(status) => {
                    write_response(stream, request.id, "diagnostic.updated", status).await?
                }
                Err(error) => send_room_error(stream, request.id, error).await?,
            },
            "shutdown" => {
                write_response(stream, request.id, "shutdown.accepted", Empty {}).await?;
                return Ok(SessionOutcome::ShutdownRequested);
            }
            _ => {
                send_error(
                    stream,
                    request.id,
                    "ipc.unknown-message-type",
                    "Unsupported request type.",
                    false,
                )
                .await?;
            }
        }
    }
}

async fn write_response<S, T>(
    stream: &mut S,
    id: String,
    message_type: &str,
    payload: T,
) -> Result<(), FrameError>
where
    S: AsyncWrite + Unpin,
    T: Serialize,
{
    write_frame(stream, &Envelope::response(id, message_type, payload)?).await
}

async fn send_error<S>(
    stream: &mut S,
    id: String,
    code: &str,
    message: &str,
    retryable: bool,
) -> Result<(), FrameError>
where
    S: AsyncWrite + Unpin,
{
    write_response(
        stream,
        id,
        "error",
        ErrorResponse {
            code,
            message,
            retryable,
        },
    )
    .await
}

async fn send_room_error<S>(stream: &mut S, id: String, error: RoomError) -> Result<(), FrameError>
where
    S: AsyncWrite + Unpin,
{
    send_error(stream, id, &error.code, &error.message, error.retryable).await
}

fn valid_request_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().all(|byte| (0x20..=0x7e).contains(&byte))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct IdentityInitializeRequest {
    private_key: Zeroizing<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct IdentityInitializeResponse {
    initialized: bool,
}

fn decode_identity(request: IdentityInitializeRequest) -> Option<Zeroizing<[u8; 32]>> {
    if request.private_key.len() != 64 {
        return None;
    }

    let mut private_key = Zeroizing::new([0_u8; 32]);
    for (index, chunk) in request.private_key.as_bytes().chunks_exact(2).enumerate() {
        let encoded = std::str::from_utf8(chunk).ok()?;
        private_key[index] = u8::from_str_radix(encoded, 16).ok()?;
    }
    Some(private_key)
}

#[derive(Serialize)]
struct Empty {}

#[cfg(test)]
mod tests {
    use std::{net::SocketAddr, sync::Arc};

    use async_trait::async_trait;
    use serde_json::json;

    use super::{SessionOutcome, run, valid_request_id};
    use crate::{
        bootstrap::SecretToken,
        protocol::{
            Envelope, PROTOCOL_VERSION,
            framing::{read_frame, write_frame},
        },
        room::{
            BackendRoom, ConnectionMode, CreateRoomRequest, JoinRoomRequest, NetworkStatus,
            RoomBackend, RoomError, RoomService,
        },
    };

    struct TestBackend;

    #[async_trait]
    impl RoomBackend for TestBackend {
        async fn set_identity(&self, _identity: zeroize::Zeroizing<[u8; 32]>) {}

        async fn create(&self, _request: &CreateRoomRequest) -> Result<BackendRoom, RoomError> {
            Ok(BackendRoom {
                room_code: "AB12-CD34-EF56".into(),
                local_address: None,
                network: network_status(),
                members: Vec::new(),
            })
        }

        async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError> {
            Ok(BackendRoom {
                room_code: request.room_code.clone(),
                local_address: Some("127.0.0.1:25566".into()),
                network: network_status(),
                members: Vec::new(),
            })
        }

        async fn set_lan_address(&self, _address: SocketAddr) -> Result<(), RoomError> {
            Ok(())
        }

        async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
            Ok(network_status())
        }

        async fn leave(&self) -> Result<(), RoomError> {
            Ok(())
        }
    }

    fn network_status() -> NetworkStatus {
        NetworkStatus {
            nat_type: Some("FullCone".into()),
            connection_mode: ConnectionMode::Direct,
            round_trip_time_milliseconds: Some(5),
            packet_loss_percent: Some(0.0),
            relay_node: None,
            is_healthy: true,
        }
    }

    #[test]
    fn request_id_is_bounded_printable_ascii() {
        assert!(valid_request_id("request-1"));
        assert!(!valid_request_id(""));
        assert!(!valid_request_id("line\nbreak"));
        assert!(!valid_request_id(&"a".repeat(129)));
    }

    #[tokio::test]
    async fn authenticated_session_reports_idle_and_shuts_down_cleanly() {
        let token = "a".repeat(64);
        let secret = SecretToken::read_from(token.as_bytes()).unwrap();
        let (mut client, mut server) = tokio::io::duplex(16 * 1024);
        let room = RoomService::default();
        let session = tokio::spawn(async move { run(&mut server, &secret, &room).await });

        write_frame(
            &mut client,
            &request(
                "hello-1",
                "hello",
                json!({
                    "authToken": token,
                    "client": "pcln",
                    "clientVersion": "0.1.0-alpha.2"
                }),
            ),
        )
        .await
        .unwrap();
        assert_eq!(
            read_frame(&mut client).await.unwrap().message_type,
            "hello.accepted"
        );

        write_frame(&mut client, &request("status-1", "room.status", json!({})))
            .await
            .unwrap();
        let status = read_frame(&mut client).await.unwrap();
        assert_eq!(status.message_type, "room.status.result");
        assert_eq!(status.payload["state"], "idle");

        write_frame(&mut client, &request("shutdown-1", "shutdown", json!({})))
            .await
            .unwrap();
        assert_eq!(
            read_frame(&mut client).await.unwrap().message_type,
            "shutdown.accepted"
        );
        assert_eq!(
            session.await.unwrap().unwrap(),
            SessionOutcome::ShutdownRequested
        );
    }

    #[tokio::test]
    async fn authentication_failure_never_echoes_secret() {
        let expected = "a".repeat(64);
        let supplied = "b".repeat(64);
        let secret = SecretToken::read_from(expected.as_bytes()).unwrap();
        let (mut client, mut server) = tokio::io::duplex(8 * 1024);
        let room = RoomService::default();
        let session = tokio::spawn(async move { run(&mut server, &secret, &room).await });

        write_frame(
            &mut client,
            &request(
                "hello-1",
                "hello",
                json!({
                    "authToken": supplied,
                    "client": "pcln",
                    "clientVersion": "0.1.0-alpha.2"
                }),
            ),
        )
        .await
        .unwrap();
        let response = read_frame(&mut client).await.unwrap();
        let serialized = serde_json::to_string(&response).unwrap();
        assert_eq!(response.message_type, "error");
        assert!(!serialized.contains(&expected));
        assert!(!serialized.contains(&supplied));
        assert_eq!(
            session.await.unwrap().unwrap(),
            SessionOutcome::PeerDisconnected
        );
    }

    #[tokio::test]
    async fn room_commands_flow_through_injected_backend() {
        let token = "a".repeat(64);
        let secret = SecretToken::read_from(token.as_bytes()).unwrap();
        let (mut client, mut server) = tokio::io::duplex(32 * 1024);
        let room = RoomService::new(Arc::new(TestBackend));
        let session = tokio::spawn(async move { run(&mut server, &secret, &room).await });

        write_frame(
            &mut client,
            &request(
                "hello-1",
                "hello",
                json!({
                    "authToken": token,
                    "client": "pcln",
                    "clientVersion": "0.1.0-alpha.2"
                }),
            ),
        )
        .await
        .unwrap();
        let hello = read_frame(&mut client).await.unwrap();
        assert_eq!(hello.message_type, "hello.accepted");
        assert!(
            hello.payload["capabilities"]
                .as_array()
                .unwrap()
                .iter()
                .any(|value| value == "room.create")
        );

        write_frame(
            &mut client,
            &request(
                "identity-1",
                "identity.initialize",
                json!({ "privateKey": "11".repeat(32) }),
            ),
        )
        .await
        .unwrap();
        let identity = read_frame(&mut client).await.unwrap();
        assert_eq!(identity.message_type, "identity.initialized");
        assert_eq!(identity.payload["initialized"], true);

        write_frame(
            &mut client,
            &request(
                "create-1",
                "room.create",
                json!({
                    "gameSessionId": "session-1",
                    "lanAddress": "127.0.0.1:25565",
                    "preferDirect": true,
                    "allowRelay": true
                }),
            ),
        )
        .await
        .unwrap();
        let created = read_frame(&mut client).await.unwrap();
        assert_eq!(created.message_type, "room.created");
        assert_eq!(created.payload["state"], "connected");
        assert_eq!(created.payload["roomCode"], "AB12-CD34-EF56");

        write_frame(
            &mut client,
            &request(
                "set-lan-1",
                "room.set-lan-address",
                json!({ "lanAddress": "127.0.0.1:25567" }),
            ),
        )
        .await
        .unwrap();
        assert_eq!(
            read_frame(&mut client).await.unwrap().message_type,
            "room.state-changed"
        );

        write_frame(
            &mut client,
            &request("diagnose-1", "network.diagnose", json!({})),
        )
        .await
        .unwrap();
        let diagnostic = read_frame(&mut client).await.unwrap();
        assert_eq!(diagnostic.message_type, "diagnostic.updated");
        assert_eq!(diagnostic.payload["connectionMode"], "direct");

        write_frame(&mut client, &request("leave-1", "room.leave", json!({})))
            .await
            .unwrap();
        assert_eq!(
            read_frame(&mut client).await.unwrap().payload["state"],
            "idle"
        );

        write_frame(
            &mut client,
            &request(
                "join-1",
                "room.join",
                json!({ "roomCode": "ab12 cd34 ef56", "gameSessionId": null }),
            ),
        )
        .await
        .unwrap();
        let joined = read_frame(&mut client).await.unwrap();
        assert_eq!(joined.message_type, "room.joined");
        assert_eq!(joined.payload["localAddress"], "127.0.0.1:25566");

        write_frame(&mut client, &request("shutdown-1", "shutdown", json!({})))
            .await
            .unwrap();
        assert_eq!(
            read_frame(&mut client).await.unwrap().message_type,
            "shutdown.accepted"
        );
        assert_eq!(
            session.await.unwrap().unwrap(),
            SessionOutcome::ShutdownRequested
        );
    }

    fn request(id: &str, message_type: &str, payload: serde_json::Value) -> Envelope {
        Envelope {
            protocol: PROTOCOL_VERSION,
            id: id.into(),
            message_type: message_type.into(),
            payload,
        }
    }
}
