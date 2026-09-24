# Transparency Log（透明日志设计与验证手册）

> 状态：AigcTotal.Log 库已实现（RFC 6962 Merkle + 段文件 + checkpoint 链 + keys 状态机 + ES256 线格式与验签 +
> SignedReport + proof.json + 锚定归档）；`aigc-verify` CLI 与本地宿主 `aigc-log-host` 已落地。
> 本文档记录字节形态约定与第三方验证流程；定时编排与静态发布属闭源 server 职责（本阶段由本地宿主替身）。

## 1. 威胁模型与公开物

日志的全部公开物：

- `segments/<首条序号 D12>.jsonl` —— 追加只读段文件，每行一个条目的 canonical JSON + LF；
- `checkpoints/<tree_size D12>.json` —— 每小时签名的 checkpoint（canonical JSON）；
- `well-known/keys.json` —— 公钥状态机快照（git 历史即锚定）；
- `proofs/<seq D12>.proof.json` —— 每条目在 checkpoint 时点补齐的包含证明（**便利物**，可由段重建）；
- 外部锚定：GitHub Releases 每日 tarball（`<yyyy-MM-dd>.tar`，含逐文件 SHA-256 manifest，见 §8）。

隐私与最小披露：日志条目只有四字段 `{input_sha256, report_sha256, seq, timestamp}`——不含判定、工具、载体。
`input_sha256` 为被检文件的整文件 SHA-256（与信封 `input.sha256` 同格式）：其他持有同文件的用户
可按它定位条目（D5 第二用户路径）；哈希不可逆，日志只披露"该文件曾被核查过"，判定细节在报告本体中，
验证者持报告即可核对。

## 2. 段文件格式

- 每行 = `LogEntry` 的 canonical JSON（RFC 8785 子集，键序 `input_sha256 < report_sha256 < seq < timestamp`）：
  `{"input_sha256":"sha256:<64hex>","report_sha256":"sha256:<64hex>","seq":<N>,"timestamp":"YYYY-MM-DDTHH:MM:SSZ"}`
  （四字段形态 v1 冻结，2026-09-24 D1；首个公开段发布后即不可再改）
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

载入期校验（`KeyStore.Parse`）：格式、状态、时间戳之外，还强制 **kid ↔ 公钥绑定**
（kid 必须 = `k`+SHA-256(SPKI(JWK))，2026-09-24 §2-#3 纵深防御：keys.json 是信任根故非漏洞，
防生成流程错配）。签发侧工具（`aigc-log-host keys generate|retire|revoke`，库内
`KeyStore.Generate`/`Serialize`）：P-256 生成 → JWK/SPKI/kid 派生 → active 记录追加写出；
私钥 PKCS#8 只进运营侧，绝不进公开目录。发布流程：well-known/keys.json 随开源仓提交，
git 历史即锚定。

## 6. SignedReport（签名报告，v1 冻结）

签发侧交付形态 = 报告信封 + 签名数组：

```json
{"report":<报告信封（schema v1）>,"signatures":[{"alg":"ES256","kid":"k…","value":"<base64url(64B P1363)>"}]}
```

- 签名域 = 信封 canonical 字节（`ReportEnvelope.CanonicalJson` 的 UTF-8 形式）——外层不参与签名，
  天然排除 signatures 数组自身；`report_sha256` = 该字节的 SHA-256（`sha256:` 形态）；
- 解析严格封闭：顶层仅 `report`/`signatures`，签名项仅 `alg`/`kid`/`value`，alg 只允许 ES256，
  value 必须解码为 64 字节 P1363（base64url 无填充）；
- SM2 双签 = signatures 追加项（后议）；
- 报告验签（`ReportVerifier`）：重算信封 canonical 字节 → 按 kid 取 keys.json 公钥 →
  `Es256Verifier` 验签 → `KeyPolicy.EvaluateForVerification`（atTime = 信封 timestamp）。

## 7. proof.json（包含证明）

