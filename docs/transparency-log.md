# Transparency Log（透明日志设计与验证手册）

> 状态：AigcTotal.Log 库已实现（RFC 6962 Merkle + 段文件 + checkpoint 链 + keys 状态机 + ES256 线格式）。
> 本文档记录字节形态约定与第三方验证流程；签名服务与静态发布属闭源 server 职责。

## 1. 威胁模型与公开物

日志的全部公开物：

- `segments/<首条序号 D12>.jsonl` —— 追加只读段文件，每行一个条目的 canonical JSON + LF；
- `checkpoints/…` —— 每小时签名的 checkpoint（canonical JSON）；
- `well-known/keys.json` —— 公钥状态机快照（git 历史即锚定）；
- 外部锚定：GitHub Releases 每日 tarball（server 职责）。

隐私与最小披露：日志条目只有三字段 `{report_sha256, seq, timestamp}`——不含判定、工具、载体。
判定在报告本体中，验证者持报告即可核对。

## 2. 段文件格式

- 每行 = `LogEntry` 的 canonical JSON（RFC 8785 子集，键序 `report_sha256 < seq < timestamp`）：
  `{"report_sha256":"sha256:<64hex>","seq":<N>,"timestamp":"YYYY-MM-DDTHH:MM:SSZ"}`
- 序号全日志严格递增（写侧拒绝非单调追加）；
- 滚动：8192 条或 1 小时（按条目时间戳）先到者；
- 崩溃残行（无 LF 结尾的尾行）由读侧忽略并记诊断——真实性由 checkpoint 树根对照保证。

## 3. Merkle 树（RFC 6962）

- 叶哈希 = SHA-256(0x00 ‖ 叶原文)，叶原文 = 条目 canonical 行的 UTF-8 字节（树对段内每个字节提交）；
- 节点哈希 = SHA-256(0x01 ‖ 左 ‖ 右)；
- n≥2：k = 小于 n 的最大 2 的幂，MTH(D[n]) = H(0x01 ‖ MTH(D[0:k]) ‖ MTH(D[k:n]))——k-split，奇数末段独立，无复制上卷；
- 包含证明验证 = RFC 9162 §2.1.3.2 官方算法（fn/sn LSB 形式）；
- 测试向量：`tests/AigcTotal.Log.Tests/TestVectors/`（源自 google/certificate-transparency 的 ctcrypto
  测试，含 3,630,887 叶真实规模包含证明；一致性证明向量留存待后置实现）。

## 4. Checkpoint

canonical JSON（键序）：

```
{"kid":"k<64hex>","prev_checkpoint_hash":"sha256:<64hex>","sha256_root_hash":"sha256:<64hex>",
 "timestamp":"...Z","tree_head_signature":"<base64url(64B P1363)>","tree_size":<N>}
```

- 签名域 = 除 `tree_head_signature` 外的 canonical JSON（SigningInput）；算法 ES256（ECDSA P-256 + SHA-256）；
- `prev_checkpoint_hash` = sha256(前一 checkpoint 的完整 canonical JSON 字节)——链自哈希；
- kid = `k` + SHA-256(SPKI DER) 全长 64 位小写 hex；
- 生成式 checkpoint（含签名）的 canonical 字节形态由 Log.Tests 字面量钉死。

## 5. keys.json 状态机

```json
{ "keys": [ { "kid": "k…", "alg": "ES256",
  "pubkey_jwk": { "kty": "EC", "crv": "P-256", "x": "…", "y": "…" },
  "status": "active|verify_only|revoked",
  "created": "…Z", "retired": null, "revoked": null, "revoked_reason": null } ] }
```

验证时判定（`KeyPolicy`）：算法必须 ES256；时间必须 ≥ created；
吊销为半开区间——checkpoint 时间早于 `revoked` → 有效但警告（密钥已非 active），等于或晚于 → 拒绝。

## 6. 第三方验证流程（aigc-verify，待实现）

1. 取报告 → 重算报告 canonical 字节 → 核对 `report_sha256`；
2. 用 keys.json 中 kid 对应公钥验证报告签名；
3. 用段文件重建树根 → 对照 checkpoint 的 `sha256_root_hash`；
4. 验证包含证明（RFC 9162 §2.1.3.2）；
5. 沿 `prev_checkpoint_hash` 链回溯至已外部锚定（GitHub Releases）的 checkpoint。
