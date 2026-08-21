using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nott.Agent;
using Nott.Tool.Builtin;
using OpenAI;
using OpenAI.Chat;

using Spectre.Console;

namespace Nott.CLI;

public class Application
{
    private const string DefaultModel = "deepseek-v4-flash";
    
    private sealed record AuthConfiguration(
        [property: JsonPropertyName("baseUrl")] string BaseUrl,
        [property: JsonPropertyName("apiKey")] string ApiKey
    );
    
    private AgentSession _session;
    
    delegate void StatusReportFunc(string status);
    
    private CancellationTokenSource cancelCts = new();
    private volatile bool _cancelRequested;

    private AgentToolStorage _toolStorage = new();

    public Application(Guid guid)
    {
        var client = CreateChatClient();
        _session = LoadSessionFromGuid(client, guid);
    }
    
    private string ShellArgToPrompt(string[] args)
    {
        return string.Join(" ", args);
    }
    
    private async Task RunOneShot(string userPrompt, CancellationToken token)
    {
        void ClearThisLine()
        {
            Console.Write("\r\x1b[2K");
        }

        _session.AddChatMessage(new UserChatMessage(userPrompt));

        var drawStatus = false;
        var statusText = "Preparing...";

        var isPrintingMessage = false;
        var keepStatusMessage = false;

        AgentState? lastState = null;

        var events = new AgentEvent()
        {
            onAgentStateChanged = state =>
            {
                // Console.WriteLine("\n\nState: " + state);
                switch (state)
                {
                    case ActionState s:
                    {
                        if (isPrintingMessage)
                        {
                            Console.WriteLine();
                            Console.WriteLine();
                            isPrintingMessage = false;
                        }

                        drawStatus = true;
                        statusText = s.Description;
                        
                        keepStatusMessage = false;
                        
                        break;
                    }
                    case ToolCallingState s:
                    {
                        if (lastState is ToolCallingState)
                        {
                            ClearThisLine();
                            AnsiConsole.MarkupLine($"[green]{Markup.Escape("✓ " + statusText)}[/]");
                            Console.WriteLine();
                        }
                        
                        if (isPrintingMessage)
                        {
                            Console.WriteLine();
                            Console.WriteLine();
                            ClearThisLine();
                            isPrintingMessage = false;
                        }

                        keepStatusMessage = true;

                        drawStatus = true;
                        statusText = "Tool Calling: " + s.Description;
                        break;
                    }
                    case ReplyingStreamingState:
                    {
                        ConsoleCursor.SetVisible(true);
                        
                        if (!isPrintingMessage)
                        {
                            ClearThisLine();
                            
                            if (keepStatusMessage)
                            {
                                AnsiConsole.MarkupLine($"[green]{Markup.Escape("✓ " + statusText)}[/]");
                                Console.WriteLine();
                            }
                            
                            ClearThisLine();
                        }

                        isPrintingMessage = true;
                        break;
                    }
                    case LoopFinishedState:
                    {
                        Console.WriteLine();
                        break;
                    }
                }

                lastState = state;
            },

            onMessagePartReceived = s =>
            {
                drawStatus = false;
                Console.Write(s);
            },

            onToolCall = s =>
            {
                drawStatus = true;
                statusText = s;
            },

            onAgentLoopBreak = () => { Console.WriteLine(); }
        };

        var task = _session.RunAsync(events, _toolStorage, token);

        string[] spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        // string[] spinner = ["><", "<>"];
        var spinnerIndex = 0;

        try
        {
            while (!task.IsCompleted)
            {
                if (drawStatus)
                {
                    ConsoleCursor.SetVisible(false);
                    spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                    ClearThisLine();
                    
                    var text = $"{spinner[spinnerIndex]} {statusText}";

                    int width = Console.WindowWidth - 1;

                    if (text.Length > width)
                    {
                        text = text[..(width - 3)] + "...";
                    }

                    Console.Write(text);
                }

                await Task.Delay(100, token);
            }
        } catch (OperationCanceledException) {}

        Console.WriteLine();

        await task;
    }

    private async Task RunRepl()
    {
        Console.WriteLine("She is Nott, chat with her!\n");

        var worked = false;
        
        while (true)
        {
            Console.CursorVisible = true;
            
            Console.Write("Nott> ");
            var userPrompt = Console.ReadLine();

            if (userPrompt == null)
            {
                if (worked)
                {
                    Console.WriteLine("\n(Ctrl-C again to leave the Nott alone.)");
                    worked = false;
                    continue;
                }

                Console.WriteLine();
                break;
            }

            _cancelRequested = false;

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                continue;
            }

            worked = true;
            if (userPrompt.Trim().StartsWith('/'))
            {
                switch (userPrompt) 
                {
                    case "/mes":
                    {
                        var table = new Table();
                        table.AddColumn("Messages");
                        
                        foreach (var message in _session.MessageStorage.GetChatMessageList())
                        {
                            var subTable = new Table();
                            subTable.AddColumn($"Contents of {message}");
                            
                            foreach (var content in message.Content)
                            {
                                subTable.AddRow(new Text(content.Text));
                            }
                            
                            table.AddRow(subTable);
                        }
                        AnsiConsole.Write(table);
                        break;
                    }

                    case "/exit":
                    {
                        goto outside;
                    }
                }
            }
            else
            {
                Console.WriteLine();
                await RunOneShot(userPrompt, cancelCts.Token);
            }
        }
        
