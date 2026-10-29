#!/bin/bash
set -euo pipefail
source_directory="$(cd "$(dirname "$0")" && pwd)"
app="$HOME/Applications/NG Bubbles Contact Sync.app"
support="$HOME/Library/Application Support/NG Bubbles Contact Sync"
agent="$HOME/Library/LaunchAgents/com.jonworek.ng-bubbles.contact-sync.plist"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$support" "$HOME/Library/LaunchAgents"
chmod 700 "$support"
/usr/bin/swiftc "$source_directory/main.swift" -o "$app/Contents/MacOS/contact-sync" -framework AppKit -framework Contacts
cp "$source_directory/Info.plist" "$app/Contents/Info.plist"
cp "$source_directory/sync.py" "$app/Contents/Resources/sync.py"
/usr/bin/codesign --force --sign - "$app"
/usr/bin/python3 - "$app" "$agent" "$support" <<'PY'
import pathlib,plistlib,sys
app,agent,support=sys.argv[1:]
data={'Label':'com.jonworek.ng-bubbles.contact-sync','ProgramArguments':['/usr/bin/open','-W','-g',app],
      'StartInterval':3600,'RunAtLoad':True,
      'StandardOutPath':support+'/launch.log','StandardErrorPath':support+'/launch-error.log',
      'ProcessType':'Background','ThrottleInterval':300}
pathlib.Path(agent).write_bytes(plistlib.dumps(data))
PY
/usr/bin/plutil -lint "$agent"
launchctl bootout "gui/$(id -u)" "$agent" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$agent"
printf 'Installed hourly contact import. Approve Contacts access on the Mac when prompted.\n'
