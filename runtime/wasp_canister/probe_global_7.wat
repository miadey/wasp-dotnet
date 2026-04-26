;; Diagnostic probe: read dotnet's global 7 (suspected __memory_base)
;; at runtime and surface its value via debug_print.
;;
;; This module is wasm-merge'd into the final canister and exports two
;; canister query endpoints that bypass the normal candid serde and
;; just call ic0.debug_print with hex-formatted bytes.

(module $probe
  (import "env" "global_7" (global $g7 (mut i32)))
  (import "ic0" "debug_print" (func $debug_print (param i32 i32)))
  (import "ic0" "msg_reply_data_append" (func $reply_append (param i32 i32)))
  (import "ic0" "msg_reply" (func $reply))

  (memory (export "memory") 1)

  ;; Static buffer for hex output: "global_7=0x" + 8 hex chars = 19 bytes
  ;; Plus DIDL header for blob reply.
  (data (i32.const 0) "DIDL\01\6d\7b\01\00\14global_7=0x")  ;; 6 + 3 + 1 = 10 hdr + 11 prefix bytes
  ;; (offsets: 0..6 = "DIDL\01\6d", 6..9 = "\7b\01\00", 9 = LEB(20)=0x14, 10..21 = "global_7=0x")

  (func (export "canister_query dump_global_7")
    (local $val i32)
    (local $i i32)
    (local $nibble i32)

    global.get $g7
    local.set $val

    ;; Format 8 hex chars at offsets 21..29 of memory.
    i32.const 21
    local.set $i
    (loop $hex
      ;; Pull top nibble: (val >> 28) & 0xF
      local.get $val
      i32.const 28
      i32.shr_u
      i32.const 15
      i32.and
      local.set $nibble

      ;; Convert to ASCII: nibble + (nibble < 10 ? '0' : 'a' - 10)
      local.get $i
      local.get $nibble
      local.get $nibble
      i32.const 10
      i32.lt_s
      if (result i32)
        i32.const 0x30      ;; '0'
      else
        i32.const 0x57      ;; 'a' - 10
      end
      i32.add
      i32.store8

      ;; Shift val left 4
      local.get $val
      i32.const 4
      i32.shl
      local.set $val

      local.get $i
      i32.const 1
      i32.add
      local.tee $i
      i32.const 29
      i32.lt_s
      br_if $hex
    )

    ;; Reply with bytes 0..29 (DIDL header + payload of 20 bytes).
    i32.const 0
    i32.const 29
    call $reply_append
    call $reply
  )
)
