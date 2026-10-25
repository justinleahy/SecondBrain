#!/usr/bin/env python3
"""Exercise synthetic meeting imports through a running isolated hardened daemon."""
import argparse
import http.cookiejar
import json
import pathlib
import time
import urllib.parse
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--origin', required=True)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    args = parser.parse_args()
    origin = urllib.parse.urlsplit(args.origin)
    if origin.hostname != '127.0.0.1' or not origin.port or not 18000 <= origin.port < 19000:
        raise ValueError('Use an isolated loopback qualification port.')
    client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    csrf = None

    def request(method, path, body=None, raw=None, headers=None):
        supplied = {'Origin': args.origin, **(headers or {})}
        if csrf and method != 'GET': supplied['X-CSRF-TOKEN'] = csrf
        if body is not None:
            raw = json.dumps(body).encode(); supplied['Content-Type'] = 'application/json'
        with client.open(urllib.request.Request(args.origin + path, raw, supplied, method=method), timeout=90) as response:
            return json.load(response)

    csrf = request('GET', '/auth/antiforgery')['token']
    request('POST', '/auth/login', {'password': 'compose-smoke-password'})
    csrf = request('GET', '/auth/antiforgery')['token']
    request('POST', '/auth/step-up', {'password': 'compose-smoke-password'})
    csrf = request('GET', '/auth/antiforgery')['token']
    prefix = 'm4-meetings-' + uuid.uuid4().hex[:10]

    def upload(text, name, metadata=None, key=None):
        boundary = 'fixture' + uuid.uuid4().hex
        raw = ('--' + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="' + name + '"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()
        raw += text.encode() + b'\r\n'
        if metadata:
            raw += ('--' + boundary + '\r\nContent-Disposition: form-data; name="metadata"\r\n\r\n' + json.dumps(metadata) + '\r\n').encode()
        raw += ('--' + boundary + '--\r\n').encode()
        headers = {'Content-Type': 'multipart/form-data; boundary=' + boundary}
        if key: headers['Idempotency-Key'] = key
        result = request('POST', '/v1/documents?wait=30', raw=raw, headers=headers)
        until = time.monotonic() + 45
        while result['document']['status'] not in ('indexed', 'indexed_partial', 'failed'):
            if time.monotonic() > until: raise TimeoutError('Meeting import did not finish.')
            time.sleep(.1); result['document'] = request('GET', '/v1/documents/' + result['document']['id'])
        return result['document']

    transcript = 'WEBVTT\n\n00:00.000 --> 00:03.000\n<v Ada>Pricing is forty credits per seat.</v>\n\n00:03.000 --> 00:05.000\n<v Bea>The rollout starts next week.</v>\n\n00:05.000 --> 00:08.000\n<v Ada>The pricing quote includes support.</v>\n'
    metadata = {'content': '', 'title': prefix + ' transcript', 'type': 'transcript',
                'props': {'participants': ['Ada', 'Bea'], 'started_at': '2026-10-24T13:00:00Z'}}
    first = upload(transcript, 'meeting.vtt', metadata, prefix)
    assert first['status'] == 'indexed' and first['type'] == 'transcript'
    repeated = upload(transcript, 'meeting.vtt', metadata, prefix)
    assert repeated['id'] == first['id'] and repeated['revision'] == first['revision']
    full = request('GET', '/v1/documents/' + first['id'] + '?include=text,segments')
    assert len(full['segments']) == 3 and [s['speaker'] for s in full['segments']] == ['Ada', 'Bea', 'Ada']
    results = request('POST', '/v1/search', {'query': 'pricing', 'mode': 'keyword',
                      'filters': {'type': 'transcript', 'people': [{'value': 'Ada', 'role': 'speaker'}]}})['results']
    selected = [row for row in results if row['document_id'] == first['id']]
    assert selected and all(row['transcript']['speaker'] == 'Ada' for row in selected)
    assert all(row['transcript']['start_ms'] in (0, 5000) for row in selected)
    srt = upload('1\n00:00:00,000 --> 00:00:01,000\nSynthetic SRT meeting.\n', 'meeting.srt',
                 {'content': '', 'title': prefix + ' SRT'})
    assert srt['status'] == 'indexed' and srt['type'] == 'transcript'
    calendar = ('BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//SecondBrain//Hardened fixture//EN\n'
        'BEGIN:VEVENT\nUID:' + prefix + '\nDTSTAMP:20261001T000000Z\nDTSTART:20261004T100000Z\nDTEND:20261004T110000Z\n'
        'RRULE:FREQ=WEEKLY;COUNT=3\nSUMMARY:' + prefix + '\nATTENDEE:mailto:ada@example.invalid\nEND:VEVENT\n'
        'BEGIN:VEVENT\nUID:' + prefix + '\nDTSTAMP:20261002T000000Z\nRECURRENCE-ID:20261011T100000Z\n'
        'DTSTART:20261011T120000Z\nDTEND:20261011T130000Z\nSTATUS:CANCELLED\nSUMMARY:' + prefix + ' cancelled\nEND:VEVENT\nEND:VCALENDAR\n')
    imported = upload(calendar, 'calendar.ics', {'content': '', 'title': prefix + ' calendar'})
    assert imported['status'] == 'indexed'
    query = {'type': 'calendar_event', 'occurred': {'from': '2026-10-01T00:00:00Z', 'to': '2026-11-01T00:00:00Z'}, 'direction': 'asc'}
    def events(value):
        result = request('POST', '/v1/query', value)
        return result, [item for item in result['records'] if item['title'].startswith(prefix)]
    page, active = events(query)
    assert len(active) == 2 and active[0]['id'] == active[1]['id'] and active[0]['instance_id'] != active[1]['instance_id']
    _, including_cancelled = events(dict(query, include_cancelled=True))
    assert len(including_cancelled) == 3 and sum(item['status'] == 'cancelled' for item in including_cancelled) == 1
    duplicate = upload(calendar, 'other-calendar.ics', {'content': '', 'title': prefix + ' duplicate'})
    assert duplicate['id'] != imported['id']
    _, duplicated = events(query); assert len(duplicated) == 4
    partial, _ = events(dict(query, occurred={'from': '1900-01-01T00:00:00Z', 'to': '2100-01-01T00:00:00Z'}))
    assert 'partial' in json.dumps(partial['coverage'])
    rejected = upload('BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:truncated', 'bad.ics')
    assert rejected['status'] == 'failed'
    _, preserved = events(query); assert len(preserved) == 4
    result = {'status': 'passed', 'syntheticData': True, 'hardenedDaemonHttp': True, 'transcriptId': first['id'],
        'transcriptSegments': 3, 'speakerAndTimestampFilters': True, 'idempotentTranscriptImport': True, 'srtImported': True,
        'calendarContainers': 2, 'confirmedOccurrences': 4, 'cancelledException': True,
        'separateContainerIdentity': True, 'partialCoverageReported': True, 'malformedCalendarPreservedAcceptedEvents': True,
        'realExportAcceptance': False, 'liveCalendarSubscription': False}
    args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
