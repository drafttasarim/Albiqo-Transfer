# Albiqo Transfer protocol 1

Frozen: 14 November 2026. Both peers use explicit foreground transfer sessions.

## Access and catalogue

The iPhone generates a fresh six-digit PIN for each session. The PC user enters it. It is held only in memory, never logged or persisted. The iPhone rate-limits failed authentication and stops the session when backgrounded. USB requires Apple's mobile-device USB driver/service and the iPhone's normal Trust approval; the Apple Devices application UI is not part of the transfer flow.

Only ZIP archives deliberately prepared/shared by the user are in the catalogue (maximum 32); no arbitrary path access, media deletion or remote filesystem writes. Metadata JSON is UTF-8, camelCase:

```json
{"protocolVersion":1,"archives":[{"id":"opaque-id","albumName":"Tatiller","fileName":"Tatiller.zip","size":12345,"sha256":"64 lowercase hexadecimal characters","createdAt":"2026-11-14T12:00:00Z"}]}
```

Authentication response: `{"protocolVersion":1,"deviceName":"iPhone","sessionId":"opaque","expiresAt":"ISO8601"}`. Errors: `{"code":"unauthorized","message":"..."}`. The recipient validates sizes, names, IDs and SHA-256 before using metadata.

## USB

The PC talks to the installed Apple usbmux service at `127.0.0.1:27015`. It first tries the plist usbmux protocol: 16-byte little-endian header (length, version=1, message=8, tag), `ListDevices`, then `Connect` with DeviceID and network-byte-order PortNumber **52783**. Older services that reject `ListDevices` are enumerated through `Listen` and Attached/Detached events. A BADVERSION result negotiates the legacy native v0 `Listen`/`Connect` protocol; Connect negotiates independently. Device IDs may be in `Properties` or the outer record. Only explicit plist ConnectionType `USB` or implicit native-v0 USB records are offered; network devices are excluded. Command replies, partial packets and connection setup have bounded waits. After successful Connect the socket is an HTTP tunnel to a loopback-only TCP listener inside Albiqo. This host-service compatibility does not change the Albiqo application protocol version.

All HTTP requests include `Authorization: Bearer <PIN>`. No secrets in URLs. Routes:

- `GET /v1/session` → auth response.
- `GET /v1/archives` → catalogue.
- `GET /v1/archives/{percent-encoded-id}` with `Range: bytes=start-end` → status206, Content-Range and exactly the requested file bytes. The PC requests up to4MiB per USB range. The iPhone streams each range in64KiB sends with backpressure; the PC buffers at most4MiB. BLE remains limited to64KiB per get.

Error statuses include401,403,404,409,416,429. The PC never falls back to unauthenticated LAN access.

## Bluetooth LE

- Service: `A4B10000-7E7A-4AD8-9ED8-4AB1C0A0B100`
- RX, encrypted Write with Response: `A4B10001-7E7A-4AD8-9ED8-4AB1C0A0B100`
- TX, encrypted Notify: `A4B10002-7E7A-4AD8-9ED8-4AB1C0A0B100`

The PC pairs through Windows before subscribing/writing. Commands are serialized. RX command JSON is at most512 bytes. Each ATT write has a5-byte header: UInt32LE requestId, UInt8 flags (`1=start`, `2=end`), followed by part of the JSON. Fragment payload capacity is `MaxPduSize - 3 - 5` (minimum15 at ATT MTU23). A start resets that command's accumulator; end executes it. Every fragment and JSON `id` must match.

Commands:

```json
{"id":1,"op":"auth","pin":"123456"}
{"id":2,"op":"list"}
{"id":3,"op":"get","archiveId":"opaque-id","offset":0,"length":16384}
```

Get length is1…65536. TX notification header is14 bytes: UInt8 kind (`1=JSON`, `2=file bytes`, `3=error JSON`), UInt32LE requestId, UInt64LE offset, UInt8 final (`0=more`, `1=end of response`), then payload. JSON/error offsets are byte offsets in the full JSON; file offsets are absolute archive byte offsets. Notifications must be contiguous, bounded by the requested length and same kind/id. Empty final JSON or truncated transfer is invalid. The server respects CoreBluetooth updateValue backpressure. A reply has an absolute600s deadline and a120s no-progress limit; an independent timer clears stuck sends. The Windows client chooses512…16384-byte ranges from its MTU so a minimum-MTU link does not require a16KiB burst. The PC uses one range at a time, retries timed-out ranges with a new requestId and discards late responses for older IDs. No entire ZIP is buffered in RAM. Bluetooth is deliberately offered for modest transfers; large albums are faster over USB.

## Saving, verification and resume

PC downloads into an adjacent `.partial` file and a metadata sidecar. Resume requires identical archive ID, size and SHA-256; stale sidecars do not authorize overwriting unrelated files. Final size and SHA-256 must match before moving atomically to a unique `.zip` name. Existing final files are never overwritten. On cancellation/error partial data remains resumable. If SHA-256 fails, only the generated resume sidecar is invalidated; suspect partial bytes remain and Retry downloads a fresh unique copy. The album-named folder is inside the ZIP; no automatic extraction of an untrusted archive. Disconnect, cancellation and closing dispose transports and clear the PIN. Neither transfer path deletes originals.
