use std::{net::SocketAddr, sync::Arc, time::Duration};

use async_trait::async_trait;
use tokio::sync::{Mutex, watch};
use zeroize::Zeroizing;

use crate::{
    room::{
        BackendRoom, ConnectionMode, CreateRoomRequest, JoinRoomRequest, NetworkStatus,
        RoomBackend, RoomError, RoomMember,
    },
    scaffolding::{PlayerKind, PlayerProfile, ScaffoldingClient, ScaffoldingServer, ServerContext},
};

use super::{
    credentials::{RoomCredentials, machine_id_from_identity},
    discovery::{
        RoomEndpointAdvertisement, clear_local_endpoint, load_local_endpoint, now_unix_seconds,
        publish_local_endpoint,
    },
    easytier::{
        EasyTierLaunchConfig, EasyTierNode, easytier_missing, resolve_easytier_binary,
        start_easytier,
    },
    port_forward::PortForward,
};

const HOST_VIRTUAL_IPV4: &str = "10.144.144.1";
const JOIN_DISCOVERY_ATTEMPTS: u32 = 20;
const JOIN_DISCOVERY_INTERVAL: Duration = Duration::from_millis(250);

struct HostSession {
    credentials: RoomCredentials,
    easytier: EasyTierNode,
    scaffolding_shutdown: watch::Sender<bool>,
    scaffolding_task: tokio::task::JoinHandle<Result<(), crate::scaffolding::ScaffoldingError>>,
    scaffolding_addr: SocketAddr,
    minecraft: SocketAddr,
}

struct MemberSession {
    easytier: EasyTierNode,
    scaffolding_forward: PortForward,
    minecraft_forward: PortForward,
}

enum ActiveSession {
    Host(HostSession),
    Member(MemberSession),
}

/// Production room backend: EasyTier process + Scaffolding + local port forwards.
pub struct EasyTierRoomBackend {
    identity: Mutex<Option<Zeroizing<[u8; 32]>>>,
    session: Mutex<Option<ActiveSession>>,
}

impl Default for EasyTierRoomBackend {
    fn default() -> Self {
        Self::new()
    }
}

impl EasyTierRoomBackend {
    pub fn new() -> Self {
        Self {
            identity: Mutex::new(None),
            session: Mutex::new(None),
        }
    }

    async fn require_identity(&self) -> Result<Zeroizing<[u8; 32]>, RoomError> {
        self.identity.lock().await.clone().ok_or_else(|| {
            RoomError::new(
                "identity.not-initialized",
                "Initialize the secure identity before entering a room.",
                false,
            )
        })
    }

    async fn ensure_idle(&self) -> Result<(), RoomError> {
        if self.session.lock().await.is_some() {
            return Err(RoomError::new(
                "room.operation-in-progress",
                "A room session is already active in the network backend.",
                false,
            ));
        }
        Ok(())
    }

    async fn stop_session_locked(session: &mut Option<ActiveSession>) {
        if let Some(active) = session.take() {
            match active {
                ActiveSession::Host(host) => {
                    let room_code = host.credentials.room_code.clone();
                    let _ = host.scaffolding_shutdown.send(true);
                    host.scaffolding_task.abort();
                    let _ = host.easytier.stop().await;
                    let _ = clear_local_endpoint(&room_code);
                }
                ActiveSession::Member(member) => {
                    member.scaffolding_forward.stop().await;
                    member.minecraft_forward.stop().await;
                    let _ = member.easytier.stop().await;
                }
            }
        }
    }
}

#[async_trait]
impl RoomBackend for EasyTierRoomBackend {
    async fn set_identity(&self, identity: Zeroizing<[u8; 32]>) {
        *self.identity.lock().await = Some(identity);
    }

