use spake2::{Ed25519Group, Identity, Password, Spake2};
use std::{ptr, slice};

type State = Spake2<Ed25519Group>;

/// Caller supplies valid buffers; lengths are bounded before constructing slices.
#[no_mangle]
pub unsafe extern "C" fn lc_spake_start(
    role: u8, password: *const u8, password_len: usize,
    id_a: *const u8, id_a_len: usize, id_b: *const u8, id_b_len: usize,
    message_out: *mut u8,
) -> *mut State {
    if role > 1 || password.is_null() || id_a.is_null() || id_b.is_null()
        || message_out.is_null() || password_len != 8
        || id_a_len == 0 || id_b_len == 0 || id_a_len > 256 || id_b_len > 256 {
        return ptr::null_mut();
    }
    let p = slice::from_raw_parts(password, password_len);
    if !p.iter().all(u8::is_ascii_digit) { return ptr::null_mut(); }
    let password = Password::new(p);
    let a = Identity::new(slice::from_raw_parts(id_a, id_a_len));
    let b = Identity::new(slice::from_raw_parts(id_b, id_b_len));
    let (state, message) = if role == 0 {
        State::start_a(&password, &a, &b)
    } else {
        State::start_b(&password, &a, &b)
    };
    if message.len() != 33 { return ptr::null_mut(); }
    ptr::copy_nonoverlapping(message.as_ptr(), message_out, 33);
    Box::into_raw(Box::new(state))
}

#[no_mangle]
pub unsafe extern "C" fn lc_spake_finish(
    state: *mut State, peer_message: *const u8, peer_len: usize, key_out: *mut u8,
) -> i32 {
    if state.is_null() { return 0; }
    let state = Box::from_raw(state);
    if peer_message.is_null() || key_out.is_null() || peer_len != 33 { return 0; }
    match state.finish(slice::from_raw_parts(peer_message, peer_len)) {
        Ok(mut key) if key.len() == 32 => {
            ptr::copy_nonoverlapping(key.as_ptr(), key_out, 32);
            key.fill(0);
            1
        }
        _ => 0,
    }
}

#[no_mangle]
pub unsafe extern "C" fn lc_spake_destroy(state: *mut State) {
    if !state.is_null() { drop(Box::from_raw(state)); }
}

#[cfg(test)]
mod tests {
    use super::*;
    // Public synthetic code and identities, never used for real pairing.
    fn exchange(a_password: &[u8], b_password: &[u8], b_id: &[u8]) -> (Vec<u8>, Vec<u8>) {
        let a = Identity::new(b"LightClip/test/client");
        let b = Identity::new(b"LightClip/test/server/group");
        let (client, m_a) = State::start_a(&Password::new(a_password), &a, &b);
        let (server, m_b) = State::start_b(&Password::new(b_password), &a, &Identity::new(b_id));
        (client.finish(&m_b).unwrap(), server.finish(&m_a).unwrap())
    }
    #[test]
    fn correct_code_agrees() {
        let (a,b) = exchange(b"12345678", b"12345678", b"LightClip/test/server/group");
        assert_eq!(a.len(),32); assert_eq!(a,b);
    }
    #[test]
    fn wrong_code_cannot_confirm() {
        let (a,b) = exchange(b"12345678", b"12345679", b"LightClip/test/server/group");
        assert!(a != b);
    }
    #[test]
    fn identities_bind_the_exchange() {
        let (a,b) = exchange(b"12345678", b"12345678", b"another/server/group");
        assert!(a != b);
    }
    #[test]
    fn invalid_role_or_message_is_rejected() {
        let a = Identity::new(b"a"); let b = Identity::new(b"b");
        let (state, _) = State::start_a(&Password::new(b"12345678"), &a, &b);
        assert!(state.finish(&[0;33]).is_err());
    }
}
