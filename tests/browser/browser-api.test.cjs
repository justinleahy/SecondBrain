const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/SecondBrain.Server/wwwroot/browser-api.js'), 'utf8');

function fixture(responses) {
    const requests = [], navigations = [];
    const context = vm.createContext({ window: {}, URL, location: { origin: 'https://private.example', assign: path => navigations.push(path) }, fetch: async (url, options) => {
        requests.push({ url: String(url), options });
        const response = responses.shift();
        assert.ok(response, 'Unexpected request');
        return { ok: response.status >= 200 && response.status < 300, status: response.status,
            json: async () => response.body, text: async () => response.body === undefined ? '' : JSON.stringify(response.body),
            headers: { get: name => name === 'Link' ? response.link : null } };
    }});
    vm.runInContext(source, context);
    return { api: context.window.secondBrainApi, requests, navigations };
}

test('every mutation fetches fresh CSRF authority and sends the expected revision', async () => {
    const { api, requests } = fixture([
        { status: 200, body: { token: 'fresh-one', headerName: 'X-CSRF-TOKEN' } }, { status: 200, body: { revision: 2 } },
        { status: 200, body: { token: 'fresh-two', headerName: 'X-CSRF-TOKEN' } }, { status: 204 }
    ]);
    await api.send('/documents/a', 'PATCH', { content: 'draft' }, 1, null);
    await api.send('/auth/logout', 'POST', null, null, null);
    assert.equal(requests[1].options.headers['X-CSRF-TOKEN'], 'fresh-one');
    assert.equal(requests[1].options.headers['If-Match'], '"1"');
    assert.equal(requests[3].options.headers['X-CSRF-TOKEN'], 'fresh-two');
    for (const request of requests) {
        assert.equal(request.options.credentials, 'same-origin');
        assert.equal(request.options.redirect, 'error');
        assert.equal(request.options.cache, 'no-store');
    }
});

test('a failed CSRF request prevents the mutation', async () => {
    const { api, requests } = fixture([{ status: 401 }]);
    const result = await api.send('/documents/a', 'DELETE', null, 1, null);
    assert.equal(result.status, 401);
    assert.equal(requests.length, 1);
});

test('absolute, scheme-relative and external paths are rejected before any request', async () => {
    const { api, requests } = fixture([]);
    for (const path of ['https://attacker.example/x', '//attacker.example/x', 'relative', '/\\attacker.example/x']) {
        await assert.rejects(api.send(path, 'GET', null, null, null));
    }
    assert.equal(requests.length, 0);
});

test('reads preserve continuation links and do not fetch CSRF tokens', async () => {
    const { api, requests } = fixture([{ status: 200, body: [], link: '</auth/sessions?after=opaque>; rel="next"' }]);
    const result = await api.send('/auth/sessions', 'GET', null, null, null);
    assert.equal(result.next, '/auth/sessions?after=opaque');
    assert.equal(requests.length, 1);
});

test('successful session rotation navigates in the browser before returning to a revoked circuit', async () => {
    const { api, navigations } = fixture([
        { status: 200, body: { token: 'fresh', headerName: 'X-CSRF-TOKEN' } }, { status: 200, body: { id: 'rotated' } }
    ]);
    await api.sendAndNavigate('/auth/step-up', 'POST', { password: 'fixture' }, '/settings');
    assert.deepEqual(navigations, ['/settings']);
});

test('failed step-up does not navigate, and cross-origin navigation is rejected before mutation', async () => {
    const { api, requests, navigations } = fixture([
        { status: 200, body: { token: 'fresh', headerName: 'X-CSRF-TOKEN' } }, { status: 401 }
    ]);
    await api.sendAndNavigate('/auth/step-up', 'POST', { password: 'fixture' }, '/settings');
    assert.deepEqual(navigations, []);
    await assert.rejects(api.sendAndNavigate('/auth/logout', 'POST', null, '//external.example'));
    assert.equal(requests.length, 2);
});
