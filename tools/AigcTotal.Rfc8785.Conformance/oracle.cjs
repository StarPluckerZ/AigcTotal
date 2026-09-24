// RFC 8785 一致性证明的 V8 侧预言机。
//
// Part 1（canonicalize）为 RFC 8785 附录 A 的 ECMAScript 示例规范化器【逐字拷贝】，
// 仅补充了 UTF-8 无关的纯文本组装（附录 A 声明省略错误处理与 UTF-8 生成，语料均为合法 I-JSON）。
// Part 2 为数字模式：JSON.stringify 即 ECMA-262 Number::toString 的规范实现（V8）。
//
// 用法：
//   node oracle.cjs struct  <corpus.jsonl>      每行 {"input":...,"expected":...}
//   node oracle.cjs numbers <hex-lines.txt>     每行 16 位 IEEE 754 十六进制
// 输出：
//   struct  → 一行 `OK <n>` 或 `FAIL <n>/<total>`（附前若干条 mismatch 明细到 stderr）
//   numbers → 每行输出对应 JSON.stringify 结果（非有限输出 `<nonfinite>`）

'use strict';
var canonicalize = function(object) {

    var buffer = '';
    serialize(object);
    return buffer;

    function serialize(object) {
        if (object === null || typeof object !== 'object' ||
            object.toJSON != null) {
            /////////////////////////////////////////////////
            // Primitive type or toJSON, use "JSON"        //
            /////////////////////////////////////////////////
            buffer += JSON.stringify(object);

        } else if (Array.isArray(object)) {
            /////////////////////////////////////////////////
            // Array - Maintain element order              //
            /////////////////////////////////////////////////
            buffer += '[';
            let next = false;
            object.forEach((element) => {
                if (next) {
                    buffer += ',';
                }
                next = true;
                /////////////////////////////////////////
                // Array element - Recursive expansion //
                /////////////////////////////////////////
                serialize(element);
            });
            buffer += ']';

        } else {
            /////////////////////////////////////////////////
            // Object - Sort properties before serializing //
            /////////////////////////////////////////////////
            buffer += '{';
            let next = false;
            Object.keys(object).sort().forEach((property) => {
                if (next) {
                    buffer += ',';
                }
                next = true;
                /////////////////////////////////////////////
                // Property names are strings, use "JSON"  //
                /////////////////////////////////////////////
                buffer += JSON.stringify(property);
                buffer += ':';
                //////////////////////////////////////////
                // Property value - Recursive expansion //
                //////////////////////////////////////////
                serialize(object[property]);
            });
            buffer += '}';
        }
    }
};
// —— RFC 8785 附录 A 逐字拷贝结束 ——

const Fs = require('fs');

const mode = process.argv[2];
const path = process.argv[3];
const lines = Fs.readFileSync(path, 'utf8').split('\n').filter((l) => l.length > 0);

if (mode === 'numbers') {
    for (const line of lines) {
        const v = Buffer.from(line, 'hex').readDoubleBE();
        if (!Number.isFinite(v)) {
            console.log('<nonfinite>');
        } else {
            console.log(JSON.stringify(v));
        }
    }
    process.exit(0);
}

if (mode === 'struct') {
    let bad = 0;
    let total = 0;
    for (const line of lines) {
        const entry = JSON.parse(line);
        total++;
        const got = canonicalize(JSON.parse(entry.input));
        if (got !== entry.expected) {
            bad++;
            if (bad <= 5) {
                console.error('MISMATCH input=' + JSON.stringify(entry.input) +
                    ' expected=' + JSON.stringify(entry.expected) + ' got=' + JSON.stringify(got));
            }
        }
    }
    console.log(bad === 0 ? 'OK ' + total : 'FAIL ' + bad + '/' + total);
    process.exit(bad === 0 ? 0 : 1);
}

console.error('unknown mode: ' + mode);
process.exit(2);
