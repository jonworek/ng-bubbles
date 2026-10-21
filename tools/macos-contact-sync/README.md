# Daily macOS contact import

Optional server-side helper for BlueBubbles 1.9.9. Requires a logged-in Mac user,
Google Contacts already syncing into macOS Contacts, Xcode command-line tools,
and Python 3. It runs daily at **08:15 in the Mac's local timezone**, and at login.
A missed scheduled run is handled by launchd when the Mac wakes.

Run `bash install.sh` on the Mac as the user who runs BlueBubbles. Grant the named
**NG Bubbles Contact Sync** helper full Contacts access in the macOS permission
prompt (or System Settings → Privacy & Security → Contacts). Limited access only
imports the contacts macOS exposes. This does not automate Google's browser login.

The helper uses Apple's Contacts framework to read current names and addresses
in memory, then imports missing entries via BlueBubbles' loopback REST API. It
reads the local server password without logging or copying it. It does not restart
BlueBubbles, edit either application's contact database, delete contacts, or alter
existing names/photos. Existing server contacts are matched by normalized address;
ambiguous names are skipped. Renames, removals and photo changes remain managed
by BlueBubbles' native/import features. The Windows Sync button then retrieves the
new server entries; normal contact refresh also runs every five minutes.

Run manually:

```bash
open -W -g "$HOME/Applications/NG Bubbles Contact Sync.app"
cat "$HOME/Library/Application Support/NG Bubbles Contact Sync/last-run.json"
```

The status contains counts and timestamps, never names, addresses or credentials.
No analytics, external HTTP requests or Google credentials are added.

Uninstall the schedule (leaves the helper and imported contacts intact):

```bash
launchctl bootout "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.jonworek.ng-bubbles.contact-sync.plist"
rm "$HOME/Library/LaunchAgents/com.jonworek.ng-bubbles.contact-sync.plist"
```
