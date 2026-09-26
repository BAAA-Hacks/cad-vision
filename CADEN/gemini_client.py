"""Gemini transport, independent of Streamlit and future CAD tools."""
import re
from urllib.parse import quote

import httpx
from google import genai
from google.genai import errors, types

from config import BASE_DIR, Settings


class ChatError(Exception):
    """A safe, user-facing error that does not expose API payloads."""


def redact(value: object, settings: Settings) -> str:
    text = str(value or '')
    for secret in (settings.api_key, quote(settings.api_key, safe='')):
        if secret:
            text = text.replace(secret, '[REDACTED]')
    text = re.sub(r'AIza[\w-]+', '[REDACTED]', text)
    return text[:2000]


def api_error_message(exc: errors.APIError, settings: Settings) -> str:
    detail = redact(exc.message, settings) or 'Google supplied no explanation.'
    hints = {
        400: 'Check the explanation below for an invalid key, request parameter, or unmet project requirement.',
        401: 'Check GEMINI_API_KEY in CADEN/.env and any overriding environment variable.',
        403: 'Check API key restrictions and your Google project permissions.',
        404: 'Check GEMINI_MODEL and whether this model is available to your project.',
        429: 'Check Google AI Studio usage, quota, and billing. If rate-limited, wait before retrying.',
    }
    hint = hints.get(exc.code, 'Check the explanation below; retry later for a temporary service failure.')
    if 'api key' in detail.lower() and any(word in detail.lower() for word in ('invalid', 'not valid', 'expired')):
        hint = 'Set a valid Gemini API key in CADEN/.env. A process environment variable can override that file.'
    return (
        f'Gemini request failed — HTTP {exc.code} / {redact(exc.status, settings) or "UNKNOWN"}\n\n'
        f'Model: {redact(settings.model, settings)}\n\n'
        f'Google explanation: {detail}\n\n'
        f'Next step: {hint}'
    )


def reply(settings: Settings, history: list[dict[str, str]], prompt: str) -> str:
    contents = [
        types.Content(
            role='model' if message['role'] == 'assistant' else 'user',
            parts=[types.Part.from_text(text=message['content'])],
        )
        for message in history
    ]
    contents.append(types.Content(role='user', parts=[types.Part.from_text(text=prompt)]))
    try:
        with genai.Client(
            api_key=settings.api_key,
            http_options=types.HttpOptions(timeout=settings.timeout_seconds * 1000),
        ) as client:
            result = client.models.generate_content(
                model=settings.model,
                contents=contents,
                config=types.GenerateContentConfig(
                    system_instruction=(BASE_DIR / 'prompts' / 'system.md').read_text(encoding='utf-8'),
                    max_output_tokens=settings.max_output_tokens,
                ),
            )
    except errors.APIError as exc:
        raise ChatError(api_error_message(exc, settings)) from None
    except httpx.TimeoutException as exc:
        raise ChatError(
            f'Gemini request timed out ({type(exc).__name__}). '
            f'The configured request timeout is {settings.timeout_seconds} seconds. '
            'Retry, or increase GEMINI_TIMEOUT_SECONDS in CADEN/.env.'
        ) from None
    except httpx.TransportError as exc:
        raise ChatError(
            f'Could not connect to Gemini ({type(exc).__name__}). '
            'Check your internet connection, proxy, firewall, and TLS certificates.\n\n'
            f'Details: {redact(exc, settings)}'
        ) from None
    text = result.text
    if not text or not text.strip():
        raise ChatError('Gemini returned no text. The response may have been blocked or exhausted its output limit. Try rephrasing.')
    if result.candidates and str(result.candidates[0].finish_reason).endswith('MAX_TOKENS'):
        text += '\n\n*Response reached the output limit; ask me to continue.*'
    return text
