import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/editor/contrib/suggest/browser/suggestController.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/editor/contrib/clipboard/browser/clipboard.js';
import 'monaco-editor/editor/contrib/wordOperations/browser/wordOperations.js';
import 'monaco-editor/languages/definitions/markdown/register.js';

self.MonacoEnvironment = { getWorker: () => new Worker(new URL('./editor.worker.js', import.meta.url), { type: 'module' }) };
const models = new WeakMap();
monaco.languages.registerCompletionItemProvider('markdown', {
    triggerCharacters: ['[', '#'],
    async provideCompletionItems(model, position, context, token) {
        if (!models.has(model)) return { suggestions: [] };
        const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
        const link = /\[\[([^\]\n]*)$/.exec(line);
        const tag = /(?:^|\s)#([^\s#]*)$/.exec(line);
        if (!link && !tag) return { suggestions: [] };
        const prefix = (link || tag)[1];
        const kind = link ? 'link' : 'tag';
        const response = await fetch('/browse/complete?kind=' + kind + '&q=' + encodeURIComponent(prefix), { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
        if (!response.ok || token.isCancellationRequested) return { suggestions: [] };
        const items = await response.json();
        if (token.isCancellationRequested) return { suggestions: [] };
        const closing = link && model.getLineContent(position.lineNumber).slice(position.column - 1).startsWith(']]') ? 2 : 0;
        const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
            startColumn: position.column - prefix.length, endColumn: position.column + closing };
        return { suggestions: items.map(item => ({ label: item.label, detail: item.detail,
            kind: link ? monaco.languages.CompletionItemKind.Reference : monaco.languages.CompletionItemKind.Keyword,
            insertText: item.value + (link ? ']]' : ''), range })) };
    }
});

export function create(host, initial, callback) {
    const model = monaco.editor.createModel(initial, 'markdown', monaco.Uri.parse('inmemory://secondbrain/' + crypto.randomUUID() + '.md'));
    const editor = monaco.editor.create(host, { model, ariaLabel: 'Content', automaticLayout: true,
        minimap: { enabled: false }, wordWrap: 'on', scrollBeyondLastLine: false, fontSize: 15,
        tabSize: 2, padding: { top: 12, bottom: 12 }, fixedOverflowWidgets: true });
    let dirty = false, resetting = false;
    models.set(model, true);
    const changed = model.onDidChangeContent(() => {
        if (!resetting && !dirty) { dirty = true; callback.invokeMethodAsync('MarkDirty').catch(() => {}); }
    });
    const handle = {
        getValue: () => model.getValue(),
        setReadOnly: value => editor.updateOptions({ readOnly: value }),
        setValue(value) { resetting = true; model.setValue(value); dirty = false; resetting = false; },
        focus: () => editor.focus(),
        dispose() { models.delete(model); changed.dispose(); editor.dispose(); model.dispose(); }
    };
    return handle;
}
