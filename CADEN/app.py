"""Run with: python -m streamlit run app.py"""
import streamlit as st

from config import load_settings
from gemini_client import ChatError, reply

st.set_page_config(page_title='CADEN', page_icon='💬', layout='centered')
st.title('CADEN')
st.caption('Your CAD Vision assistant · Gemini chat')

try:
    settings = load_settings()
except ValueError as exc:
    st.error(str(exc))
    st.stop()

if 'messages' not in st.session_state:
    st.session_state.messages = []

with st.sidebar:
    st.subheader('CADEN')
    st.caption(f'Model: {settings.model}')
    st.write('Chat only. CAD metadata and visualization tools are not connected yet.')
    if st.button('New chat', use_container_width=True):
        st.session_state.messages = []
        st.session_state.pop('failed_prompt', None)
        st.session_state.pop('request_error', None)
        st.rerun()
    st.caption('Messages are sent to Gemini. History stays in this browser session and is not saved to disk.')

for message in st.session_state.messages:
    with st.chat_message(message['role']):
        st.markdown(message['content'])

retry = False
if 'failed_prompt' in st.session_state:
    st.error(st.session_state.get('request_error', 'The previous request failed.'))
    with st.expander('Previous message'):
        st.write(st.session_state.failed_prompt)
    retry = st.button('Retry last message')

entered = st.chat_input('Message CADEN…')
prompt = st.session_state.get('failed_prompt') if retry else entered
if prompt and prompt.strip():
    prompt = prompt.strip()
    with st.chat_message('user'):
        st.markdown(prompt)
    try:
        with st.chat_message('assistant'):
            with st.spinner('CADEN is thinking…'):
                answer = reply(settings, st.session_state.messages, prompt)
            st.markdown(answer)
    except ChatError as exc:
        st.session_state.failed_prompt = prompt
        st.session_state.request_error = str(exc)
        st.rerun()
    except Exception as exc:
        st.session_state.failed_prompt = prompt
        st.session_state.request_error = (
            f'Unexpected local error ({type(exc).__name__}). '
            'Check the installed dependencies and local configuration. '
            'Raw exception details are withheld because they may contain credentials.'
        )
        st.rerun()
    else:
        st.session_state.messages.extend([
            {'role': 'user', 'content': prompt},
            {'role': 'assistant', 'content': answer},
        ])
        st.session_state.pop('failed_prompt', None)
        st.session_state.pop('request_error', None)
        st.rerun()
