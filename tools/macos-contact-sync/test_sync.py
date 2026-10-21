import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location('contact_sync', pathlib.Path(__file__).with_name('sync.py'))
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class ContactSyncTests(unittest.TestCase):
    def test_new_contact_import_is_idempotent_and_preserves_existing_photos(self):
        records = [{'displayName': 'Existing', 'sourceType': 'db', 'phoneNumbers': [{'address': '+15551234567'}], 'avatar': 'keep'}]
        writes = []
        def request(payload):
            if payload is None:
                return {'data': records[:]}
            writes.append(payload)
            result = {**payload, 'sourceType': 'db'}
            records.append(result)
            return {'data': [result]}
        incoming = [
            {'displayName': 'Existing renamed', 'phoneNumbers': ['(555) 123-4567']},
            {'displayName': 'New person', 'phoneNumbers': ['+15559876543']},
        ]
        self.assertEqual(1, sync.import_contacts(incoming, request)['added'])
        self.assertEqual(0, sync.import_contacts(incoming, request)['added'])
        self.assertEqual(1, len(writes))
        self.assertEqual('keep', records[0]['avatar'])
        self.assertEqual('Existing', records[0]['displayName'])

    def test_ambiguous_names_and_contacts_without_addresses_are_skipped(self):
        incoming = [{'displayName': 'Same name', 'emails': ['new@example.test']}, {'displayName': 'No address'}]
        def request(payload):
            self.assertIsNone(payload)
            return {'data': [{'displayName': 'Same name', 'emails': ['old@example.test'], 'sourceType': 'db'}]}
        result = sync.import_contacts(incoming, request)
        self.assertEqual(0, result['added'])
        self.assertEqual(1, result['nameConflictsSkipped'])
        self.assertNotEqual(sync.address('+445551234567'), sync.address('+15551234567'))

    def test_partial_server_failures_are_not_reported_as_success(self):
        def request(payload):
            return {'data': []} if payload is None else {'data': [], 'metadata': {'errors': [{}]}}
        with self.assertRaises(RuntimeError):
            sync.import_contacts([{'displayName': 'New person', 'phoneNumbers': ['+15559876543']}], request)


if __name__ == '__main__':
    unittest.main()
