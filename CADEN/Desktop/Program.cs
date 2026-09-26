using Core;
using Desktop.Configuration;
using Core.Tools;
using Core.Tools.Query;

namespace Desktop;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            string? directory = args.Length == 2 && args[0] == "--config-dir" ? args[1] : null;
            Application.Run(new ChatWindow(LocalConfiguration.FindDirectory(directory)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is ArgumentException ? ex.Message : "CADEN could not start (" + ex.GetType().Name + "). Check the installation.", "CADEN");
        }
    }
}

internal sealed class ChatWindow : Form
{
    private readonly string directory;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private ChatSession? session;
    private GeminiClient? gemini;
    private string? metadataPath;
    private CancellationTokenSource? request;
    private readonly RichTextBox transcript = new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.None, DetectUrls = false };
    private readonly TextBox input = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Message CADEN… (Ctrl+Enter to send)" };
    private readonly TextBox error = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ForeColor = Color.DarkRed, BackColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None };
    private readonly Label status = new() { AutoSize = true, Text = "Ready", Margin = new Padding(8) };
    private readonly Label model = new() { AutoSize = true, Margin = new Padding(8) };
    private readonly Label design = new() { AutoSize = true, Text = "No metadata loaded." };
    private readonly Button loadMetadata = new() { Text = "Load metadata", AutoSize = true };
    private readonly Button send = new() { Text = "Send", AutoSize = true };
    private readonly Button reset = new() { Text = "New chat", AutoSize = true };
    private readonly Button retry = new() { Text = "Retry", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private string? failedPrompt;

    public ChatWindow(string directory)
    {
        this.directory = directory;
        string defaultMetadata = Path.Combine(directory, "data", "metadata.json");
        if (File.Exists(defaultMetadata)) metadataPath = defaultMetadata;
        Text = "CADEN — C# Chat"; Size = new Size(900, 780); MinimumSize = new Size(620, 540);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 11); BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "CADEN", AutoSize = true, Font = new Font("Segoe UI", 19, FontStyle.Bold) });
        header.Controls.Add(model);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(design, 0, 1);
        layout.Controls.Add(transcript, 0, 2); layout.Controls.Add(error, 0, 3); layout.Controls.Add(input, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };
        actions.Controls.AddRange(new Control[] { send, reset, loadMetadata, retry, cancel, status });
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(new Label { Text = "History stays in memory. Messages are sent to Gemini. System instructions stay hidden.", AutoSize = true, ForeColor = Color.DimGray }, 0, 6);
        Controls.Add(layout);
        send.Click += async (_, _) => await SendAsync(input.Text);
        retry.Click += async (_, _) => await SendAsync(failedPrompt ?? "");
        reset.Click += (_, _) => Reload();
        loadMetadata.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Load CADEN metadata (starts a new chat)", Filter = "JSON metadata (*.json)|*.json", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                // Validate before replacing the active design or clearing its conversation.
                try { ReadMetadata(picker.FileName); metadataPath = picker.FileName; Reload(); }
                catch (Exception ex) { error.Text = ex is ToolInputException ? ex.Message : "Could not read metadata (" + ex.GetType().Name + ")."; }
            }
        };
        cancel.Click += (_, _) => request?.Cancel();
        input.KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (request == null) await SendAsync(input.Text); } };
        FormClosing += (_, _) => request?.Cancel();
        FormClosed += (_, _) => http.Dispose();
        Reload();
    }

    private void Reload()
    {
        transcript.Clear(); error.Clear(); input.Clear(); failedPrompt = null; retry.Enabled = false;
        session = null;
        try
        {
            var settings = LocalConfiguration.Load(directory);
            var metadata = metadataPath == null ? null : ReadMetadata(metadataPath);
            gemini = new GeminiClient(http, settings, QueryTools.Create(metadata));
            session = new ChatSession(gemini);
            design.Text = metadata == null ? "No metadata loaded — use Load metadata." : metadata.Name + " · " + metadata.Count + " objects" + (metadata.IsFixture ? " · synthetic fixture" : " · exported metadata");
            model.Text = settings.Model; status.Text = "Ready"; send.Enabled = true;
        }
        catch (Exception ex)
        {
            error.Text = ex is ArgumentException || ex is ToolInputException ? ex.Message : "Configuration could not be loaded (" + ex.GetType().Name + ").";
            send.Enabled = false; status.Text = "Configuration needed";
        }
    }

    private static MetadataStore ReadMetadata(string path)
    {
        if (new FileInfo(path).Length > 10000000) throw new ToolInputException("INVALID_METADATA", "Metadata exceeds the 10 MB prototype limit.");
        return new MetadataStore(File.ReadAllText(path));
    }

    private void RenderHistory()
    {
        transcript.Clear();
        if (session == null) return;
        foreach (var message in session.Messages)
        {
            if (message.Text.Length == 0) continue; // Structured tool exchanges stay in model context, not the visible transcript.
            transcript.SelectionColor = message.Role == "user" ? Color.DarkSlateBlue : Color.DarkGreen;
            transcript.AppendText(message.Role == "user" ? "YOU\n" : "CADEN\n");
            transcript.SelectionColor = Color.FromArgb(35, 35, 35);
            transcript.AppendText(message.Text + "\n\n");
        }
        transcript.SelectionStart = transcript.TextLength; transcript.ScrollToCaret();
    }

    private async Task SendAsync(string prompt)
    {
        if (session == null || request != null || string.IsNullOrWhiteSpace(prompt)) return;
        request = new CancellationTokenSource();
        send.Enabled = reset.Enabled = loadMetadata.Enabled = retry.Enabled = input.Enabled = false; cancel.Enabled = true;
        error.Clear(); status.Text = "CADEN is thinking…";
        RenderHistory(); transcript.AppendText("YOU\n" + prompt.Trim() + "\n\n");
        try
        {
            await session.SendAsync(prompt, request.Token);
            if (IsDisposed) return;
            failedPrompt = null; input.Clear(); RenderHistory(); status.Text = "Ready · " + (gemini?.LastToolCallCount ?? 0) + " queries";
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            failedPrompt = prompt; RenderHistory(); input.Text = prompt;
            error.Text = ex is OperationCanceledException ? "Request cancelled. This turn was not added to history."
                : ex is ChatException ? ex.Message : "Unexpected local error (" + ex.GetType().Name + "). Raw details withheld to protect credentials.";
            status.Text = ex is OperationCanceledException ? "Cancelled" : "Request failed";
        }
        finally
        {
            request.Dispose(); request = null;
            if (!IsDisposed)
            {
                send.Enabled = reset.Enabled = loadMetadata.Enabled = input.Enabled = true; retry.Enabled = failedPrompt != null;
                cancel.Enabled = false; input.Focus();
            }
        }
    }
}
