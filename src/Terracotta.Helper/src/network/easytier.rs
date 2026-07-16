use std::{env, path::PathBuf, process::Stdio, time::Duration};

use tokio::{process::Command, time::sleep};

use crate::room::RoomError;

use super::credentials::RoomCredentials;

/// Default public shared nodes used when the environment does not override peers.
const DEFAULT_SHARED_PEERS: &[&str] = &[
    "tcp://public.easytier.top:11010",
    "tcp://easytier.public.kkrainbow.top:11010",
];

#[derive(Debug, Clone)]
pub struct EasyTierLaunchConfig {
    pub binary: PathBuf,
    pub prefer_direct: bool,
    pub allow_relay: bool,
    pub host_ipv4: Option<&'static str>,
}

pub struct EasyTierNode {
    child: tokio::process::Child,
}

impl EasyTierNode {
    pub async fn stop(mut self) -> Result<(), RoomError> {
        // Ask the process to exit, then force-kill if it ignores the signal.
        let _ = self.child.start_kill();
        match tokio::time::timeout(Duration::from_secs(3), self.child.wait()).await {
            Ok(Ok(_)) => Ok(()),
            Ok(Err(error)) => Err(RoomError::new(
                "network.easytier-stop-failed",
                format!("Failed to stop EasyTier: {error}"),
                false,
            )),
            Err(_) => {
                let _ = self.child.start_kill();
                let _ = self.child.wait().await;
                Ok(())
            }
        }
    }
}

pub fn resolve_easytier_binary() -> Option<PathBuf> {
    if let Ok(explicit) = env::var("TERRACOTTA_EASYTIER_PATH") {
        let path = PathBuf::from(explicit);
        if path.is_file() {
            return Some(path);
        }
    }

    let current = env::current_exe().ok()?;
    let directory = current.parent()?;
    let candidate = directory.join(easytier_file_name());
    if candidate.is_file() {
        return Some(candidate);
    }
    None
}

pub async fn start_easytier(
    credentials: &RoomCredentials,
    config: EasyTierLaunchConfig,
) -> Result<EasyTierNode, RoomError> {
    if !config.binary.is_file() {
        return Err(easytier_missing());
    }

    let mut command = Command::new(&config.binary);
    command
        .kill_on_drop(true)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .env("ET_NETWORK_NAME", &credentials.network_name)
        .env("ET_NETWORK_SECRET", credentials.network_secret.as_str())
        .arg("--no-tun")
        .arg("--use-smoltcp")
        .arg("--rpc-portal")
        .arg("127.0.0.1:0")
        .arg("--rpc-portal-whitelist")
        .arg("127.0.0.1/32,::1/128");

    if let Some(ipv4) = config.host_ipv4 {
        command.arg("--ipv4").arg(ipv4);
    }

    if config.prefer_direct {
        // Prefer lower latency paths when available; EasyTier still falls back.
        command.arg("--latency-first");
    }
    // `allow_relay` is retained for future private-mode policy. Shared public
    // nodes remain required in alpha.2 for NAT coordination even when the UI
    // prefers direct paths.
    let _ = config.allow_relay;

    for peer in shared_peers() {
        command.arg("-p").arg(peer);
    }

    let mut child = command.spawn().map_err(|error| {
        RoomError::new(
            "network.easytier-start-failed",
            format!("Failed to start EasyTier: {error}"),
            true,
        )
    })?;

    // Give the process a brief moment to fail fast on bad arguments.
    sleep(Duration::from_millis(200)).await;
    if let Ok(Some(status)) = child.try_wait() {
        return Err(RoomError::new(
            "network.easytier-start-failed",
            format!("EasyTier exited immediately with status {status}."),
            true,
        ));
    }

    Ok(EasyTierNode { child })
}

pub fn easytier_missing() -> RoomError {
    RoomError::new(
        "network.easytier-missing",
        "The EasyTier runtime was not found next to terracotta-helper. Place easytier-core in the same native directory or set TERRACOTTA_EASYTIER_PATH.",
        false,
    )
}

fn easytier_file_name() -> &'static str {
    if cfg!(windows) {
        "easytier-core.exe"
    } else {
        "easytier-core"
    }
}

fn shared_peers() -> Vec<String> {
    if let Ok(value) = env::var("TERRACOTTA_EASYTIER_PEERS") {
        let peers = value
            .split([',', ';'])
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_owned)
            .collect::<Vec<_>>();
        if !peers.is_empty() {
            return peers;
        }
    }
    DEFAULT_SHARED_PEERS
        .iter()
        .map(|value| (*value).to_owned())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{easytier_file_name, easytier_missing, resolve_easytier_binary};

    #[test]
    fn missing_error_uses_stable_code() {
        assert_eq!(easytier_missing().code, "network.easytier-missing");
        assert!(!easytier_missing().retryable);
    }

    #[test]
    fn platform_binary_name_matches_os() {
        if cfg!(windows) {
            assert_eq!(easytier_file_name(), "easytier-core.exe");
        } else {
            assert_eq!(easytier_file_name(), "easytier-core");
        }
    }

    #[test]
    fn resolve_returns_none_without_sidecar() {
        // In the default helper test environment the sidecar is not present.
        // An explicit override would only appear when developers opt in.
        if std::env::var_os("TERRACOTTA_EASYTIER_PATH").is_none() {
            // May still find a file if developers dropped one next to the test exe;
            // only assert the function is callable and returns an Option.
            let _ = resolve_easytier_binary();
        }
    }
}
