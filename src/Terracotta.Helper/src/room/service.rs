use std::{net::SocketAddr, sync::Arc};

use async_trait::async_trait;
use serde::{Deserialize, Serialize};
use tokio::sync::Mutex;
use zeroize::Zeroizing;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum RoomState {
    Idle,
    Creating,
    Joining,
    Connected,
    Reconnecting,
    Leaving,
    Faulted,
    Diagnosing,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum RoomRole {
    None,
    Host,
    Member,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ConnectionMode {
    Unknown,
    Direct,
    Relay,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoomMember {
    pub id: String,
    pub display_name: String,
    pub connection_mode: ConnectionMode,
    pub round_trip_time_milliseconds: Option<u32>,
    pub packet_loss_percent: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NetworkStatus {
    pub nat_type: Option<String>,
    pub connection_mode: ConnectionMode,
    pub round_trip_time_milliseconds: Option<u32>,
    pub packet_loss_percent: Option<f64>,
    pub relay_node: Option<String>,
    pub is_healthy: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RoomSnapshot {
    pub state: RoomState,
    pub role: RoomRole,
    pub room_code: Option<String>,
    pub local_address: Option<String>,
    pub game_session_id: Option<String>,
    pub network: Option<NetworkStatus>,
    pub members: Vec<RoomMember>,
    pub error_code: Option<String>,
    pub error_message: Option<String>,
}

impl RoomSnapshot {
    fn idle() -> Self {
        Self {
            state: RoomState::Idle,
            role: RoomRole::None,
            room_code: None,
            local_address: None,
            game_session_id: None,
            network: None,
            members: Vec::new(),
            error_code: None,
            error_message: None,
        }
    }

    fn pending(state: RoomState, role: RoomRole, game_session_id: Option<String>) -> Self {
        Self {
            state,
            role,
            room_code: None,
            local_address: None,
            game_session_id,
            network: None,
            members: Vec::new(),
            error_code: None,
            error_message: None,
        }
    }

    fn faulted(role: RoomRole, game_session_id: Option<String>, error: &RoomError) -> Self {
        Self {
            state: RoomState::Faulted,
            role,
            room_code: None,
            local_address: None,
            game_session_id,
            network: None,
            members: Vec::new(),
            error_code: Some(error.code.clone()),
            error_message: Some(error.message.clone()),
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateRoomRequest {
    pub game_session_id: String,
    pub lan_address: String,
    pub prefer_direct: bool,
    pub allow_relay: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JoinRoomRequest {
    pub room_code: String,
    pub game_session_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SetLanAddressRequest {
    pub lan_address: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoomError {
    pub code: String,
    pub message: String,
    pub retryable: bool,
}

impl RoomError {
    pub fn new(code: impl Into<String>, message: impl Into<String>, retryable: bool) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
            retryable,
        }
    }

    pub(crate) fn invalid(message: impl Into<String>) -> Self {
        Self::new("room.invalid-request", message, false)
    }

    fn busy() -> Self {
        Self::new(
            "room.operation-in-progress",
            "A room operation is already in progress.",
            false,
        )
    }
}

#[derive(Debug, Clone)]
pub struct BackendRoom {
    pub room_code: String,
    pub local_address: Option<String>,
    pub network: NetworkStatus,
    pub members: Vec<RoomMember>,
}

#[async_trait]
pub trait RoomBackend: Send + Sync {
    async fn set_identity(&self, identity: Zeroizing<[u8; 32]>);

    async fn create(&self, request: &CreateRoomRequest) -> Result<BackendRoom, RoomError>;

    async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError>;

    async fn set_lan_address(&self, address: SocketAddr) -> Result<(), RoomError>;

    async fn diagnose(&self) -> Result<NetworkStatus, RoomError>;

    async fn leave(&self) -> Result<(), RoomError>;
}

pub struct RoomService {
    state: Mutex<RoomSnapshot>,
    backend: Arc<dyn RoomBackend>,
}

impl Default for RoomService {
    fn default() -> Self {
        Self::new(Arc::new(crate::network::EasyTierRoomBackend::new()))
    }
}

impl RoomService {
    pub fn new(backend: Arc<dyn RoomBackend>) -> Self {
        Self {
            state: Mutex::new(RoomSnapshot::idle()),
            backend,
        }
    }

    pub async fn initialize_identity(&self, identity: Zeroizing<[u8; 32]>) {
        self.backend.set_identity(identity).await;
    }

    pub async fn status(&self) -> RoomSnapshot {
        self.state.lock().await.clone()
    }

    pub async fn create(&self, mut request: CreateRoomRequest) -> Result<RoomSnapshot, RoomError> {
        request.game_session_id = validate_session_id(&request.game_session_id)?.to_owned();
        request.lan_address = validate_loopback_address(&request.lan_address)?.to_string();

        {
            let mut state = self.state.lock().await;
            if state.state != RoomState::Idle {
                return Err(RoomError::busy());
            }
            *state = RoomSnapshot::pending(
                RoomState::Creating,
                RoomRole::Host,
                Some(request.game_session_id.clone()),
            );
        }

        match self.backend.create(&request).await {
            Ok(room) => {
                let room = match validate_backend_room(room, false) {
                    Ok(room) => room,
                    Err(error) => {
                        *self.state.lock().await = RoomSnapshot::faulted(
                            RoomRole::Host,
                            Some(request.game_session_id),
                            &error,
                        );
                        return Err(error);
                    }
                };
                let snapshot = RoomSnapshot {
                    state: RoomState::Connected,
                    role: RoomRole::Host,
                    room_code: Some(room.room_code),
                    local_address: room.local_address,
                    game_session_id: Some(request.game_session_id),
                    network: Some(room.network),
                    members: room.members,
                    error_code: None,
                    error_message: None,
                };
                *self.state.lock().await = snapshot.clone();
                Ok(snapshot)
            }
            Err(error) => {
                *self.state.lock().await =
                    RoomSnapshot::faulted(RoomRole::Host, Some(request.game_session_id), &error);
                Err(error)
            }
        }
    }

    pub async fn join(&self, mut request: JoinRoomRequest) -> Result<RoomSnapshot, RoomError> {
        request.room_code = normalize_room_code(&request.room_code)?;
        request.game_session_id = request
            .game_session_id
            .as_deref()
            .map(validate_session_id)
            .transpose()?
            .map(str::to_owned);

        {
            let mut state = self.state.lock().await;
            if state.state != RoomState::Idle {
                return Err(RoomError::busy());
            }
            *state = RoomSnapshot::pending(
                RoomState::Joining,
                RoomRole::Member,
                request.game_session_id.clone(),
            );
        }

        match self.backend.join(&request).await {
            Ok(room) => {
                let room = match validate_backend_room(room, true) {
                    Ok(room) => room,
                    Err(error) => {
                        *self.state.lock().await = RoomSnapshot::faulted(
                            RoomRole::Member,
                            request.game_session_id,
                            &error,
                        );
                        return Err(error);
                    }
                };
                let snapshot = RoomSnapshot {
                    state: RoomState::Connected,
                    role: RoomRole::Member,
                    room_code: Some(room.room_code),
                    local_address: room.local_address,
                    game_session_id: request.game_session_id,
                    network: Some(room.network),
                    members: room.members,
                    error_code: None,
                    error_message: None,
                };
                *self.state.lock().await = snapshot.clone();
                Ok(snapshot)
            }
            Err(error) => {
                *self.state.lock().await =
                    RoomSnapshot::faulted(RoomRole::Member, request.game_session_id, &error);
                Err(error)
            }
        }
    }

    pub async fn set_lan_address(
        &self,
        request: SetLanAddressRequest,
    ) -> Result<RoomSnapshot, RoomError> {
        let address = validate_loopback_address(&request.lan_address)?;
        let current = self.state.lock().await.clone();
        if current.state != RoomState::Connected || current.role != RoomRole::Host {
            return Err(RoomError::new(
                "room.not-host",
                "Only a connected room host can change the LAN address.",
                false,
            ));
        }

        self.backend.set_lan_address(address).await?;
        Ok(self.status().await)
    }

    pub async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
        let previous = {
            let mut state = self.state.lock().await;
            if state.state != RoomState::Connected && state.state != RoomState::Reconnecting {
                return Err(RoomError::new(
                    "room.not-connected",
                    "Network diagnostics require an active room.",
                    false,
                ));
            }
            let previous = state.state;
            state.state = RoomState::Diagnosing;
            previous
        };

        match self.backend.diagnose().await {
            Ok(network) => {
                let mut state = self.state.lock().await;
                state.state = previous;
                state.network = Some(network.clone());
                Ok(network)
            }
            Err(error) => {
                let current = self.state.lock().await.clone();
                *self.state.lock().await =
                    RoomSnapshot::faulted(current.role, current.game_session_id, &error);
                Err(error)
            }
        }
    }

    pub async fn leave(&self) -> Result<RoomSnapshot, RoomError> {
        {
            let mut state = self.state.lock().await;
            if state.state == RoomState::Idle {
                return Ok(state.clone());
            }
            if state.state == RoomState::Leaving {
                return Err(RoomError::busy());
            }
            state.state = RoomState::Leaving;
        }

        match self.backend.leave().await {
            Ok(()) => {
                let idle = RoomSnapshot::idle();
                *self.state.lock().await = idle.clone();
                Ok(idle)
            }
            Err(error) => {
                let current = self.state.lock().await.clone();
                *self.state.lock().await =
                    RoomSnapshot::faulted(current.role, current.game_session_id, &error);
                Err(error)
            }
        }
    }
}

fn validate_session_id(value: &str) -> Result<&str, RoomError> {
    let value = value.trim();
    if value.is_empty() || value.len() > 128 || value.chars().any(char::is_control) {
        return Err(RoomError::invalid(
            "The game session ID must contain 1 to 128 non-control characters.",
        ));
    }
    Ok(value)
}

fn validate_loopback_address(value: &str) -> Result<SocketAddr, RoomError> {
    let address = value
        .parse::<SocketAddr>()
        .map_err(|_| RoomError::invalid("The LAN address must be a valid IP endpoint."))?;
    if !address.ip().is_loopback() || address.port() == 0 {
        return Err(RoomError::invalid(
            "The LAN address must use a loopback IP and a non-zero port.",
        ));
    }
    Ok(address)
}

fn normalize_room_code(value: &str) -> Result<String, RoomError> {
    let compact: String = value
        .chars()
        .filter(|character| !matches!(character, '-' | ' '))
        .map(|character| character.to_ascii_uppercase())
        .collect();
    if compact.len() != 12 || !compact.bytes().all(|byte| byte.is_ascii_alphanumeric()) {
        return Err(RoomError::new(
            "room.invalid-code",
            "The room code must contain three groups of four ASCII letters or digits.",
            false,
        ));
    }
    Ok(format!(
        "{}-{}-{}",
        &compact[0..4],
        &compact[4..8],
        &compact[8..12]
    ))
}

fn validate_backend_room(
    mut room: BackendRoom,
    require_local_address: bool,
) -> Result<BackendRoom, RoomError> {
    room.room_code = normalize_room_code(&room.room_code)?;
    room.local_address = room
        .local_address
        .as_deref()
        .map(validate_loopback_address)
        .transpose()?
        .map(|address| address.to_string());
    if require_local_address && room.local_address.is_none() {
        return Err(RoomError::new(
            "network.forward-missing",
            "The network backend did not provide a local Minecraft address.",
            false,
        ));
    }
    Ok(room)
}

#[cfg(test)]
mod tests {
    use std::{net::SocketAddr, sync::Arc};

    use async_trait::async_trait;

    use super::{
        BackendRoom, ConnectionMode, CreateRoomRequest, JoinRoomRequest, NetworkStatus,
        RoomBackend, RoomError, RoomRole, RoomService, RoomState,
    };

    #[derive(Default)]
    struct TestBackend;

    struct InvalidJoinBackend;

    #[async_trait]
    impl RoomBackend for TestBackend {
        async fn set_identity(&self, _identity: zeroize::Zeroizing<[u8; 32]>) {}

        async fn create(&self, _request: &CreateRoomRequest) -> Result<BackendRoom, RoomError> {
            Ok(room("AB12-CD34-EF56", None))
        }

        async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError> {
            Ok(room(&request.room_code, Some("127.0.0.1:25565")))
        }

        async fn set_lan_address(&self, _address: SocketAddr) -> Result<(), RoomError> {
            Ok(())
        }

        async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
            Ok(network())
        }

        async fn leave(&self) -> Result<(), RoomError> {
            Ok(())
        }
    }

    #[async_trait]
    impl RoomBackend for InvalidJoinBackend {
        async fn set_identity(&self, identity: zeroize::Zeroizing<[u8; 32]>) {
            TestBackend.set_identity(identity).await;
        }

        async fn create(&self, request: &CreateRoomRequest) -> Result<BackendRoom, RoomError> {
            TestBackend.create(request).await
        }

        async fn join(&self, request: &JoinRoomRequest) -> Result<BackendRoom, RoomError> {
            Ok(room(&request.room_code, Some("192.0.2.1:25565")))
        }

        async fn set_lan_address(&self, address: SocketAddr) -> Result<(), RoomError> {
            TestBackend.set_lan_address(address).await
        }

        async fn diagnose(&self) -> Result<NetworkStatus, RoomError> {
            TestBackend.diagnose().await
        }

        async fn leave(&self) -> Result<(), RoomError> {
            TestBackend.leave().await
        }
    }

    fn room(code: &str, local_address: Option<&str>) -> BackendRoom {
        BackendRoom {
            room_code: code.into(),
            local_address: local_address.map(str::to_owned),
            network: network(),
            members: Vec::new(),
        }
    }

    fn network() -> NetworkStatus {
        NetworkStatus {
            nat_type: Some("Unknown".into()),
            connection_mode: ConnectionMode::Direct,
            round_trip_time_milliseconds: Some(1),
            packet_loss_percent: Some(0.0),
            relay_node: None,
            is_healthy: true,
        }
    }

    #[tokio::test]
    async fn create_and_leave_follow_room_lifecycle() {
        let service = RoomService::new(Arc::new(TestBackend));
        let connected = service
            .create(CreateRoomRequest {
                game_session_id: " session-1 ".into(),
                lan_address: "127.0.0.1:25565".into(),
                prefer_direct: true,
                allow_relay: true,
            })
            .await
            .unwrap();

        assert_eq!(connected.state, RoomState::Connected);
        assert_eq!(connected.role, RoomRole::Host);
        assert_eq!(connected.room_code.as_deref(), Some("AB12-CD34-EF56"));
        assert_eq!(connected.game_session_id.as_deref(), Some("session-1"));
        assert_eq!(service.leave().await.unwrap().state, RoomState::Idle);
    }

    #[tokio::test]
    async fn join_normalizes_code_and_requires_loopback_forward() {
        let service = RoomService::new(Arc::new(TestBackend));
        let connected = service
            .join(JoinRoomRequest {
                room_code: "ab12 cd34-ef56".into(),
                game_session_id: None,
            })
            .await
            .unwrap();

        assert_eq!(connected.role, RoomRole::Member);
        assert_eq!(connected.room_code.as_deref(), Some("AB12-CD34-EF56"));
        assert_eq!(connected.local_address.as_deref(), Some("127.0.0.1:25565"));
    }

    #[tokio::test]
    async fn create_rejects_non_loopback_minecraft_address() {
        let service = RoomService::new(Arc::new(TestBackend));
        let error = service
            .create(CreateRoomRequest {
                game_session_id: "session-1".into(),
                lan_address: "192.0.2.1:25565".into(),
                prefer_direct: true,
                allow_relay: true,
            })
            .await
            .unwrap_err();

        assert_eq!(error.code, "room.invalid-request");
        assert_eq!(service.status().await.state, RoomState::Idle);
    }

    #[tokio::test]
    async fn missing_network_runtime_records_stable_fault() {
        // SAFETY: isolate EasyTier resolution for this unit test.
        unsafe {
            std::env::set_var(
                "TERRACOTTA_EASYTIER_PATH",
                std::env::temp_dir().join("terracotta-missing-easytier-core-service"),
            );
        }
        let service = RoomService::default();
        service
            .initialize_identity(zeroize::Zeroizing::new([3_u8; 32]))
            .await;
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
        let snapshot = service.status().await;
        assert_eq!(snapshot.state, RoomState::Faulted);
        assert_eq!(
            snapshot.error_code.as_deref(),
            Some("network.easytier-missing")
        );
        unsafe {
            std::env::remove_var("TERRACOTTA_EASYTIER_PATH");
        }
    }

    #[tokio::test]
    async fn invalid_backend_result_cannot_leave_state_stuck_joining() {
        let service = RoomService::new(Arc::new(InvalidJoinBackend));
        let error = service
            .join(JoinRoomRequest {
                room_code: "AB12-CD34-EF56".into(),
                game_session_id: None,
            })
            .await
            .unwrap_err();

        assert_eq!(error.code, "room.invalid-request");
        assert_eq!(service.status().await.state, RoomState::Faulted);
    }
}