    async fn create(&self, request: &CreateRoomRequest) -> Result<BackendRoom, RoomError> {
        self.ensure_idle().await?;
        let identity = self.require_identity().await?;
        let binary = resolve_easytier_binary().ok_or_else(easytier_missing)?;
        let minecraft = request
            .lan_address
            .parse::<SocketAddr>()
            .map_err(|_| RoomError::invalid("The LAN address must be a valid IP endpoint."))?;
        if !minecraft.ip().is_loopback() || minecraft.port() == 0 {
            return Err(RoomError::invalid(
                "The LAN address must use a loopback IP and a non-zero port.",
            ));
        }

        let credentials = RoomCredentials::generate()?;
        let easytier = start_easytier(
            &credentials,
            EasyTierLaunchConfig {
                binary,
                prefer_direct: request.prefer_direct,
                allow_relay: request.allow_relay,
                host_ipv4: Some(HOST_VIRTUAL_IPV4),
            },
        )
        .await?;

        let host_profile = PlayerProfile {
            name: "Host".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Host),
        };
        let context = Arc::new(ServerContext::new(host_profile, minecraft.port()).map_err(
            |error| {
                RoomError::new(
                    "network.scaffolding-failed",
                    format!("Failed to create Scaffolding context: {error}"),
                    false,
                )
            },
        )?);
        let server =
            ScaffoldingServer::bind(SocketAddr::from(([127, 0, 0, 1], 0)), Arc::clone(&context))
                .await
                .map_err(|error| {
                    RoomError::new(
                        "network.scaffolding-failed",
                        format!("Failed to bind Scaffolding server: {error}"),
                        true,
                    )
                })?;
        let scaffolding_addr = server.local_addr().map_err(|error| {
            RoomError::new(
                "network.scaffolding-failed",
                format!("Failed to read Scaffolding address: {error}"),
                true,
            )
        })?;
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let scaffolding_task = tokio::spawn(server.run(shutdown_rx));

        if let Err(error) = publish_local_endpoint(&RoomEndpointAdvertisement {
            room_code: credentials.room_code.clone(),
            scaffolding: scaffolding_addr.to_string(),
            minecraft: minecraft.to_string(),
            published_unix_seconds: now_unix_seconds(),
        }) {
            let _ = shutdown_tx.send(true);
            scaffolding_task.abort();
            let _ = easytier.stop().await;
            return Err(error);
        }

        let room = BackendRoom {
            room_code: credentials.room_code.clone(),
            local_address: None,
            network: host_network_status(request.prefer_direct, request.allow_relay),
            members: vec![RoomMember {
                id: machine_id_from_identity(&identity),
                display_name: "Host".into(),
                connection_mode: ConnectionMode::Direct,
                round_trip_time_milliseconds: Some(0),
                packet_loss_percent: Some(0.0),
            }],
        };

        *self.session.lock().await = Some(ActiveSession::Host(HostSession {
            credentials,
            easytier,
            scaffolding_shutdown: shutdown_tx,
            scaffolding_task,
            scaffolding_addr,
            minecraft,
        }));

