using System;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Nethermind.Crypto;

static byte[] Hex(string s){ var b=new byte[s.Length/2]; for(int i=0;i<b.Length;i++) b[i]=Convert.ToByte(s.Substring(i*2,2),16); return b; }
static string Hx(ReadOnlySpan<byte> b)=>Convert.ToHexString(b).ToLowerInvariant();

var goldenPath = System.IO.Path.Combine(AppContext.BaseDirectory, "vetkd_golden.json");
var g = JsonDocument.Parse(System.IO.File.ReadAllText(goldenPath)).RootElement;
byte[] enc = Hex(g.GetProperty("encrypted_key").GetString()!);
byte[] dpkB = Hex(g.GetProperty("dpk").GetString()!);
byte[] tsk = Hex(g.GetProperty("tsk_secret").GetString()!);
byte[] input = Hex(g.GetProperty("input_hex").GetString()!);
byte[] goldVk = Hex(g.GetProperty("vetkey").GetString()!);
byte[] goldOkm = Hex(g.GetProperty("aes_okm").GetString()!);

// --- parse the 192-byte encrypted_key: c1(G1 48) || c2(G2 96) || c3(G1 48) ---
var c1 = new Bls.P1(enc.AsSpan(0,48));
var c2 = new Bls.P2(enc.AsSpan(48,96));
var c3 = new Bls.P1(enc.AsSpan(144,48));
var dpk = new Bls.P2(dpkB);
Console.WriteLine($"decoded: c1.inGroup={c1.ToAffine().InGroup()} c2.inGroup={c2.ToAffine().InGroup()} c3.inGroup={c3.ToAffine().InGroup()} dpk.inGroup={dpk.ToAffine().InGroup()}");

var g1 = Bls.P1.Generator().ToAffine();
var g2 = Bls.P2.Generator().ToAffine();

// (a) consistency: e(c1,g2) == e(g1,c2)
bool consistent = Bls.PT.FinalVerify(new Bls.PT(g2, c1.ToAffine()), new Bls.PT(c2.ToAffine(), g1));
Console.WriteLine($"consistency e(c1,g2)==e(g1,c2): {consistent}");

// (b) unblind: k = c3 - c1*tsk
var sc = new Bls.Scalar(); sc.FromBendian(tsk);
var k = c3.Add(c1.Mult(sc).Neg());

// (c) verify: e(k,g2) == e(H,dpk), H = augmentedHashToG1(dpk||input)
byte[] dst = Encoding.ASCII.GetBytes("BLS_SIG_BLS12381G1_XMD:SHA-256_SSWU_RO_AUG_");
var H = new Bls.P1(); H = H.HashTo(input, dst, dpkB);
bool verified = Bls.PT.FinalVerify(new Bls.PT(g2, k.ToAffine()), new Bls.PT(dpk.ToAffine(), H.ToAffine()));
Console.WriteLine($"verify e(k,g2)==e(H,dpk): {verified}");

// (d) vetkey = compress(k) (48B)
byte[] vk = k.Compress();
bool vkMatch = Hx(vk)==Hx(goldVk);
Console.WriteLine($"vetkey C# = {Hx(vk)}");
Console.WriteLine($"vetkey JS = {Hx(goldVk)}  MATCH={vkMatch}");

// (e) AES key material via HKDF-SHA256
byte[] info = Encoding.UTF8.GetBytes("ic-vetkd-bls12-381-g2-aes-gcm-v2-wasp-bzzz/dm/v1");
byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, vk, 32, Array.Empty<byte>(), info);
bool okmMatch = Hx(okm)==Hx(goldOkm);
Console.WriteLine($"aes_okm C# = {Hx(okm)}");
Console.WriteLine($"aes_okm JS = {Hx(goldOkm)}  MATCH={okmMatch}");

Console.WriteLine((consistent && verified && vkMatch && okmMatch)
  ? "\n✅ C# vetKD decrypt reproduces the mainnet vetKey + AES key EXACTLY."
  : "\n❌ mismatch");
