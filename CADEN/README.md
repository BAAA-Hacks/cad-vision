# CADEN local chat

A standalone Streamlit chat window backed by Gemini. Includes multi-turn conversation,
New chat, loading feedback, and safe API errors. CAD metadata, GLB loading, and tools
are not connected in this milestone.

## Setup (PowerShell, Python 3.11+)

```powershell
cd C:\Users\agnco\cad-vision\CADEN
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Keep your existing `.env`. Set `GEMINI_API_KEY` there; `.env.example` lists optional
settings. `GOOGLE_API_KEY` is also accepted. Process environment values override
the corresponding `.env` values. Never commit the real key.

```powershell
.\.venv\Scripts\python.exe -m streamlit run app.py
```

Open http://127.0.0.1:8501. Stop with Ctrl+C. No virtual-environment activation is required.
The default model is `gemini-flash-latest`; set `GEMINI_MODEL` to a specific available
model if you want a pinned model. API usage is charged/limited according to your Google project.

History is held in Streamlit session memory, sent with each follow-up, and never saved
to disk by this app. New chat clears it. Reloading/disconnecting may also clear it.
Failed requests do not enter conversation history and can be retried.

## Structure

- `app.py`: chat UI and session history
- `config.py`: local settings
- `gemini_client.py`: API transport and error handling
- `prompts/system.md`: basic CADEN behavior

Tool declarations, metadata retrieval, and dispatch can be added separately later.

## Quick check

Run the offline UI checks with `.\.venv\Scripts\python.exe -m unittest -v`.

Send “Remember the word copper,” then ask “Which word did I give you?”
Click New chat and confirm the transcript clears. Ask about the assembly's mass:
CADEN should explain that no model metadata is connected.
