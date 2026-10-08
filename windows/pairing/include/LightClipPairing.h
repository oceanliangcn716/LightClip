#ifndef LIGHTCLIP_PAIRING_H
#define LIGHTCLIP_PAIRING_H
#include <stdint.h>
#include <stddef.h>
// role 0: joining client (A); role 1: inviting group member (B).
// The 33-byte message is public. Password and derived key stay in process memory.
void *lc_spake_start(uint8_t role, const uint8_t *password, size_t password_len,
                     const uint8_t *id_a, size_t id_a_len,
                     const uint8_t *id_b, size_t id_b_len, uint8_t *message_out);
// Consumes state on every call; returns 1 for a valid peer message, else 0.
int32_t lc_spake_finish(void *state, const uint8_t *peer_message, size_t peer_len,
                       uint8_t *key_out);
void lc_spake_destroy(void *state);
#endif
