# DUNA/1: Unity Demo Arena application records

Schema UUID: `b2908d48-f766-4c78-aac7-e312513ea701`. Record size: **128 bytes**. Integer and IEEE-754 float fields are big-endian. Unused/reserved bytes must be zero. This schema is application-owned; DHMP V1 remains headerless. Every complete packet is validated before any records are published.

| Offset | Bytes | Application field |
| --- | --- | --- |
| 0 | 1 | Kind: Join=1, Welcome=2, Input=3, State=4, Leave=5 |
| 1 | 1 | Application profile version, 1 |
| 2 | 1 | Flags: bit 0 jump, bit 1 grounded; other bits zero |
| 3 | 1 | Reserved zero |
| 4 | 4 | Server-assigned player ID |
| 8 | 4 | Input sequence or server snapshot tick |
| 12 | 4 | Last applied input sequence, in state snapshots |
| 16 | 8 | Recipient's server-issued application session token |
| 24 | 8 | Client join nonce |
| 32 | 12 | X/Y/Z feet position |
| 44 | 4 | Vertical velocity |
| 48 | 4 | Yaw in degrees, 0 inclusive to 360 exclusive |
| 52 | 8 | World-relative movement X/Z, each -1 to 1 |
| 60 | 4 | Client monotonic milliseconds, echoed for input-to-snapshot timing |
| 64 | 1 | UTF-8 display name byte length, 0 to 32 |
| 65 | 32 | Name bytes, followed by zero padding |
| 97 | 31 | Reserved zero |

The client first sends the existing 32-byte Control V1 HELLO on binding 254, using the UUID and record size above. No data is admitted before a compatible HELLO. The client accepts only a response from its configured server with its outstanding correlation. The server clamps outbound packets to the advertised peer receive ceiling, rounded down to whole records. Control is a one-shot compatibility attempt with a total ten-second client deadline.

After ACCEPT, Join application records are repeated at most twice per second until Welcome or the deadline. The server makes repeated joins with the same source/nonce idempotent. A different join nonce cannot replace an active source address. Welcome selects a fresh player ID and session token. The token plus join nonce rejects stale records from a previous application session. It does not authenticate the peer and is visible to observers in this plaintext profile.

Each input is numbered by the application. Packets carry up to the latest three pending complete input records; this bounded redundancy and the applied-input field are application behavior, not protocol-level reliable delivery. The server validates session/source/player identity, accepts at most 32 queued inputs, ignores duplicates and old/far-future sequence numbers, and advances only on its own 30 Hz clock. It uses input direction/jump/yaw, never a client position.

Snapshots at 10 Hz include all active players and are split into packets of at most 1152 bytes. They use sequential record publication, then per-player application generations to discard stale snapshots. Applying packet-local Latest to a multi-entity batch would incorrectly discard other players. Sequence comparison uses signed modular distance with a bounded forward window.

The client replays at most 128 pending inputs over the received authoritative motor state. The timestamp display measures input-to-snapshot response, including server tick/snapshot scheduling, not a dedicated network ping. Byte counters report actual submitted/received IP payload bytes, including compatibility messages but excluding the IPv6 and link headers.

Leave is best effort. The server retires inactive peers after ten seconds. Remote avatars disappear after two seconds without a newer state; later state can recreate an avatar after temporary loss. Duplicate/delayed packets do not reset these lifetimes unless they advance valid application state.

The supplied map consists of a floor, bounded play area and four solid axis-aligned platforms. `ArenaMotor` is the authority for collision; the Unity scene draws that same geometry. Arbitrary Unity scenes, player-to-player physics and Rigidbody prediction require an application-specific motor/schema extension.
