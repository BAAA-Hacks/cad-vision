"""Local configuration; never print credentials."""
import os
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import dotenv_values

BASE_DIR = Path(__file__).resolve().parent


@dataclass(frozen=True)
class Settings:
    api_key: str = field(repr=False)
    model: str
    timeout_seconds: int
    max_output_tokens: int


def load_settings() -> Settings:
    values = {**dotenv_values(BASE_DIR / '.env'), **os.environ}
    key = (values.get('GEMINI_API_KEY') or values.get('GOOGLE_API_KEY') or '').strip()
    if not key or key == 'your_api_key_here':
        raise ValueError('Set GEMINI_API_KEY in CADEN/.env, then reload this page.')
    model = (values.get('GEMINI_MODEL') or 'gemini-flash-latest').strip()
    if not model:
        raise ValueError('GEMINI_MODEL must not be blank.')
    try:
        timeout = int(values.get('GEMINI_TIMEOUT_SECONDS') or '60')
        tokens = int(values.get('GEMINI_MAX_OUTPUT_TOKENS') or '4096')
    except ValueError:
        raise ValueError('Timeout and output-token settings must be positive integers.') from None
    if timeout <= 0 or tokens <= 0:
        raise ValueError('Timeout and output-token settings must be positive integers.')
    return Settings(key, model, timeout, tokens)
