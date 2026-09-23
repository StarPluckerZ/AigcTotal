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

    /// <summary>keys.json 解析（开源仓 well-known 快照；git 历史即锚定）。格式与 kid/JWK/状态合法性在载入期全部校验。</summary>
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
            if (doc["keys"] is not List<object?> list)
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

            return new KeyRecord(kid, alg, jwkStrings, status, created, retired, revoked, reason);
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
}
