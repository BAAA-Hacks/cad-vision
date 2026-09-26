using Core;
using Desktop.Configuration;
using Core.Tools;
using Core.Tools.Query;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Project;
using Core.Diagnostics;

namespace Desktop;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var diagnostics = new FileDiagnostics(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CADEN", "logs"));
        DiagnosticLog.Configure(diagnostics.Write, diagnostics.DirectoryPath);
        Application.ThreadException += (_, e) => MessageBox.Show(DiagnosticLog.Report(e.Exception, "desktop.ui").UserMessage, "CADEN error");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Report(e.ExceptionObject as Exception ?? new Exception("Unknown unhandled failure."), "desktop.unhandled");
        try
        {
            string? directory = args.Length == 2 && args[0] == "--config-dir" ? args[1] : null;
            Application.Run(new ChatWindow(LocalConfiguration.FindDirectory(directory)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(DiagnosticLog.Report(ex, "desktop.startup").UserMessage, "CADEN");
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
    private ProjectSnapshot? currentSnapshot;
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
                try { Reload(picker.FileName); }
                catch (Exception ex) { error.Text = DiagnosticLog.Report(ex, "desktop.load_metadata").UserMessage; }
            }
        };
        cancel.Click += (_, _) => request?.Cancel();
        input.KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (request == null) await SendAsync(input.Text); } };
        FormClosing += (_, _) => request?.Cancel();
        DiagnosticLog.Reported += ShowDiagnostic;
        FormClosed += (_, _) => { DiagnosticLog.Reported -= ShowDiagnostic; http.Dispose(); };
        Reload();
    }

    private void ShowDiagnostic(DiagnosticReceipt receipt)
    {
        if (IsDisposed || Disposing) return;
        void Display()
        {
            if (IsDisposed || Disposing) return;
            error.AppendText((error.TextLength == 0 ? "" : Environment.NewLine + Environment.NewLine) + receipt.UserMessage);
        }
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke((Action)Display); }
        else Display();
    }

    private void Reload(string? replacementPath = null)
    {
        try
        {
            var settings = LocalConfiguration.Load(directory);
            string? nextPath = replacementPath ?? metadataPath;
            var metadata = nextPath == null ? null : ReadMetadata(nextPath);
            var association = ProjectAssociationFile.LoadOrCreate(directory);
            var nextClient = new GeminiClient(http, settings, SemanticQueryTools.Create(metadata, association));
            transcript.Clear(); error.Clear(); input.Clear(); failedPrompt = null; retry.Enabled = false;
            metadataPath = nextPath; gemini = nextClient; currentSnapshot = metadata;
            session = new ChatSession(gemini);
            design.Text = metadata == null ? "No metadata loaded — use Load metadata." : metadata.Name + " · " + metadata.ComponentsById.Count + " objects" + (metadata.IsFixture ? " · synthetic fixture" : " · exported metadata") + " · hierarchy " + metadata.Capabilities.Hierarchy;
            model.Text = settings.Model; status.Text = "Ready"; send.Enabled = true;
            if (metadata != null && metadata.LoadDiagnostics.Any(d => d.IsError))
                DiagnosticLog.Report(new InvalidDataException(string.Join(Environment.NewLine, metadata.LoadDiagnostics.Where(d => d.IsError).Select(d => d.Code + " at " + d.Path + ": " + d.Message))), "metadata.degraded_load", metadata.ProjectId, metadata.SnapshotId);
        }
        catch (Exception ex)
        {
            error.Text = DiagnosticLog.Report(ex, "desktop.reload").UserMessage;
            send.Enabled = session != null; status.Text = session == null ? "Configuration needed" : "Reload failed; previous chat retained";
        }
    }

    private static ProjectSnapshot ReadMetadata(string path)
    {
        if (new FileInfo(path).Length > 10000000) throw new ToolInputException("INVALID_METADATA", "Metadata exceeds the 10 MB prototype limit.");
        var loaded = LoadProject.Load(File.ReadAllText(path));
        if (!loaded.Success) throw new ToolInputException("INVALID_METADATA", string.Join(Environment.NewLine, loaded.Diagnostics.Where(d => d.Fatal).Take(8).Select(d => d.Code + ": " + d.Message)));
        return loaded.Snapshot!;
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
                : ex is ChatException chatFailure && chatFailure.DiagnosticId != null ? chatFailure.Message
                : DiagnosticLog.Report(ex, "desktop.send", currentSnapshot?.ProjectId, currentSnapshot?.SnapshotId).UserMessage;
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
