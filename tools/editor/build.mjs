import { build } from 'esbuild';
import { readFile, writeFile, copyFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const lock = JSON.parse(await readFile(resolve(root, 'package-lock.json'), 'utf8'));
const outdir = resolve(root, '../../src/SecondBrain.Server/wwwroot/editor');
await mkdir(outdir, { recursive: true });
const patched = new Set();
const patchedDependencies = new Set();
const output = await build({
    absWorkingDir: root,
    entryPoints: { editor: 'editor.js', 'editor.worker': 'editor.worker.js' },
    outdir, bundle: true, minify: true, format: 'esm', target: 'es2022',
    loader: { '.ttf': 'file' }, legalComments: 'none', metafile: true,
    plugins: [{ name: 'nonce-monaco-styles', setup(context) {
        // Monaco ships a copied sanitizer as well as declaring an npm dependency.
        // Redirect that exact reviewed import to the patched, lockfile-pinned module.
        context.onResolve({ filter: /^\.\/dompurify\/dompurify\.js$/ }, async args => {
            if (!args.importer.endsWith('/monaco-editor/esm/vs/base/browser/domSanitize.js')) throw new Error('Unexpected vendored sanitizer import.');
            const vendor = await readFile(resolve(dirname(args.importer), args.path), 'utf8');
            if (!vendor.startsWith('/*! @license DOMPurify 3.4.15 ')) throw new Error('Review the changed vendored sanitizer before rebuilding.');
            patchedDependencies.add('monaco-vendored-dompurify');
            return { path: resolve(root, 'node_modules/dompurify/dist/purify.es.mjs') };
        });
        context.onLoad({ filter: /(?:domStylesheets|contextview)\.js$/ }, async args => {
            const original = await readFile(args.path, 'utf8');
            const needle = "const style = document.createElement('style');";
            if (original.split(needle).length !== 2) throw new Error('Monaco style factory changed: ' + args.path);
            patched.add(args.path.split('/').at(-1));
            return { contents: original.replace(needle, needle + "\nstyle.nonce = document.querySelector('meta[name=secondbrain-style-nonce]')?.content || '';"), loader: 'js' };
        });
    } }]
});
if (patched.size !== 2) throw new Error('Expected both Monaco style factories to carry the response nonce.');
if (patchedDependencies.size !== 1) throw new Error('Expected the reviewed sanitizer replacement.');
if (Object.keys(output.metafile.inputs).some(path => path.endsWith('/base/browser/dompurify/dompurify.js'))) throw new Error('The original vendored sanitizer is still included.');
await copyFile(resolve(root, 'node_modules/monaco-editor/LICENSE'), resolve(outdir, 'MONACO-LICENSE.txt'));
await copyFile(resolve(root, 'node_modules/monaco-editor/ThirdPartyNotices.txt'), resolve(outdir, 'THIRD-PARTY-NOTICES.txt'));
await copyFile(resolve(root, 'node_modules/dompurify/LICENSE'), resolve(outdir, 'DOMPURIFY-LICENSE.txt'));
const files = [];
for (const path of Object.keys(output.metafile.outputs).sort()) {
    const contents = await readFile(resolve(root, path));
    files.push({ file: path.split('/').at(-1), bytes: contents.length, sha256: createHash('sha256').update(contents).digest('hex') });
}
const packages = new Map();
for (const input of Object.keys(output.metafile.inputs)) {
    const packagePath = Object.keys(lock.packages).filter(path => path && input.startsWith(path + '/')).sort((a, b) => b.length - a.length)[0];
    if (!packagePath) continue;
    const pinned = lock.packages[packagePath];
    const installed = JSON.parse(await readFile(resolve(root, packagePath, 'package.json'), 'utf8'));
    if (installed.version !== pinned.version) throw new Error('Bundled dependency differs from lock: ' + packagePath);
    packages.set(packagePath, { name: installed.name, version: pinned.version, integrity: pinned.integrity, license: pinned.license, lockPath: packagePath });
    if (input === 'node_modules/monaco-editor/esm/vs/base/common/marked/marked.js') {
        const marked = lock.packages['node_modules/marked'];
        const contents = await readFile(resolve(root, input));
        if (!contents.toString('utf8').startsWith('/**\n * marked v' + marked.version + ' -')) throw new Error('Review the changed vendored Markdown parser.');
        packages.set('node_modules/marked', { name: 'marked', version: marked.version, integrity: marked.integrity, license: marked.license, lockPath: 'node_modules/marked', vendoredFrom: input, vendoredSha256: createHash('sha256').update(contents).digest('hex') });
    }
}
if (!packages.has('node_modules/monaco-editor') || ![...packages.values()].some(p => p.name === 'dompurify')) throw new Error('Bundled dependency inventory is incomplete: ' + JSON.stringify({ packages: [...packages.values()], inputs: Object.keys(output.metafile.inputs).slice(0, 5) }));
await writeFile(resolve(outdir, 'manifest.json'), JSON.stringify({ monaco: '0.57.0', esbuild: '0.28.2', packages: [...packages.values()].sort((a, b) => a.name.localeCompare(b.name)), dependencyPatches: [...patchedDependencies].sort(), styleNoncePatches: [...patched].sort(), files }, null, 2) + '\n');