        let _ = scaffolding_addr;
        Ok(room)
    }

    async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError> {
        self.ensure_idle().await?;
        let identity = self.require_identity().await?;
        let binary = resolve_easytier_binary().ok_or_else(easytier_missing)?;
        let credentials = RoomCredentials::from_room_code(&request.room_code)?;

        let easytier = start_easytier(
            &credentials,
            EasyTierLaunchConfig {
                binary,
                prefer_direct: true,
                allow_relay: true,
                host_ipv4: None,
            },
        )
        .await?;

        let advertisement = match wait_for_local_endpoint(&credentials.room_code).await {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(error);
            }
        };

        let scaffolding_target = match advertisement.scaffolding_addr() {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(error);
            }
        };
        let minecraft_target = match advertisement.minecraft_addr() {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(error);
            }
        };

        let scaffolding_forward = match PortForward::start(scaffolding_target).await {
            Ok(value) => value,
            Err(error) => {
                let _ = easytier.stop().await;
                return Err(RoomError::new(
                    "network.forward-failed",
                    format!("Failed to create Scaffolding forward: {error}"),
                    true,
                ));
            }
        };
        let minecraft_forward = match PortForward::start(minecraft_target).await {
            Ok(value) => value,
            Err(error) => {
                scaffolding_forward.stop().await;
                let _ = easytier.stop().await;
                return Err(RoomError::new(
                    "network.forward-failed",
                    format!("Failed to create Minecraft forward: {error}"),
                    true,
                ));
            }
        };

        let guest_profile = PlayerProfile {
            name: "Player".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Guest),
        };
        let mut members = vec![RoomMember {
            id: machine_id_from_identity(&identity),
            display_name: "Player".into(),
            connection_mode: ConnectionMode::Unknown,
            round_trip_time_milliseconds: None,
            packet_loss_percent: None,
        }];
        let mut rtt_ms = None;
        match ScaffoldingClient::connect(scaffolding_forward.local_addr(), guest_profile).await {
            Ok(mut client) => match client.heartbeat().await {
                Ok(heartbeat) => {
                    rtt_ms = Some(heartbeat.latency.as_millis().min(u128::from(u32::MAX)) as u32);
                    members = heartbeat
                        .players
                        .into_iter()
                        .map(|profile| RoomMember {
                            id: profile.machine_id,
                            display_name: profile.name,
                            connection_mode: ConnectionMode::Unknown,
                            round_trip_time_milliseconds: rtt_ms,
                            packet_loss_percent: None,
                        })
                        .collect();
                }
                Err(error) => {
                    tracing::debug!(%error, "Scaffolding heartbeat failed after join");
                }
            },
            Err(error) => {
                tracing::debug!(%error, "Scaffolding client connect failed after join");
            }
        }

        let room = BackendRoom {
            room_code: credentials.room_code.clone(),
            local_address: Some(minecraft_forward.local_addr().to_string()),
            network: NetworkStatus {
                nat_type: Some("Unknown".into()),
                connection_mode: ConnectionMode::Unknown,
                round_trip_time_milliseconds: rtt_ms,
                packet_loss_percent: None,
                relay_node: None,
                is_healthy: true,
            },
            members,
        };
        // Drop credentials so the network secret is zeroized once join completes.
        drop(credentials);

        *self.session.lock().await = Some(ActiveSession::Member(MemberSession {
            easytier,
            scaffolding_forward,
            minecraft_forward,
        }));
        Ok(room)
    }

    async fn set_lan_address(&self, address: SocketAddr) -> Result<(), RoomError> {
        let mut guard = self.session.lock().await;
        let Some(ActiveSession::Host(host)) = guard.as_mut() else {
            return Err(RoomError::new(
                "room.not-host",
                "Only a connected room host can change the LAN address.",
                false,
            ));
        };
        if !address.ip().is_loopback() || address.port() == 0 {
            return Err(RoomError::invalid(
                "The LAN address must use a loopback IP and a non-zero port.",
            ));
        }

        // Restart Scaffolding against the new Minecraft port while keeping the EasyTier node.
        let _ = host.scaffolding_shutdown.send(true);
        host.scaffolding_task.abort();
        let identity = self.require_identity().await?;
        let host_profile = PlayerProfile {
            name: "Host".into(),
            machine_id: machine_id_from_identity(&identity),
            vendor: "PCL N Terracotta".into(),
            kind: Some(PlayerKind::Host),
        };
        let context = Arc::new(ServerContext::new(host_profile, address.port()).map_err(
            |error| {
                RoomError::new(
                    "network.scaffolding-failed",
                    format!("Failed to create Scaffolding context: {error}"),
                    false,
                )
            },
        )?);
        let server =
            ScaffoldingServer::bind(SocketAddr::from(([127, 0, 0, 1], 0)), Arc::clone(&context))
                .await
                .map_err(|error| {
                    RoomError::new(
                        "network.scaffolding-failed",
                        format!("Failed to rebind Scaffolding server: {error}"),
                        true,
                    )
                })?;
        let scaffolding_addr = server.local_addr().map_err(|error| {
            RoomError::new(
                "network.scaffolding-failed",
                format!("Failed to read Scaffolding address: {error}"),
                true,
            )
        })?;
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        host.scaffolding_shutdown = shutdown_tx;
        host.scaffolding_task = tokio::spawn(server.run(shutdown_rx));
        host.scaffolding_addr = scaffolding_addr;
        host.minecraft = address;
        publish_local_endpoint(&RoomEndpointAdvertisement {
            room_code: host.credentials.room_code.clone(),
            scaffolding: scaffolding_addr.to_string(),
            minecraft: address.to_string(),
            published_unix_seconds: now_unix_seconds(),
        })?;
        Ok(())
    }

    async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
        let guard = self.session.lock().await;
        match guard.as_ref() {
            Some(ActiveSession::Host(_)) => Ok(NetworkStatus {
                nat_type: Some("Unknown".into()),
                connection_mode: ConnectionMode::Direct,
                round_trip_time_milliseconds: Some(0),
                packet_loss_percent: Some(0.0),
                relay_node: None,
                is_healthy: true,
            }),
            Some(ActiveSession::Member(_)) => Ok(NetworkStatus {
                nat_type: Some("Unknown".into()),
                connection_mode: ConnectionMode::Unknown,
                round_trip_time_milliseconds: None,
                packet_loss_percent: None,
                relay_node: None,
                is_healthy: true,
            }),
            None => Err(RoomError::new(
                "room.not-connected",
                "Network diagnostics require an active room.",
                false,
            )),
        }
    }

    async fn leave(&self) -> Result<(), RoomError> {
        let mut guard = self.session.lock().await;
        Self::stop_session_locked(&mut guard).await;
        Ok(())
    }
}

