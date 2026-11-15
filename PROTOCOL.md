# Albiqo Transfer protocol 2 — direct media

Frozen: 14 November 2026. Albiqo iOS 1.0.3 (13) and Windows Transfer 0.2.0-preview introduce protocol2; protocol1 remains available only for older prepared-archive sessions.

The iPhone exposes only the album explicitly selected for foreground PC Transfer. The temporary six-digit PIN is held in memory. Authentication, Apple USB trust/driver prerequisites, encrypted BLE pairing, service UUIDs, RX5/TX14 framing and request serialization are unchanged from protocol1 below. There are no filesystem-path, remote-write or phone-media deletion operations. Session closure clears access. Old Windows clients must update when the phone advertises protocolVersion2.

## Session, catalogue and files

All HTTP requests use `Authorization: Bearer <PIN>`, never a URL secret. JSON is UTF-8/camelCase. Nullable Swift fields may be omitted; Windows accepts omitted optional fields as null.

- `GET /v1/session`, or BLE `{id,op:"auth",pin}` → `{protocolVersion:2,deviceName,sessionId,expiresAt}`.
- `GET /v2/catalogue`, or BLE `{id,op:"catalog"}` → `{protocolVersion:2,albums:[{id,name,assetCount,fileCount}]}`.
- `GET /v2/albums/{albumId}/files/{cursor}`, or BLE `{id,op:"files",albumId,cursor}` → `{protocolVersion:2,albumId,files:[{id,albumId,fileName,version,createdAt}],nextCursor:int|null}`.

IDs are 32 lowercase hex characters. `version` is a stable 32- or 64-hex source fingerprint, **not a precomputed file-content digest**. `createdAt` is ISO8601. Listing metadata does not require reading all originals. Cursor starts at0, each page contains at most32 resources and the optional next cursor increases. A Live Photo or edited asset may have more than one original resource, so fileCount may exceed assetCount. The PC validates metadata and duplicate IDs before saving. Filenames may duplicate; each resource is retained.

## Bounded source chunks

`GET /v2/files/{fileId}/chunk/{offset}/{length}`, or BLE `{id,op:"chunk",fileId,offset,length}` returns:

```json
{"offset":0,"data":"base64 bytes","eof":false}
```

Final successful response:

```json
{"offset":262144,"data":"base64 bytes, possibly empty","eof":true,"totalSize":300000,"sha256":"64 lowercase hexadecimal characters"}
```

`offset` is the requested absolute file byte offset. USB length is1…262144 bytes with a512KiB JSON cap. BLE length is1…16384 bytes with a32KiB JSON cap; Windows selects512…16384 from the negotiated MTU. V2 replies are TX kind1 JSON with framing offsets into the JSON starting at0; the JSON object's offset remains the absolute media offset. V1 get replies remain kind2 binary. RX commands stay at most512 bytes. HTTP paths have no query strings.

Nonfinal responses must contain bytes. EOF may contain a short or empty final block but only follows successful source completion and must include the actual full size and full source SHA-256. Missing EOF proof, wrong offset, premature EOF, excessive bytes or a mismatched hash never publishes a complete PC file. A request may wait for iCloud source preparation: USB v2 chunks and BLE v2 chunk replies have a600-second bound; caller cancellation interrupts the wait. Control requests retain shorter bounds. The server has a600-second reply limit and120-second no-progress limit, with Photos progress extending source activity as applicable.

The iPhone reads one original resource sequentially through a bounded producer/consumer buffer, retains the last chunk for idempotent retry and does not start a new original read for each block. On genuine resume it reopens once, discards and hashes the prefix, and includes that prefix in the final digest. No whole source prehash, whole-album ZIP or Albiqo media staging file is required. Photos/iCloud's own downloads and cache may still consume system storage.

## PC save and reconnect semantics

The selected destination receives an album-named folder (Windows-invalid characters are sanitized). Existing unrelated folders are not adopted. Matching generated album metadata reconnects to the same folder. Original filename extensions and all listed resource components are retained; conflicting names receive unique suffixes. Existing files are never overwritten. Destination directories, resource paths and metadata paths reject symlinks/reparse points.

PC-only state lives in `.albiqo-transfer` inside the album folder: an album marker, per-resource `.pending.json`, unique `.partial`, completed `.complete.json` receipts and a transfer lock. Checkpoint identity includes source file ID, album ID, resource version and original filename. Resume re-hashes retained PC bytes before appending, then compares the complete local size/SHA against the phone's successful EOF proof. Only then is the file moved to a unique final name. A checkpoint closes the crash window between publishing and receipt creation.

On cancellation or connection failure, the partial stays resumable. On malformed EOF/hash failure, its generated pending sidecar is invalidated and Retry creates a fresh partial; suspect bytes remain unpublished. Completed receipts avoid duplicates only after revalidating the actual saved file's size/SHA. A missing or user-modified completed file is downloaded to a new unique name; a user-modified file is preserved. Changed source versions do not reuse old partials or completed receipts. Progress reports file counts and actual transferred bytes; a total byte size is optional and is not invented before it is known.

---

## Legacy compatibility reference

# Albiqo Transfer protocol 1

Frozen: 14 November 2026. Both peers use explicit foreground transfer sessions.

## Access and catalogue

From iOS1.0.3(15), foreground album engines share one memory-only pairing lease. Each engine still has its own sessionId and exposes only its selected album. Album-screen dismissal stops its engine/media/listeners, while the code lease can be reused when another foreground album is opened. Backgrounding invalidates the shared lease even after sheet dismissal. The shared ten-failure budget, fifteen-minute idle limit and twenty-four-hour absolute limit cannot be reset by changing albums. Earlier iOS releases generate a fresh PIN for each engine. The PC user enters it. It is held only in memory, never logged or persisted. The iPhone rate-limits failed authentication and stops the session when backgrounded. USB requires Apple's mobile-device USB driver/service and the iPhone's normal Trust approval; the Apple Devices application UI is not part of the transfer flow.

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

PC downloads into an adjacent `.partial` file and a metadata sidecar. Resume requires identical archive ID, size and SHA-256; stale sidecars do not authorize overwriting unrelated files. Final size and SHA-256 must match before moving atomically to a unique `.zip` name. Existing final files are never overwritten. On cancellation/error partial data remains resumable. If SHA-256 fails, only the generated resume sidecar is invalidated; suspect partial bytes remain and Retry downloads a fresh unique copy. The album-named folder is inside the ZIP; no automatic extraction of an untrusted archive. Disconnect and application closing dispose transports and clear the PIN. From Windows0.2.1, transfer cancellation preserves a live transport and its memory-only PIN. USB authorization is bound to an uninterrupted usbmux Listen attachment with exact deviceId+serial verification; target Detached, replacement identity, service EOF or malformed events permanently cancel it. Bluetooth authorization is bound to one BluetoothLEDevice connection lifetime; a Disconnected event permanently cancels it even if the address reconnects. GATT service recreation may be refreshed on the same live link and reauthenticated with the cached PIN. No PIN is stored in settings, files or logs. Invalid/expired authentication clears the cache; no automatic retry loop guesses PINs. Neither transfer path deletes originals.
