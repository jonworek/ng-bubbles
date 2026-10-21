import AppKit
import Contacts
import Foundation

final class SyncDelegate: NSObject, NSApplicationDelegate {
    let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/NG Bubbles Contact Sync")
    func report(_ status: String) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let data = try? JSONSerialization.data(withJSONObject: ["time": ISO8601DateFormatter().string(from: Date()), "status": status])
        try? data?.write(to: directory.appendingPathComponent("last-run.json"), options: .atomic)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            do {
                let store = CNContactStore()
                guard try await store.requestAccess(for: .contacts) else {
                    report("Contacts permission required. Enable NG Bubbles Contact Sync in System Settings > Privacy & Security > Contacts.")
                    NSApplication.shared.terminate(nil); return
                }
                let keys = [CNContactIdentifierKey, CNContactGivenNameKey, CNContactFamilyNameKey, CNContactOrganizationNameKey, CNContactPhoneNumbersKey, CNContactEmailAddressesKey] as [CNKeyDescriptor]
                var contacts: [[String: Any]] = []
                try store.enumerateContacts(with: CNContactFetchRequest(keysToFetch: keys)) { contact, _ in
                    let display = [contact.givenName, contact.familyName].filter { !$0.isEmpty }.joined(separator: " ")
                    let name = display.isEmpty ? contact.organizationName : display
                    if name.isEmpty { return }
                    contacts.append(["firstName": contact.givenName, "lastName": contact.familyName, "displayName": name,
                        "phoneNumbers": contact.phoneNumbers.map { $0.value.stringValue },
                        "emails": contact.emailAddresses.map { $0.value as String }])
                }
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
                process.arguments = [Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/sync.py").path]
                let pipe = Pipe(); process.standardInput = pipe
                try process.run()
                let payload = try JSONSerialization.data(withJSONObject: contacts)
                pipe.fileHandleForWriting.write(payload); try pipe.fileHandleForWriting.close()
                process.waitUntilExit()
                if process.terminationStatus != 0 { report("Local BlueBubbles import failed. Will retry at the next scheduled run.") }
            } catch { report("Contact refresh failed (\(type(of: error))). Will retry at the next scheduled run.") }
            NSApplication.shared.terminate(nil)
        }
    }
}
let application = NSApplication.shared
let delegate = SyncDelegate()
application.delegate = delegate
application.setActivationPolicy(.accessory)
application.run()
