using System;
using System.Collections.Generic;
using System.IO;
using AigcTotal.Log.Segments;
using AigcTotal.Report;

namespace AigcTotal.Log.Keys
{
    public enum KeyStatus
    {
        Active = 1,
        VerifyOnly = 2,
        Revoked = 3,
    }

    /// <summary>well-known 公钥记录（kid = "k" + 公钥 SPKI DER 的 SHA-256 全长 64 位小写 hex）。</summary>
    public sealed record KeyRecord(
        string Kid,
        string Alg,
        IReadOnlyDictionary<string, string> PubkeyJwk,
        KeyStatus Status,
        DateTimeOffset Created,
        DateTimeOffset? Retired,
        DateTimeOffset? Revoked,
        string? RevokedReason)
    {
    }

    public sealed class KeyFile
    {
        internal KeyFile(IReadOnlyList<KeyRecord> keys)
        {
            Keys = keys;
        }

        /// <summary>由记录集合构建（keys 工具/宿主侧使用；载入路径走 Parse）。</summary>
        public static KeyFile Of(params KeyRecord[] keys) => new KeyFile(keys);

        public IReadOnlyList<KeyRecord> Keys { get; }

        public KeyRecord? Find(string kid)
        {
            foreach (KeyRecord key in Keys)
            {
                if (key.Kid == kid) return key;
            }
            return null;
        }
    }

    /// <summary>keys.json 解析（开源仓 well-known 快照；git 历史即锚定）。格式、kid/JWK/状态合法性与
    /// kid↔公钥绑定（§2-#3：kid 必须 = "k"+SHA-256(SPKI(JWK))，防 keys.json 生成流程出错）在载入期全部校验。</summary>
    public static class KeyStore
    {
        public static KeyFile Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static KeyFile Parse(string json)
        {
            Dictionary<string, object?> doc;
            try
            {
                doc = CanonicalJson.Deserialize(json);
            }
            catch (FormatException ex)
            {
                throw new FormatException("malformed keys.json: " + ex.Message, ex);
            }
            if (!doc.TryGetValue("keys", out object? keysObj) || keysObj is not List<object?> list)
            {
                throw new FormatException("keys.json must contain a 'keys' array");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<KeyRecord>();
            foreach (object? item in list)
            {
                keys.Add(ParseKey(item as Dictionary<string, object?>, seen));
            }
            return new KeyFile(keys);
        }

        private static KeyRecord ParseKey(Dictionary<string, object?>? doc, HashSet<string> seen)
        {
            if (doc == null) throw new FormatException("key entry must be an object");
            if (Require(doc, "kid") is not string kid || !TokenFormat.IsValidKid(kid))
            {
                throw new FormatException("bad kid format");
            }
            if (!seen.Add(kid)) throw new FormatException($"duplicate kid '{kid}'");
            if (Require(doc, "alg") is not string alg || alg != "ES256")
            {
                throw new FormatException("alg must be ES256");
            }
            if (Require(doc, "pubkey_jwk") is not Dictionary<string, object?> jwk)
            {
                throw new FormatException("pubkey_jwk must be an object");
            }
            if (Require(jwk, "kty") is not string kty || kty != "EC"
                || Require(jwk, "crv") is not string crv || crv != "P-256"
                || Require(jwk, "x") is not string x || x.Length == 0
                || Require(jwk, "y") is not string y || y.Length == 0)
            {
                throw new FormatException("pubkey_jwk must be EC/P-256 with x and y");
            }

            if (Require(doc, "status") is not string statusText
                || !TryParseStatus(statusText, out KeyStatus status))
            {
                throw new FormatException("status must be active / verify_only / revoked");
            }
            if (Require(doc, "created") is not string createdText)
            {
                throw new FormatException("missing created timestamp");
            }
            if (!LogTime.TryParse(createdText, out DateTimeOffset created))
            {
                throw new FormatException("bad created timestamp");
            }
            DateTimeOffset? retired = ParseOptionalTimestamp(doc, "retired");
            DateTimeOffset? revoked = ParseOptionalTimestamp(doc, "revoked");
            string? reason = doc.TryGetValue("revoked_reason", out object? reasonObj) ? reasonObj as string : null;

            if (status == KeyStatus.Revoked && revoked == null)
            {
                throw new FormatException("revoked key requires a revoked timestamp");
            }

            var jwkStrings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in jwk)
            {
                jwkStrings[pair.Key] = pair.Value as string ?? throw new FormatException("jwk values must be strings");
            }

            // kid ↔ 公钥绑定（纵深防御：keys.json 是信任根，非漏洞；防生成流程错配）
            string derivedKid;
            byte[] spki;
            try
            {
                spki = Signing.Es256Wire.BuildSpkiFromJwk(jwkStrings);
                derivedKid = Signing.Es256Wire.KidFromSpki(spki);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or FormatException)
            {
                throw new FormatException($"kid '{kid}': pubkey_jwk is not a valid P-256 point ({ex.Message})");
            }
            if (derivedKid != kid)
            {
                throw new FormatException(
                    $"kid '{kid}' does not match SHA-256(SPKI(pubkey_jwk)) '{derivedKid}' — mis-generated keys.json");
            }

            return new KeyRecord(kid, alg, jwkStrings, status, created, retired, revoked, reason);
        }

