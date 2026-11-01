import parser from '@babel/parser';
function staticValue(n) {
 if(['StringLiteral','NumericLiteral','BooleanLiteral'].includes(n.type))return n.value;
 if(n.type==='UnaryExpression' && n.operator==='-')return -staticValue(n.argument);
 if(n.type==='ArrayExpression')return n.elements.map(staticValue);
 if(n.type==='ObjectExpression')return Object.fromEntries(n.properties.map(p=>[p.key.name??p.key.value,staticValue(p.value)]));
 if(n.type==='NewExpression' && n.callee.name==='Array')return Array(staticValue(n.arguments[0])).fill(0);
 if(n.type==='CallExpression' && n.callee.type==='MemberExpression' && n.callee.property.name==='fill')return staticValue(n.callee.object).fill(staticValue(n.arguments[0]));
 throw Error('Nonliteral '+n.type);
}
// 从网页版 bundle 中抽取键盘各机型的命令字段模式（parser schema）。
// 输出：work/models/keyboard-models.json
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const bundlePath = path.resolve(process.argv[2]);
const src = fs.readFileSync(bundlePath, 'utf8');

/** 找到 `sym=` 赋值起点（跳过属性访问 .sym=） */
function assignStart(sym) {
  const re = new RegExp(`(^|[^\\w$.])${sym.replace(/\$/g, '\\$')}\\s*=\\s*`, 'g');
  let m;
  while ((m = re.exec(src))) {
    const idx = m.index + m[0].length;
    // 排除比较运算 == / ===
    if (src[idx] === '=') continue;
    return idx;
  }
  throw new Error('symbol not found: ' + sym);
}

function balanced(start, open, close) {
  let depth = 0, i = start;
  for (; i < src.length; i++) {
    const c = src[i];
    if (c === '"' || c === "'" || c === '`') {
      const q = c;
      i++;
      while (i < src.length && src[i] !== q) {
        if (src[i] === '\\') i++;
        i++;
      }
      continue;
    }
    if (c === open) depth++;
    else if (c === close) {
      depth--;
      if (depth === 0) return src.slice(start, i + 1);
    }
  }
  throw new Error('unbalanced');
}

function literal(sym) {
  const at = assignStart(sym);
  const open = src[at];
  if (open !== '[' && open !== '{') return null;
  const close = open === '[' ? ']' : '}';
  const text = balanced(at, open, close);
  return { value: staticValue(parser.parseExpression(text)), start: at, end: at + text.length };
}

function fnBody(sym) {
  const at = assignStart(sym);
  const arrow = src.indexOf('{', at);
  const body = balanced(arrow, '{', '}');
  return body;
}

/** 把 jss 链式调用正文转成字段列表 */
function chainFields(body) {
  const out = [];
  const re = /\.(uint8|uint16|uint24|uint32|bit4)\(\s*"([^"]+)"\s*\)|\.array\(\s*"([^"]+)"\s*,\s*\{\s*type:\s*"uint8"\s*,\s*length:\s*([^}]+)\}|keyList\.forEach/g;
  let m;
  while ((m = re.exec(body))) {
    if (m[0] === 'keyList.forEach') {
      out.push({ n: '#keyList', k: 'keys' });
      continue;
    }
    if (m[1]) {
      out.push({ n: m[2], k: m[1] === 'bit4' ? 'b4' : 'u8' });
      continue;
    }
    const len = m[4].trim();
    out.push({ n: m[3], k: 'arr', len: /^\d+$/.test(len) ? Number(len) : len });
  }
  return out;
}

/** 把字面量数组（ARRAY/deep 形式）转成字段列表 */
function literalFields(value) {
  return value.map((f) => {
    if (f.key !== undefined) return { n: f.key, k: 'arr', len: f.length };
    if (f.type === 'uint8') return { n: f.name, k: 'u8' };
    if (f.type === 'bit4') return { n: f.name, k: 'b4' };
    if (f.type === 'ARRAY') return { n: f.name, k: 'var' };
    throw new Error('unknown type ' + f.type);
  });
}

const CMD = [{ n: 'command', k: 'u8' }];
const SPACE0 = [{ n: 'space0', k: 'var' }];

function schema(sym) {
  const lit = literal(sym);
  if (lit) return literalFields(lit.value);
  return chainFields(fnBody(sym));
}

// ---- 1. 机型 → 模块标识符映射 ----
const loaderAt = src.indexOf('"./config/G75_Pro.js"');
const loaderBlock = src.slice(loaderAt, src.indexOf('`./config/${', loaderAt));
const models = {};
for (const m of loaderBlock.matchAll(/"\.\/config\/([A-Za-z0-9_]+)\.js":\(\)=>[^,]*?then\(\(\)=>([\w$]+)\)/g)) {
  models[m[1]] = m[2];
}

// ---- 2. 每个模块导出的符号 ----
const out = { generatedFrom: 'purify.es-CjFXZAJq.js', defaults: {}, models: {} };

