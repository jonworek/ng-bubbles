"""Import current macOS contacts through the local BlueBubbles API; never log PII."""
import datetime
import json
import pathlib
import re
import sqlite3
import sys
import urllib.parse
import urllib.request


def address(value):
    value = value.strip().lower()
    if '@' in value:
        return value
    digits = re.sub(r'\D', '', value)
    if len(digits) == 10 and not value.startswith('+'):
        digits = '1' + digits
    return digits


def addresses(contact):
    return {address(item if isinstance(item, str) else item.get('address', ''))
            for key in ('phoneNumbers', 'emails') for item in contact.get(key, [])} - {''}


def import_contacts(incoming, request):
    existing = request(None)['data']
    db = [item for item in existing if item.get('sourceType') == 'db']
    added = unchanged = conflicts = 0
    for contact in incoming:
        keys = addresses(contact)
        if not keys:
            continue
        matches = [item for item in db if addresses(item) & keys]
        same_name = [item for item in db if (item.get('displayName') or '').casefold() == contact['displayName'].casefold()]
        if matches:
            # Leave existing names and pictures intact; the API cannot safely update by ID.
            unchanged += 1
            continue
        if same_name:
            conflicts += 1
            continue
        response = request(contact)
        if response.get('metadata', {}).get('errors') or not response.get('data'):
            raise RuntimeError('Contact import rejected')
        db.extend(response['data'])
        added += 1
    return {'read': len(incoming), 'added': added, 'existing': unchanged, 'nameConflictsSkipped': conflicts}


def main():
    home = pathlib.Path.home()
    directory = home / 'Library/Application Support/NG Bubbles Contact Sync'
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    with sqlite3.connect('file:' + str(home / 'Library/Application Support/bluebubbles-server/config.db') + '?mode=ro', uri=True) as connection:
        settings = dict(connection.execute('select name,value from config where name in (?,?)', ('socket_port', 'password')))
    def setting(name):
        try:
            return json.loads(settings[name])
        except json.JSONDecodeError:
            return settings[name]
    query = urllib.parse.urlencode({'password': setting('password')})
    url = 'http://127.0.0.1:' + str(int(setting('socket_port'))) + '/api/v1/contact?' + query
    def request(payload):
        data = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(url, data=data, headers={'Content-Type': 'application/json'})
        # Credentials stay on loopback; redirects must not forward them elsewhere.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        with urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect()).open(request, timeout=120) as response:
            result = json.load(response)
        if result.get('status', 200) >= 400:
            raise RuntimeError('Local server request failed')
        return result
    result = import_contacts(json.load(sys.stdin), request)
    result.update(time=datetime.datetime.now(datetime.timezone.utc).isoformat(), status='success')
    target = directory / 'last-run.json'
    temporary = directory / 'last-run.tmp'
    temporary.write_text(json.dumps(result) + '\n')
    temporary.chmod(0o600)
    temporary.replace(target)
    print(json.dumps(result))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Exceptions from urllib can include the password-bearing URL.
        print(json.dumps({'status': 'failed', 'type': type(error).__name__}))
        sys.exit(1)