        /// <summary>序列化写出（发布流程用）：与 Parse 对称；pretty 形态便于 git 审阅，字段序固定。</summary>
        public static string Serialize(KeyFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            var keys = new List<object?>();
            foreach (KeyRecord key in file.Keys)
            {
                var jwk = new Dictionary<string, object?>();
                foreach (var pair in key.PubkeyJwk)
                {
                    jwk[pair.Key] = pair.Value;
                }
                keys.Add(new Dictionary<string, object?>
                {
                    ["kid"] = key.Kid,
                    ["alg"] = key.Alg,
                    ["pubkey_jwk"] = jwk,
                    ["status"] = StatusToken(key.Status),
                    ["created"] = LogTime.Format(key.Created),
                    ["retired"] = key.Retired.HasValue ? LogTime.Format(key.Retired.Value) : null,
                    ["revoked"] = key.Revoked.HasValue ? LogTime.Format(key.Revoked.Value) : null,
                    ["revoked_reason"] = key.RevokedReason,
                });
            }
            return CanonicalJson.SerializePretty(new Dictionary<string, object?> { ["keys"] = keys });
        }

#if NET
        /// <summary>
        /// 生成新密钥（net10；签发侧能力）：P-256 → SPKI → kid → active 记录 + 私钥 PKCS#8 导出。
        /// 私钥只进运营侧存储（绝不进公开目录）；keys.json 仅含公钥。
        /// </summary>
        public static GeneratedKey Generate(DateTimeOffset createdUtc)
        {
            using var key = System.Security.Cryptography.ECDsa.Create(
                System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            byte[] pkcs8 = key.ExportPkcs8PrivateKey();

            byte[] point = new byte[65];
            // Q = 0x04 || X || Y：从 SPKI 定长模板的尾部提取（RFC 5480 P-256 布局，26 字节前缀）
            spki.AsSpan(26).CopyTo(point);
            string x = Signing.Es256Wire.Base64UrlEncode(point.AsSpan(1, 32));
            string y = Signing.Es256Wire.Base64UrlEncode(point.AsSpan(33, 32));
            string kid = Signing.Es256Wire.KidFromSpki(spki);

            var record = new KeyRecord(
                kid, "ES256",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["x"] = x,
                    ["y"] = y,
                },
                KeyStatus.Active, createdUtc, Retired: null, Revoked: null, RevokedReason: null);
            return new GeneratedKey(record, pkcs8);
        }
#endif

        internal static string StatusToken(KeyStatus status)
        {
            switch (status)
            {
                case KeyStatus.Active: return "active";
                case KeyStatus.VerifyOnly: return "verify_only";
                case KeyStatus.Revoked: return "revoked";
                default: throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static DateTimeOffset? ParseOptionalTimestamp(Dictionary<string, object?> doc, string name)
        {
            if (!doc.TryGetValue(name, out object? value) || value == null) return null;
            if (value is string text && LogTime.TryParse(text, out DateTimeOffset parsed)) return parsed;
            throw new FormatException($"bad {name} timestamp");
        }

        private static object? Require(Dictionary<string, object?> doc, string name)
        {
            if (!doc.TryGetValue(name, out object? value) || value == null)
            {
                throw new FormatException($"missing field '{name}'");
            }
            return value;
        }

        private static bool TryParseStatus(string text, out KeyStatus status)
        {
            switch (text)
            {
                case "active": status = KeyStatus.Active; return true;
                case "verify_only": status = KeyStatus.VerifyOnly; return true;
                case "revoked": status = KeyStatus.Revoked; return true;
                default: status = default; return false;
            }
        }
    }

#if NET
    /// <summary>生成产物：active 公钥记录（进 keys.json）+ PKCS#8 私钥（运营侧保管）。</summary>
    public sealed record GeneratedKey(KeyRecord Record, byte[] PrivatePkcs8);
#endif
}