async fn wait_for_local_endpoint(room_code: &str) -> Result<RoomEndpointAdvertisement, RoomError> {
    for _ in 0..JOIN_DISCOVERY_ATTEMPTS {
        if let Some(advertisement) = load_local_endpoint(room_code)? {
            return Ok(advertisement);
        }
        tokio::time::sleep(JOIN_DISCOVERY_INTERVAL).await;
    }
    Err(RoomError::new(
        "network.peer-unreachable",
        "The room host was not discovered on this machine. Cross-machine EasyTier port mapping is not fully available in this alpha; host and member must currently share a local discovery path, or the host must still be online.",
        true,
    ))
}

fn host_network_status(prefer_direct: bool, allow_relay: bool) -> NetworkStatus {
    NetworkStatus {
        nat_type: Some("Unknown".into()),
        connection_mode: if prefer_direct {
            ConnectionMode::Direct
        } else if allow_relay {
            ConnectionMode::Relay
        } else {
            ConnectionMode::Unknown
        },
        round_trip_time_milliseconds: Some(0),
        packet_loss_percent: Some(0.0),
        relay_node: None,
        is_healthy: true,
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::EasyTierRoomBackend;
    use crate::room::{CreateRoomRequest, RoomBackend, RoomService};

    #[tokio::test]
    async fn missing_easytier_binary_returns_stable_fault() {
        // Ensure the sidecar lookup fails even if a developer has one on PATH.
        // SAFETY: test isolation for EasyTier binary resolution.
        unsafe {
            std::env::set_var(
                "TERRACOTTA_EASYTIER_PATH",
                std::env::temp_dir().join("terracotta-missing-easytier-core"),
            );
        }

        let backend = Arc::new(EasyTierRoomBackend::new());
        RoomBackend::set_identity(backend.as_ref(), zeroize::Zeroizing::new([9_u8; 32])).await;
        let service = RoomService::new(backend);
        let error = service
            .create(CreateRoomRequest {
                game_session_id: "session-1".into(),
                lan_address: "127.0.0.1:25565".into(),
                prefer_direct: true,
                allow_relay: true,
            })
            .await
            .unwrap_err();
        assert_eq!(error.code, "network.easytier-missing");

        unsafe {
            std::env::remove_var("TERRACOTTA_EASYTIER_PATH");
        }
    }
}
