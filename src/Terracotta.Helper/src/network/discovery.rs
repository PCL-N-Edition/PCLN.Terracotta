use std::{
    fs,
    net::SocketAddr,
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};

use crate::room::RoomError;

use super::credentials::compact_room_code;

const MAX_AGE: Duration = Duration::from_secs(6 * 60 * 60);

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RoomEndpointAdvertisement {
    pub room_code: String,
    pub scaffolding: String,
    pub minecraft: String,
    pub published_unix_seconds: u64,
}

impl RoomEndpointAdvertisement {
    pub fn scaffolding_addr(&self) -> Result<SocketAddr, RoomError> {
        parse_loopback(&self.scaffolding)
    }

    pub fn minecraft_addr(&self) -> Result<SocketAddr, RoomError> {
        parse_loopback(&self.minecraft)
    }
}

pub fn publish_local_endpoint(
    advertisement: &RoomEndpointAdvertisement,
) -> Result<PathBuf, RoomError> {
    let path = advertisement_path(&advertisement.room_code)?;
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(io_error)?;
        restrict_directory(parent)?;
    }
    let body = serde_json::to_vec_pretty(advertisement).map_err(|error| {
        RoomError::new(
            "network.discovery-encode-failed",
            format!("Failed to encode room advertisement: {error}"),
            false,
        )
    })?;
    fs::write(&path, body).map_err(io_error)?;
    restrict_file(&path)?;
    Ok(path)
}

pub fn load_local_endpoint(
    room_code: &str,
) -> Result<Option<RoomEndpointAdvertisement>, RoomError> {
    let path = advertisement_path(room_code)?;
    if !path.is_file() {
        return Ok(None);
    }
    let body = fs::read(&path).map_err(io_error)?;
    let advertisement: RoomEndpointAdvertisement = serde_json::from_slice(&body).map_err(|_| {
        RoomError::new(
            "network.discovery-invalid",
            "The local room advertisement is invalid.",
            false,
        )
    })?;
    let compact_expected = compact_room_code(room_code);
    let compact_actual = compact_room_code(&advertisement.room_code);
    if compact_expected != compact_actual {
        return Err(RoomError::new(
            "network.discovery-invalid",
            "The local room advertisement does not match the requested room code.",
            false,
        ));
    }
    advertisement.scaffolding_addr()?;
    advertisement.minecraft_addr()?;
    if advertisement_is_stale(advertisement.published_unix_seconds) {
        let _ = fs::remove_file(&path);
        return Ok(None);
    }
    Ok(Some(advertisement))
}

pub fn clear_local_endpoint(room_code: &str) -> Result<(), RoomError> {
    let path = advertisement_path(room_code)?;
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(io_error(error)),
    }
}

pub fn now_unix_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or(0)
}

fn advertisement_path(room_code: &str) -> Result<PathBuf, RoomError> {
    let compact = compact_room_code(room_code);
    if compact.len() != 12 || !compact.bytes().all(|byte| byte.is_ascii_alphanumeric()) {
        return Err(RoomError::new(
            "room.invalid-code",
            "The room code must contain three groups of four ASCII letters or digits.",
            false,
        ));
    }
    Ok(discovery_root()?.join(format!("{compact}.json")))
}

fn discovery_root() -> Result<PathBuf, RoomError> {
    if let Ok(explicit) = std::env::var("TERRACOTTA_DISCOVERY_DIR") {
        return Ok(PathBuf::from(explicit));
    }
    let temp = std::env::temp_dir().join("pcln-terracotta").join("rooms");
    Ok(temp)
}

fn advertisement_is_stale(published_unix_seconds: u64) -> bool {
    let now = now_unix_seconds();
    now.saturating_sub(published_unix_seconds) > MAX_AGE.as_secs()
}

fn parse_loopback(value: &str) -> Result<SocketAddr, RoomError> {
    let address = value.parse::<SocketAddr>().map_err(|_| {
        RoomError::new(
            "network.discovery-invalid",
            "The local room advertisement contains an invalid endpoint.",
            false,
        )
    })?;
    if !address.ip().is_loopback() || address.port() == 0 {
        return Err(RoomError::new(
            "network.discovery-invalid",
            "The local room advertisement must use a loopback endpoint.",
            false,
        ));
    }
    Ok(address)
}

fn io_error(error: std::io::Error) -> RoomError {
    RoomError::new(
        "network.discovery-io",
        format!("Local room discovery failed: {error}"),
        true,
    )
}

#[cfg(unix)]
fn restrict_directory(path: &Path) -> Result<(), RoomError> {
    use std::os::unix::fs::PermissionsExt;
    let permissions = fs::Permissions::from_mode(0o700);
    fs::set_permissions(path, permissions).map_err(io_error)
}

#[cfg(unix)]
fn restrict_file(path: &Path) -> Result<(), RoomError> {
    use std::os::unix::fs::PermissionsExt;
    let permissions = fs::Permissions::from_mode(0o600);
    fs::set_permissions(path, permissions).map_err(io_error)
}

#[cfg(windows)]
fn restrict_directory(_path: &Path) -> Result<(), RoomError> {
    Ok(())
}

#[cfg(windows)]
fn restrict_file(_path: &Path) -> Result<(), RoomError> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::{
        RoomEndpointAdvertisement, clear_local_endpoint, load_local_endpoint,
        publish_local_endpoint,
    };

    #[test]
    fn publish_and_load_round_trip() {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!("terracotta-discovery-test-{suffix}"));
        // SAFETY: test-only environment isolation for discovery root.
        unsafe {
            std::env::set_var("TERRACOTTA_DISCOVERY_DIR", &root);
        }

        let advertisement = RoomEndpointAdvertisement {
            room_code: "AB12-CD34-EF56".into(),
            scaffolding: "127.0.0.1:41234".into(),
            minecraft: "127.0.0.1:25565".into(),
            published_unix_seconds: super::now_unix_seconds(),
        };
        publish_local_endpoint(&advertisement).unwrap();
        let loaded = load_local_endpoint("ab12 cd34 ef56").unwrap().unwrap();
        assert_eq!(loaded.scaffolding, advertisement.scaffolding);
        assert_eq!(loaded.minecraft, advertisement.minecraft);
        clear_local_endpoint("AB12-CD34-EF56").unwrap();
        assert!(load_local_endpoint("AB12-CD34-EF56").unwrap().is_none());

        let _ = std::fs::remove_dir_all(root);
        unsafe {
            std::env::remove_var("TERRACOTTA_DISCOVERY_DIR");
        }
    }
}