// ---- 2b. 每个机型的键盘布局与默认逐键灯色（I8 表） ----
// I8 的值是符号引用，不能直接 eval，用正则把 型号 → 符号 的映射抠出来
const i8At = assignStart('I8');
const i8Text = balanced(i8At, '{', '}');
const layouts = {};
const i8 = {};
for (const m of i8Text.matchAll(/([\w$]+):\{baseKeyBoard:([\w$]+),commonObj:([\w$]+),specialVal:([\w$]+),lightRgbObj:([\w$]+),lightDiyObj:([\w$]+),otherObj:([\w$]+)\}/g)) {
  i8[m[1]] = { baseKeyBoard: m[2], commonObj: m[3], lightDiyObj: m[6] };
}
for (const [model, info] of Object.entries(i8)) {
  const board = (literal(info.baseKeyBoard)?.value ?? []).map((k) => ({
    name: k.name,
    key: k.key,
    width: k.width,
    top: k.marginTop,
    left: k.marginLeft,
    def: k.defaultVal,
    fn: k.fnVal,
    fn2: k.fn2Val,
  }));

  const diy = literal(info.lightDiyObj)?.value ?? {};

  // 写按键时官方还会把 commonObj 一起铺进去：space1..space13 是 4 字节，
  // space15 是一长串 0（各机型长度不同），fix 是 90 165 结尾。漏了它写进去不生效。
  const common = literal(info.commonObj)?.value ?? {};
  const keyWriteCommon = {};
  for (const [name, value] of Object.entries(common)) {
    if (Array.isArray(value)) keyWriteCommon[name] = value;
  }

  layouts[model] = {
    board,
    diyPre: diy.pre ?? [0, 0, 1, 0, 128, 1],
    diyReadHeader: diy.space0 ?? [0, 0, 1, 0, 122, 1],
    diyRed: diy.rTable ?? [],
    diyGreen: diy.gTable ?? [],
    diyBlue: diy.bTable ?? [],
    keyWriteCommon,
  };
}
out.layouts = layouts;

const defaults = {
  keyList: 'DY',
  performanceCommonParser: 'Hie',
  settingCommonParser: '$ie',
  getPerformanceParser: 'qie',
  lightColorCommonParser: 'tae',
  getLightColorParser: 'eae',
};

out.defaults = {
  keyList: schema(defaults.keyList),
  getPerformance: schema(defaults.getPerformanceParser),
  setPerformance: [...CMD, ...SPACE0, ...schema(defaults.performanceCommonParser)],
  getKeySetting: [{ n: 'space0', k: 'arr', len: 6 }, { n: '#keyList', k: 'keys' }],
  setKeySetting: [...CMD, ...SPACE0, ...schema(defaults.settingCommonParser)],
  getLightColor: schema(defaults.getLightColorParser),
  setLightColor: [...CMD, ...SPACE0, ...schema(defaults.lightColorCommonParser)],
  getDiyLight: [{ n: 'space0', k: 'arr', len: 6 }, { n: 'rTable', k: 'arr', len: 126 }, { n: 'gTable', k: 'arr', len: 126 }, { n: 'bTable', k: 'arr', len: 126 }],
  setDiyLight: [...CMD, { n: 'pre', k: 'var' }, { n: 'rTable', k: 'var' }, { n: 'gTable', k: 'var' }, { n: 'bTable', k: 'var' }],
};

for (const [model, sym] of Object.entries(models)) {
  const at = assignStart(sym);
  const text = src.slice(at, at + 2000);
  const exportsAt = text.indexOf('__proto__:null,');
  const exportsText = text.slice(exportsAt + '__proto__:null,'.length, text.indexOf('}),Symbol.toStringTag'));
  const ex = {};
  for (const p of exportsText.matchAll(/([\w$]+):([\w$]+)/g)) ex[p[1]] = p[2];

  const pick = (key, def) => (ex[key] ? schema(ex[key]) : out.defaults[def]);
  const entry = {
    keyList: ex.keyList ? schema(ex.keyList) : out.defaults.keyList,
    getPerformance: pick('getPerformanceParser', 'getPerformance'),
    setPerformance: ex.performanceCommonParser
      ? [...CMD, ...SPACE0, ...schema(ex.performanceCommonParser)]
      : out.defaults.setPerformance,
    getKeySetting: [{ n: 'space0', k: 'arr', len: 6 }, { n: '#keyList', k: 'keys' }],
    setKeySetting: [...CMD, ...SPACE0, ...(ex.settingCommonParser ? schema(ex.settingCommonParser) : out.defaults.setKeySetting.slice(2))],
    getLightColor: pick('getLightColorParser', 'getLightColor'),
    setLightColor: ex.lightColorCommonParser
      ? [...CMD, ...SPACE0, ...schema(ex.lightColorCommonParser)]
      : out.defaults.setLightColor,
    getDiyLight: out.defaults.getDiyLight,
    setDiyLight: out.defaults.setDiyLight,
  };
  out.models[model] = entry;
}


const dest = path.resolve(process.argv[3]);
fs.writeFileSync(dest, JSON.stringify(out, null, 1));
console.log('models:', Object.keys(models).length);
console.log('written:', dest);
console.log('K87 keyList len:', out.models.K87.keyList.length, 'setPerformance fields:', out.models.K87.setPerformance.length);
