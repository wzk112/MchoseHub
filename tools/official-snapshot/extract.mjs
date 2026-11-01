import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {execFileSync} from 'node:child_process';
const root=import.meta.dirname;
if(!process.argv[2])throw Error('Usage: node extract.mjs <official-bundle.js> [resources-output-folder]');
const bundle=path.resolve(process.argv[2]),out=path.resolve(process.argv[3]??path.join(root,'../../src/MchoseHub.Core/Resources'));
const snapshot=JSON.parse(fs.readFileSync(path.join(root,'snapshot.json'),'utf8'));
if(crypto.createHash('sha256').update(fs.readFileSync(bundle)).digest('hex')!==snapshot.sha256)throw Error('Official snapshot changed. Review symbols and protocol before replacing resources.');
fs.mkdirSync(out,{recursive:true});
const temp=path.join(out,'schemas.tmp.json');
try {
 execFileSync(process.execPath,[path.join(root,'extract-schemas.mjs'),bundle,temp],{stdio:'inherit'});
 const schemas=JSON.parse(fs.readFileSync(temp,'utf8'));schemas.models.G87=structuredClone(schemas.defaults);delete schemas.models.fullKeyCode;
 schemas.source=snapshot.source;schemas.fetched=snapshot.fetched;schemas.sha256=snapshot.sha256;
 fs.writeFileSync(path.join(out,'keyboard-models.json'),JSON.stringify(schemas,null,1));
 execFileSync(process.execPath,[path.join(root,'extract-layouts.cjs'),bundle,out],{stdio:'inherit'});
} finally { if(fs.existsSync(temp))fs.unlinkSync(temp); }
