pub mod bootstrap;
pub mod cli;
pub mod error;
pub mod ipc;
pub mod network;
pub mod platform;
pub mod protocol;
pub mod room;
pub mod scaffolding;

use bootstrap::SecretToken;
use cli::ValidatedArgs;
use error::HelperError;
use ipc::{LocalIpcListener, session::SessionOutcome};
use platform::ParentGuard;
use room::RoomService;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExitReason {
    ShutdownRequested,
    PeerDisconnected,
    ParentExited,
    Interrupted,
}

pub async fn run(args: ValidatedArgs, secret: SecretToken) -> Result<ExitReason, HelperError> {
    std::fs::create_dir_all(&args.data_dir)?;
    std::fs::create_dir_all(&args.log_dir)?;

    let parent = ParentGuard::attach(args.parent_pid)?;
    let mut listener = LocalIpcListener::bind(&args)?;
    let mut stream = tokio::time::timeout(std::time::Duration::from_secs(10), listener.accept())
        .await
        .map_err(|_| {
            HelperError::Ipc(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "IPC client connection timed out",
            ))
        })??;
    let room = RoomService::default();

    tokio::select! {
        outcome = ipc::session::run(&mut stream, &secret, &room) => {
            match outcome? {
                SessionOutcome::ShutdownRequested => Ok(ExitReason::ShutdownRequested),
                SessionOutcome::PeerDisconnected => Ok(ExitReason::PeerDisconnected),
            }
        }
        result = parent.wait_for_exit() => {
            result?;
            Ok(ExitReason::ParentExited)
        }
        result = tokio::signal::ctrl_c() => {
            result?;
            Ok(ExitReason::Interrupted)
        }
    }
}