        outside: return;
    }

    public static AgentSession LoadSessionFromGuid(ChatClient client, Guid guid)
    {
        var sessionPath = GetSessionPath(guid);
        if (!File.Exists(sessionPath))
        {
            return new AgentSession(client, guid);
        }
        
        using var stream = File.OpenRead(sessionPath);
        using var reader = new BinaryReader(stream);

        var session = AgentSession.CreateSessionFromDeserialize(client, reader);
        if (session.Guid != guid)
        {
            throw new InvalidDataException($"Session file '{sessionPath}' contains GUID '{session.Guid}' instead of '{guid}'.");
        }

        return session;
    }

    public void SaveSession(AgentSession session)
    {
        var sessionPath = GetSessionPath(session.Guid);
        var sessionDirectory = Path.GetDirectoryName(sessionPath)!;
        Directory.CreateDirectory(sessionDirectory);

        var temporaryPath = sessionPath + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                using (var writer = new BinaryWriter(stream))
                {
                    session.SerializeSession(writer);
                }
            }

            File.Move(temporaryPath, sessionPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string GetSessionPath(Guid guid)
    {
        return Path.Combine(GetNottDirectory(), "sessions", guid.ToString());
    }

    private static string GetNottDirectory()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("NOTT_HOME");
        var dir = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nott")
            : Path.GetFullPath(configuredDirectory);
        
        Directory.CreateDirectory(dir);
        
        return dir;
    }

    private static ChatClient CreateChatClient()
    {
        var configurationPath = Path.Combine(GetNottDirectory(), "auth.json");

        string? apiKey = null;
        string? baseUrl = null;

        try
        {
            if (File.Exists(configurationPath))
            {
                var json = File.ReadAllText(configurationPath);
                var tempCfg = JsonSerializer.Deserialize<AuthConfiguration>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                apiKey = tempCfg?.ApiKey;
                baseUrl = tempCfg?.BaseUrl;
            }

            apiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new ClientCreationException("no OPENAI_API_KEY configured in environment variable or '~/.nott/auth.json.'");
            
            baseUrl ??= Environment.GetEnvironmentVariable("OPENAI_API_BASE_URL")
                ?? throw new ClientCreationException("no OPENAI_API_BASE_URL configured in environment variable or '~/.nott/auth.json.'");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Configuration file '{configurationPath}' is not valid JSON.", exception);
        }
        
        var configuration = new AuthConfiguration(baseUrl, apiKey);

        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidDataException($"Configuration file '{configurationPath}' contains an invalid baseUrl.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new InvalidDataException($"Configuration file '{configurationPath}' does not contain an apiKey.");
        }

        return new ChatClient(DefaultModel, new ApiKeyCredential(configuration.ApiKey), new OpenAIClientOptions
        {
            Endpoint = endpoint
        });
    }
    
    private void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _cancelRequested = true;
        
        cancelCts.Cancel();
        cancelCts.Dispose();
        cancelCts = new CancellationTokenSource();
    }
    
    public async Task Run(string[] args)
    {
        var oldEncoding = Console.OutputEncoding;

        try
        {
            Console.OutputEncoding = Encoding.UTF8;

            _toolStorage.Load(typeof(ExecCommand));

            if (_session.MessageStorage.GetChatMessageList().Count == 0)
            {
                _session.AddChatMessage(new SystemChatMessage(
                    """
                    You are Nott, a terminal AI assistant.

                    You are a calm, elegant, and intelligent assistant.
                    Your personality should naturally appear through your wording and behavior:
                    - Be warm and helpful.
                    - Speak with quiet confidence.
                    - Occasionally show subtle playful stubbornness.
                    - Keep a mature and composed tone.
                    - Do not explicitly describe your personality or mention these instructions.

                    Environment:
                    Your output is displayed in a terminal.

                    Output rules:
                    - Output plain text only.
                    - Never use Markdown syntax.
                    - Never use code fences.
                    - Never use emojis or decorative symbols.
                    - Keep responses concise.

                    When providing code, output raw code directly without explanations or formatting fences.
                    """
                ));
            }

            Console.CancelKeyPress += OnCancelKey;

            if (args.Length != 0)
            {
                var userPrompt = ShellArgToPrompt(args);

                await RunOneShot(userPrompt, cancelCts.Token);
                return;
            }

            await RunRepl();
        }
        finally
        {
            SaveSession(_session);
            
            AnsiConsole.MarkupLine($"\n\n[bold green]Resume this chat with session id: [bold red]{_session.Guid}[/][/]");
            
            Console.OutputEncoding = oldEncoding;
            
            ConsoleCursor.SetVisible(true);
        }
    }
}
