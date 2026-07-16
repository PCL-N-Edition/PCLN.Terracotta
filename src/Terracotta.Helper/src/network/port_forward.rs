use std::{
    io,
    net::SocketAddr,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
};

use tokio::{
    io::copy_bidirectional,
    net::{TcpListener, TcpStream},
    sync::watch,
    task::JoinHandle,
};

/// Loopback TCP forwarder used by members to expose remote Minecraft/Scaffolding
/// services on `127.0.0.1`.
pub struct PortForward {
    local_addr: SocketAddr,
    active_connections: Arc<AtomicUsize>,
    shutdown: watch::Sender<bool>,
    task: JoinHandle<()>,
}

impl PortForward {
    pub async fn start(target: SocketAddr) -> io::Result<Self> {
        let listener = TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0))).await?;
        let local_addr = listener.local_addr()?;
        let (shutdown, shutdown_rx) = watch::channel(false);
        let active_connections = Arc::new(AtomicUsize::new(0));
        let task = tokio::spawn(run_listener(
            listener,
            target,
            shutdown_rx,
            Arc::clone(&active_connections),
        ));
        Ok(Self {
            local_addr,
            active_connections,
            shutdown,
            task,
        })
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.local_addr
    }

    pub fn active_connections(&self) -> usize {
        self.active_connections.load(Ordering::Relaxed)
    }

    pub async fn stop(self) {
        let _ = self.shutdown.send(true);
        self.task.abort();
        let _ = self.task.await;
    }
}

async fn run_listener(
    listener: TcpListener,
    target: SocketAddr,
    mut shutdown: watch::Receiver<bool>,
    active_connections: Arc<AtomicUsize>,
) {
    loop {
        tokio::select! {
            accepted = listener.accept() => {
                let Ok((inbound, _)) = accepted else {
                    break;
                };
                let active = Arc::clone(&active_connections);
                tokio::spawn(async move {
                    active.fetch_add(1, Ordering::Relaxed);
                    if let Ok(mut outbound) = TcpStream::connect(target).await {
                        let mut inbound = inbound;
                        let _ = copy_bidirectional(&mut inbound, &mut outbound).await;
                    }
                    active.fetch_sub(1, Ordering::Relaxed);
                });
            }
            changed = shutdown.changed() => {
                if changed.is_err() || *shutdown.borrow() {
                    break;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use tokio::{
        io::{AsyncReadExt, AsyncWriteExt},
        net::TcpListener,
    };

    use super::PortForward;

    #[tokio::test]
    async fn forwards_bytes_through_loopback() {
        let upstream = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let upstream_addr = upstream.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut stream, _) = upstream.accept().await.unwrap();
            let mut buffer = [0_u8; 4];
            stream.read_exact(&mut buffer).await.unwrap();
            assert_eq!(&buffer, b"ping");
            stream.write_all(b"pong").await.unwrap();
        });

        let forward = PortForward::start(upstream_addr).await.unwrap();
        let mut client = tokio::net::TcpStream::connect(forward.local_addr())
            .await
            .unwrap();
        client.write_all(b"ping").await.unwrap();
        let mut buffer = [0_u8; 4];
        client.read_exact(&mut buffer).await.unwrap();
        assert_eq!(&buffer, b"pong");
        forward.stop().await;
        server.await.unwrap();
    }
}
