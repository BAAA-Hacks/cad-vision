"""Offline checks: python -m unittest -v"""
import unittest
from unittest.mock import patch
from types import SimpleNamespace
from urllib.parse import quote

from streamlit.testing.v1 import AppTest

from config import BASE_DIR, Settings
from gemini_client import ChatError, api_error_message


class ChatTests(unittest.TestCase):
    def test_api_error_explains_invalid_key_and_redacts_secrets(self):
        key = 'private/key+value'
        settings = Settings(key, 'test-model', 60, 4096)
        error = SimpleNamespace(
            code=400, status='INVALID_ARGUMENT',
            message=f'API key not valid: {key}, {quote(key, safe="")}, AIzaAnotherSecret123',
        )
        message = api_error_message(error, settings)
        self.assertIn('HTTP 400 / INVALID_ARGUMENT', message)
        self.assertIn('Model: test-model', message)
        self.assertIn('API key not valid', message)
        self.assertIn('Set a valid Gemini API key', message)
        for secret in (key, quote(key, safe=''), 'AIzaAnotherSecret123'):
            self.assertNotIn(secret, message)

    def test_quota_error_has_specific_guidance(self):
        settings = Settings('test-key', 'test-model', 60, 4096)
        message = api_error_message(SimpleNamespace(
            code=429, status='RESOURCE_EXHAUSTED', message='Quota exceeded.'
        ), settings)
        self.assertIn('Quota exceeded.', message)
        self.assertIn('quota, and billing', message)

    def test_followup_reset_and_failure_retry(self):
        settings = Settings('test-key', 'test-model', 60, 4096)
        with patch('config.load_settings', return_value=settings), patch('gemini_client.reply') as mock:
            app = AppTest.from_file(str(BASE_DIR / 'app.py')).run()
            self.assertFalse(app.exception)
            mock.return_value = 'I will remember copper.'
            app.chat_input[0].set_value('Remember copper').run()
            self.assertEqual(len(app.chat_message), 2)
            mock.return_value = 'Copper.'
            app.chat_input[0].set_value('Which word?').run()
            history = mock.call_args.args[1]
            self.assertEqual(history[0]['content'], 'Remember copper')
            self.assertEqual(len(app.chat_message), 4)
            mock.side_effect = ChatError('Simulated timeout')
            app.chat_input[0].set_value('Try this').run()
            self.assertEqual(len(app.session_state['messages']), 4)
            self.assertEqual(app.error[0].value, 'Simulated timeout')
            mock.side_effect = None
            mock.return_value = 'Retried successfully.'
            next(b for b in app.button if b.label == 'Retry last message').click().run()
            self.assertEqual(len(app.chat_message), 6)
            self.assertFalse(app.error)
            next(b for b in app.button if b.label == 'New chat').click().run()
            self.assertEqual(len(app.chat_message), 0)
            self.assertFalse(app.exception)

    def test_missing_key(self):
        with patch('config.load_settings', side_effect=ValueError('Set GEMINI_API_KEY')):
            app = AppTest.from_file(str(BASE_DIR / 'app.py')).run()
            self.assertEqual(app.error[0].value, 'Set GEMINI_API_KEY')
            self.assertEqual(len(app.chat_input), 0)
            self.assertFalse(app.exception)


if __name__ == '__main__':
    unittest.main()
