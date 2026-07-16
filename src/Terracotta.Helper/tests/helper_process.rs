use std::{
    io::{Read, Write},
    path::Path,
    process::{Command, Stdio},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use serde_json::json;
use terracotta_helper::protocol::{
    Envelope, PROTOCOL_VERSION,
    framing::{read_frame, write_frame},
};

#[cfg(windows)]
type TestStream = tokio::net::windows::named_pipe::NamedPipeClient;
#[cfg(unix)]
type TestStream = tokio::net::UnixStream;

#[tokio::test]
async fn real_helper_handshakes_reports_status_and_exits_after_shutdown() {
    let temporary = test_directory();
    let nonce = nonce();
    let endpoint = endpoint(temporary.path(), &nonce);
    let token = "a".repeat(64);
    let mut child = Command::new(env!("CARGO_BIN_EXE_terracotta-helper"))
        .args([
            "--ipc",
            &endpoint,
            "--parent-pid",
            &std::process::id().to_string(),
            "--data-dir",
            temporary.path().join("data").to_str().unwrap(),
            "--log-dir",
            temporary.path().join("logs").to_str().unwrap(),
            "--protocol-version",
            "1",
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(token.as_bytes())
        .unwrap();

    let mut stream = connect_with_retry(&endpoint).await;
    write_frame(
        &mut stream,
        &request(
            "hello-1",
            "hello",
            json!({
                "authToken": token,
                "client": "pcln",
                "clientVersion": "integration-test"
            }),
        ),
    )
    .await
    .unwrap();
    assert_eq!(
        read_with_timeout(&mut stream, "hello").await.message_type,
        "hello.accepted"
    );

    write_frame(&mut stream, &request("status-1", "room.status", json!({})))
        .await
        .unwrap();
    let status = read_with_timeout(&mut stream, "status").await;
    assert_eq!(status.message_type, "room.status.result");
    assert_eq!(status.payload["state"], "idle");

    write_frame(&mut stream, &request("shutdown-1", "shutdown", json!({})))
        .await
        .unwrap();
    assert_eq!(
        read_with_timeout(&mut stream, "shutdown")
            .await
            .message_type,
        "shutdown.accepted"
    );
    drop(stream);

    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if tokio::time::Instant::now() >= deadline {
            child.kill().unwrap();
            panic!("Helper did not exit after shutdown");
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    };
    let mut stderr = String::new();
    child
        .stderr
        .take()
        .unwrap()
        .read_to_string(&mut stderr)
        .unwrap();
    assert!(status.success(), "Helper failed: {stderr}");
}

async fn read_with_timeout(stream: &mut TestStream, stage: &str) -> Envelope {
    tokio::time::timeout(Duration::from_secs(5), read_frame(stream))
        .await
        .unwrap_or_else(|_| panic!("Helper timed out during {stage}"))
        .unwrap()
}

fn request(id: &str, message_type: &str, payload: serde_json::Value) -> Envelope {
    Envelope {
        protocol: PROTOCOL_VERSION,
        id: id.into(),
        message_type: message_type.into(),
        payload,
    }
}

fn nonce() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    format!("{nanos:032x}")
}

fn test_directory() -> tempfile::TempDir {
    #[cfg(target_os = "macos")]
    let base = Path::new("/tmp");
    #[cfg(not(target_os = "macos"))]
    let base = std::env::temp_dir();

    tempfile::Builder::new()
        .prefix("tc-")
        .tempdir_in(base)
        .unwrap()
}

#[cfg(windows)]
fn endpoint(_temporary: &Path, nonce: &str) -> String {
    format!(r"\\.\pipe\pcln-terracotta-{nonce}")
}

#[cfg(unix)]
fn endpoint(temporary: &Path, nonce: &str) -> String {
    temporary
        .join(format!("terracotta-{nonce}.sock"))
        .to_string_lossy()
        .into_owned()
}

#[cfg(windows)]
async fn connect_with_retry(endpoint: &str) -> TestStream {
    use tokio::net::windows::named_pipe::ClientOptions;

    let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        match ClientOptions::new().open(endpoint) {
            Ok(stream) => return stream,
            Err(error) if tokio::time::Instant::now() < deadline => {
                let _ = error;
                tokio::time::sleep(Duration::from_millis(40)).await;
            }
            Err(error) => panic!("failed to connect to Helper named pipe: {error}"),
        }
    }
}

#[cfg(unix)]
async fn connect_with_retry(endpoint: &str) -> TestStream {
    let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
    loop {
        match tokio::net::UnixStream::connect(endpoint).await {
            Ok(stream) => return stream,
            Err(error) if tokio::time::Instant::now() < deadline => {
                let _ = error;
                tokio::time::sleep(Duration::from_millis(40)).await;
            }
            Err(error) => panic!("failed to connect to Helper Unix socket: {error}"),
        }
    }
}
