#!/usr/bin/env python3
# One-off: seed a PRIVATE "Examples" forum on dvl2u with ORIGINAL IC/dev content
# (topics + threaded replies + reactions + an accepted answer) to demo the forum
# features. All calls go through the dfx controller identity (super-admin). Private
# so it never appears on the public web; deletable via /api/server/delete.
import subprocess, json, re, os, sys

CAN = "wasp"
NET = ["--network", "ic"]
ENV = dict(os.environ, DFX_WARNING="-mainnet_plaintext_identity")

HEART = "❤️"   # ❤️ (must match the allowed-reactions string exactly)
THUMB = "\U0001F44D"     # 👍

def candid_blob(obj):
    j = json.dumps(obj, ensure_ascii=False, separators=(',', ':'))  # compact: the canister's extractor expects "k":"v" (no spaces)
    return j.replace("\\", "\\\\").replace('"', '\\"')  # escape for candid blob "..."

def call(url, obj):
    arg = '(record{method="POST";url="%s";headers=vec{};body=blob "%s"})' % (url, candid_blob(obj))
    p = subprocess.run(["dfx","canister","call",CAN,"http_request_update",arg]+NET,
                       capture_output=True, text=True, env=ENV)
    out = p.stdout + p.stderr
    return decode(out)

def decode(out):
    m = re.search(r'status_code = (\d+)', out)
    status = int(m.group(1)) if m else -1
    b = re.search(r'blob "(.*?)";', out, re.S)
    body = ""
    if b:
        s = b.group(1); i = 0; raw = bytearray()
        while i < len(s):
            c = s[i]
            if c == '\\' and i+1 < len(s):
                n = s[i+1]
                esc = {'n':10,'r':13,'t':9,'"':34,'\\':92,"'":39}
                if n in esc: raw.append(esc[n]); i += 2; continue
                pair = s[i+1:i+3]
                if re.fullmatch(r'[0-9a-fA-F]{2}', pair): raw.append(int(pair,16)); i += 3; continue
                raw.append(ord(c)); i += 1
            else:
                raw.extend(c.encode('utf-8')); i += 1
        body = raw.decode('utf-8', errors='replace')
    return status, body

# ── content (ORIGINAL) ──
TOPICS = [
 {"title":"How does Internet Identity sign in without a password?",
  "op":{"u":"User99","t":"I keep seeing passwordless everywhere, but what actually proves it is me? Is it just WebAuthn, or does the canister side do more? #internet-identity #auth"},
  "replies":[
    {"u":"Tester","t":"Two layers. WebAuthn (a passkey or biometric) unlocks a **session key** in your browser; Internet Identity then issues a *delegation* that signs that key, scoped to the dapp. The canister just sees msg_caller = your II principal. No shared secret ever leaves your device."},
    {"u":"Dev1","t":"And the principal is per-frontend-origin, so the same anchor on a different site = a different principal. Trips up everyone once."}],
  "accept_post":1,                       # posts[1] (Tester) is the accepted answer
  "reacts":[(1,THUMB),(2,HEART)]},       # 👍 on Tester, ❤️ on Dev1
 {"title":"Stable memory: surviving a --mode upgrade",
  "op":{"u":"Anonymous","t":"What is the safe pattern so I do not wipe user data on every deploy? #stable-memory #motoko"},
  "replies":[
    {"u":"Tester","t":"Append-only is the rule: never reorder or shrink persisted fields. Keep a magic + version header so you can detect the layout and migrate forward. Need to change a record? Write a new region and copy across."}],
  "accept_post":-1,
  "reacts":[(1,HEART)]},
 {"title":"Show: a chat app running 100% in one canister",
  "op":{"u":"User99","t":"No backend and no database, every message is a signed transaction inside a single canister, rendered server-side. Feedback welcome! #showcase #wasp"},
  "replies":[
    {"u":"Dev1","t":"Love the render-as-query approach. How do you do the ~5s live updates without websockets?"},
    {"u":"User99","t":"Polling a certified query endpoint and diffing the result. Cheap, and it survives a refresh."}],
  "accept_post":-1,
  "reacts":[(0,HEART),(0,THUMB)]},       # ❤️ + 👍 on the OP (posts[0])
]

def main():
    print("== create PRIVATE forum 'Examples' ==")
    st, body = call("/api/servers", {"name":"Examples","kind":"forum","private":"true"})
    print("  ", st, body)
    sid = json.loads(body)["id"]
    print("  serverId =", sid)
    for ti, top in enumerate(TOPICS):
        print("== topic:", top["title"])
        st, body = call("/api/forum/topic", {"serverId":sid,"title":top["title"],"text":top["op"]["t"],"username":top["op"]["u"]})
        print("   topic:", st, body)
        rid = json.loads(body)["topicId"]
        for rep in top["replies"]:
            st, b2 = call("/api/chat/post", {"roomId":rid,"text":rep["t"],"username":rep["u"]})
            print("   reply", rep["u"], st, b2)
        # read back ids
        st, rb = call("/api/forum/read", {"serverId":sid,"roomId":rid})
        posts = json.loads(rb).get("posts", [])
        ids = [p["id"] for p in posts]
        print("   post ids:", ids)
        for (pidx, emoji) in top["reacts"]:
            if pidx < len(ids):
                st, _ = call("/api/chat/react", {"msgId":ids[pidx],"emoji":emoji})
                print("   react", emoji, "on post", pidx, "->", st)
        if top["accept_post"] >= 0 and top["accept_post"] < len(ids):
            st, sb = call("/api/forum/solve", {"roomId":rid,"msgId":ids[top["accept_post"]]})
            print("   accepted-answer ->", st, sb)
    print("DONE. serverId =", sid, "(private)")

main()
