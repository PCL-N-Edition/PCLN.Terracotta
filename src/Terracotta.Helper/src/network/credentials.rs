use hmac::{Hmac, Mac};
use sha2::{Digest, Sha256};
use zeroize::{Zeroize, ZeroizeOnDrop, Zeroizing};

use crate::room::RoomError;

type HmacSha256 = Hmac<Sha256>;

/// Protocol-level root used only to bind a room code to EasyTier credentials.
/// Members derive the same network name/secret from the room code alone.
const ROOM_SECRET_ROOT: &[u8] = b"cn.pcln.terracotta.room.v1";
const ROOM_CODE_ALPHABET: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

#[derive(Clone, Zeroize, ZeroizeOnDrop)]
pub struct RoomCredentials {
    pub room_code: String,
    pub network_name: String,
    pub network_secret: Zeroizing<String>,
}

impl RoomCredentials {
    pub fn from_room_code(room_code: &str) -> Result<Self, RoomError> {
        let room_code = normalize_room_code(room_code)?;
        let compact = compact_room_code(&room_code);
        let network_name = network_name_for(&compact);
        let network_secret = network_secret_for(&compact);
        Ok(Self {
            room_code,
            network_name,
            network_secret,
        })
    }

    pub fn generate() -> Result<Self, RoomError> {
        let mut material = [0_u8; 12];
        getrandom_fill(&mut material)?;
        let mut compact = String::with_capacity(12);
        for byte in material {
            compact.push(ROOM_CODE_ALPHABET[(byte as usize) % ROOM_CODE_ALPHABET.len()] as char);
        }
        Self::from_room_code(&format_room_code(&compact))
    }
}

pub fn normalize_room_code(value: &str) -> Result<String, RoomError> {
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
    Ok(format_room_code(&compact))
}

pub fn compact_room_code(room_code: &str) -> String {
    room_code
        .chars()
        .filter(|character| !matches!(character, '-' | ' '))
        .map(|character| character.to_ascii_uppercase())
        .collect()
}

pub fn format_room_code(compact: &str) -> String {
    format!("{}-{}-{}", &compact[0..4], &compact[4..8], &compact[8..12])
}

pub fn machine_id_from_identity(identity: &[u8; 32]) -> String {
    let digest = Sha256::digest(identity);
    hex_encode(&digest[..16])
}

fn network_name_for(compact: &str) -> String {
    let digest = Sha256::digest(compact.as_bytes());
    let mut encoded = String::with_capacity(16);
    // Crockford-like base32 without padding, first 10 chars after prefix.
    const ALPHABET: &[u8] = b"abcdefghijklmnopqrstuvwxyz234567";
    let mut bits = 0_u32;
    let mut value = 0_u32;
    for byte in digest.iter().take(8) {
        value = (value << 8) | u32::from(*byte);
        bits += 8;
        while bits >= 5 {
            bits -= 5;
            let index = ((value >> bits) & 0x1f) as usize;
            encoded.push(ALPHABET[index] as char);
        }
    }
    if bits > 0 {
        let index = ((value << (5 - bits)) & 0x1f) as usize;
        encoded.push(ALPHABET[index] as char);
    }
    format!("tc-{}", &encoded[..encoded.len().min(10)])
}

fn network_secret_for(compact: &str) -> Zeroizing<String> {
    let mut mac =
        HmacSha256::new_from_slice(ROOM_SECRET_ROOT).expect("HMAC accepts any key length");
    mac.update(compact.as_bytes());
    let result = mac.finalize().into_bytes();
    Zeroizing::new(hex_encode(&result))
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(HEX[(byte >> 4) as usize] as char);
        output.push(HEX[(byte & 0x0f) as usize] as char);
    }
    output
}

fn getrandom_fill(buffer: &mut [u8]) -> Result<(), RoomError> {
    // Prefer OS CSPRNG without adding a standalone dependency.
    fill_random(buffer).map_err(|_| {
        RoomError::new(
            "room.entropy-unavailable",
            "The system random number generator is unavailable.",
            true,
        )
    })
}

#[cfg(windows)]
fn fill_random(buffer: &mut [u8]) -> std::io::Result<()> {
    use std::ptr;

    #[link(name = "bcrypt")]
    unsafe extern "system" {
        fn BCryptGenRandom(
            algorithm: *mut core::ffi::c_void,
            buffer: *mut u8,
            size: u32,
            flags: u32,
        ) -> i32;
    }

    const BCRYPT_USE_SYSTEM_PREFERRED_RNG: u32 = 0x0000_0002;
    let status = unsafe {
        BCryptGenRandom(
            ptr::null_mut(),
            buffer.as_mut_ptr(),
            buffer.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status == 0 {
        Ok(())
    } else {
        Err(std::io::Error::other(format!(
            "BCryptGenRandom failed with status {status}"
        )))
    }
}

#[cfg(unix)]
fn fill_random(buffer: &mut [u8]) -> std::io::Result<()> {
    use std::{fs::File, io::Read};

    File::open("/dev/urandom")?.read_exact(buffer)
}

#[cfg(test)]
mod tests {
    use super::{
        RoomCredentials, compact_room_code, machine_id_from_identity, normalize_room_code,
    };

    #[test]
    fn normalize_accepts_spaced_and_lower_codes() {
        assert_eq!(
            normalize_room_code("ab12 cd34-ef56").unwrap(),
            "AB12-CD34-EF56"
        );
    }

    #[test]
    fn normalize_rejects_invalid_codes() {
        assert_eq!(
            normalize_room_code("short").unwrap_err().code,
            "room.invalid-code"
        );
        assert_eq!(
            normalize_room_code("!!!!-!!!!-!!!!").unwrap_err().code,
            "room.invalid-code"
        );
    }

    #[test]
    fn credentials_are_stable_for_same_room_code() {
        let left = RoomCredentials::from_room_code("AB12-CD34-EF56").unwrap();
        let right = RoomCredentials::from_room_code("ab12 cd34 ef56").unwrap();
        assert_eq!(left.room_code, right.room_code);
        assert_eq!(left.network_name, right.network_name);
        assert_eq!(left.network_secret.as_str(), right.network_secret.as_str());
        assert!(left.network_name.starts_with("tc-"));
        assert_eq!(left.network_secret.len(), 64);
        assert_eq!(compact_room_code(&left.room_code).len(), 12);
    }

    #[test]
    fn generated_codes_round_trip() {
        let generated = RoomCredentials::generate().unwrap();
        let restored = RoomCredentials::from_room_code(&generated.room_code).unwrap();
        assert_eq!(generated.room_code, restored.room_code);
        assert_eq!(
            generated.network_secret.as_str(),
            restored.network_secret.as_str()
        );
    }

    #[test]
    fn machine_id_is_stable_hex() {
        let id = machine_id_from_identity(&[7_u8; 32]);
        assert_eq!(id.len(), 32);
        assert!(id.chars().all(|character| character.is_ascii_hexdigit()));
        assert_eq!(id, machine_id_from_identity(&[7_u8; 32]));
    }
}