```json
{"audit_path":["<base64url(32B)",…],"seq":<N>,"tree_size":<M>}
```

- 叶哈希 = `Rfc6962.LeafHash(条目 canonical 行)`；下标 = `seq − 全日志首条序号`；
- audit_path 自叶向根（RFC 6962 §2.1.3），验证 = RFC 9162 §2.1.3.2（`MerkleVerifier.VerifyInclusion`）；
- 生成由编排器在 checkpoint 时点为每条已入日志条目补齐（`ProofFactory`）；
- proof 是**便利物**——`aigc-verify` 无 proof 时自行从公开段重建树计算路径；
  tree_size ↔ root 的绑定由对应 checkpoint 的 `(sha256_root_hash, tree_size)` 对成立。

## 8. 外部锚（GitHub Releases 每日 tarball）

- 归档内容：`manifest.json`（date + 逐文件 `{path, sha256, bytes}`）+ `segments/` + `checkpoints/` +
  `well-known/`（`proofs/` 不进锚，可由段重建）；
- tar 为确定性 POSIX ustar（mtime=0、路径排序、固定 mode）——同一公开物目录重打包逐字节一致，
  GNU tar 可正常读取；篡改任一字节在读取期即被 manifest SHA-256 对照拒绝；
- 发布：每日 CI（`.github/workflows/anchor-daily.yml`）打 tarball、创建日期 tag 的 Release
  （库核心不含 GitHub API 依赖；下载与解包留 CI/手动，验证侧持本地 tarball 用
  `LocalAnchorProvider.ReadArchive` 整验）；
- 锚定语义：外部见证归档内各 checkpoint 的 canonical 字节于日期 T；验证者沿
  `prev_checkpoint_hash` 回溯，碰到任一被锚 checkpoint 即为信任终点；
  **截断链**（链首带 prev，见 §2-#2）只有对照外部锚才能建立信任——无锚时 `aigc-verify` 拒绝；
- 镜像纪律见 `MIRROR.md`（被攻击时审计可得性不受影响）。

## 9. 第三方验证流程（`aigc-verify`）

```console
$ aigc-verify verify <signed-report.json> --keys keys.json \
    --segments segments/ --checkpoints checkpoints/ \
    [--proof proofs/000000000000.proof.json] [--anchor 2026-09-24.tar]
$ aigc-verify lookup --input-sha256 sha256:… --segments segments/
$ aigc-verify audit --segments segments/ --checkpoints checkpoints/ --keys keys.json [--anchor …]
```

**持报告者**（verify，五步全链）：

1. 解析 SignedReport → 重算信封 canonical 字节 → 得 `report_sha256`；
2. 按 kid 取 keys.json 公钥验签（含密钥状态机）；
3. 在公开段中定位条目（有 proof 按 seq 二分定位段；无 proof 按 report_sha256 扫描），
   信封 `input.sha256` 与条目 `input_sha256` 交叉核对；
4. 由段重建该 checkpoint tree_size 的树根 → 对照 `sha256_root_hash` → 验证包含证明
   （有 proof 用之；无 proof 自行重建路径）；
5. checkpoint 链回溯：逐链接校验 + 逐枚验签；链首截断时必须命中外部锚，否则拒绝。

**仅持同文件者**（lookup，D5 第二用户路径）：对自己文件算 SHA-256 → 按 `input_sha256`
定位条目 → 证明"该文件曾在 T 时刻被核查并存证"；默认只见存在性 + 时间，
要看判定细节须按条目 `report_sha256` 取回报告本体再走 verify。

**全量审计**（audit）：链首截断检测、逐 checkpoint 验签与前缀树根对照、未覆盖尾条目告警、
可选锚归档整验。公开物 + 开源 CLI 即完整信任源（D4：检索接口是便利层，不进信任路径）。

退出码：0 有效 / 1 验证失败 / 10 处理错误；`--json` 输出机器态。全程无网络访问。
