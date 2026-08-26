using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Markdig.Wpf;
using PCL.Core.AI;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils.Exts;

namespace PCL;

public partial class PageToolsAi
{
    private readonly List<AiChatMessage> _history = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _isShowingKey;
    private string? _sessionPath;
    private bool _isLoadingSessions;

    public PageToolsAi()
    {
        InitializeComponent();
        Loaded += PageToolsAi_Loaded;
    }

    private void PageToolsAi_Loaded(object sender, RoutedEventArgs e)
    {
        if (TextEndpoint.Text.Length > 0)
            return; // 已加载过
        TextEndpoint.Text = Config.Ai.Endpoint;
        TextModel.Text = Config.Ai.Model;
        _ApplyKeyMask();
        _RefreshSessionList();
        if (PanChatList.Children.Count == 0)
            _AddBubble(Lang.Text("Tools.Ai.Chat.EmptyHint"), isUser: false, isStatus: true);
    }

    #region 设置

    private void _ApplyKeyMask()
    {
        if (Config.Ai.ApiKey.IsNullOrEmpty())
        {
            TextApiKey.Text = "";
            _isShowingKey = true;
            return;
        }
        _isShowingKey = false;
        TextApiKey.Text = "••••••••";
    }

    private void BtnShowKey_Click(object sender, MouseButtonEventArgs e)
    {
        _isShowingKey = !_isShowingKey;
        TextApiKey.Text = _isShowingKey ? Config.Ai.ApiKey : "••••••••";
    }

    private void _SaveConfig()
    {
        Config.Ai.Endpoint = TextEndpoint.Text.Trim();
        Config.Ai.Model = TextModel.Text.Trim();
        if (_isShowingKey)
            Config.Ai.ApiKey = TextApiKey.Text;
    }

    private void BtnSave_Click(object sender, MouseButtonEventArgs e)
    {
        _SaveConfig();
        HintService.Hint(Lang.Text("Tools.Ai.Setting.Saved"), HintType.Success);
    }

    private async void BtnTest_Click(object sender, MouseButtonEventArgs e)
    {
        _SaveConfig();
        if (Config.Ai.ApiKey.IsNullOrEmpty())
        {
            HintService.Hint(Lang.Text("Tools.Ai.Setting.NoKey"), HintType.Error);
            return;
        }
        BtnTest.IsEnabled = false;
        try
        {
            var client = ModAi.CreateClient();
            var completion = await client.CompleteAsync(
            [
                AiChatMessage.System("你是连接测试助手，只回复一个词。"),
                AiChatMessage.User("请回复：正常")
            ]);
            if (string.IsNullOrWhiteSpace(completion.Choice.Content))
                throw new Exception(Lang.Text("Tools.Ai.Setting.TestFailedEmpty"));
            HintService.Hint(Lang.Text("Tools.Ai.Setting.TestSuccess", completion.Choice.Content), HintType.Success);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 连接测试失败");
            HintService.Hint(Lang.Text("Tools.Ai.Setting.TestFailed", ex.Message), HintType.Error);
        }
        finally
        {
            BtnTest.IsEnabled = true;
        }
    }

    #endregion

    #region 对话

    private void BtnSkillCore_Click(object sender, MouseButtonEventArgs e) =>
        TextInput.Text = Lang.Text("Tools.Ai.Skill.Core.Prompt");

    private void BtnSkillMod_Click(object sender, MouseButtonEventArgs e) =>
        TextInput.Text = Lang.Text("Tools.Ai.Skill.Mod.Prompt");

    private void BtnSkillKeybind_Click(object sender, MouseButtonEventArgs e) =>
        TextInput.Text = Lang.Text("Tools.Ai.Skill.Keybind.Prompt");

    private void TextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            BtnSend_Click(sender, null!);
        }
    }

    /// <summary>单行输入框基准高度。</summary>
    private const double InputBaseHeight = 28;

    /// <summary>输入框最大高度。</summary>
    private const double InputMaxHeight = 120;

    private double? _inputLineHeight;

    /// <summary>
    /// 每次换行高度恰好增加一行（用实际字体行高计算，避免模板 MinHeight 造成的增量偏差）。
    /// </summary>
    private void TextInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _inputLineHeight ??= _MeasureLineHeight();
        var lines = _CountLines(TextInput.Text);
        TextInput.Height = Math.Min(InputBaseHeight + (lines - 1) * _inputLineHeight.Value, InputMaxHeight);
    }

    /// <summary>用输入框实际字体测量一行文字的高度。</summary>
    private double _MeasureLineHeight()
    {
        var typeface = new Typeface(TextInput.FontFamily, TextInput.FontStyle, TextInput.FontWeight, TextInput.FontStretch);
        return new FormattedText("Aq", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, TextInput.FontSize, Brushes.White, 96).Height;
    }

    private static int _CountLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        return normalized.Count(c => c is '\n' or '\r') + 1;
    }

    private async void BtnSend_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isRunning)
            return;
        var input = TextInput.Text.Trim();
        if (input.Length == 0)
            return;
        _SaveConfig();
        if (Config.Ai.ApiKey.IsNullOrEmpty())
        {
            HintService.Hint(Lang.Text("Tools.Ai.Setting.NoKey"), HintType.Error);
            return;
        }

        TextInput.Text = "";
        _AddBubble(input, isUser: true);
        _history.Add(AiChatMessage.User(input));

        _isRunning = true;
        BtnSend.IsEnabled = false;
        BtnStop.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var streamingBlock = _CreateStreamingBlock();
        var streamingText = "";
        var isFirstChunk = true;
        var currentToolName = "";

        try
        {
            await foreach (var evt in ModAi.RunAsync(_history, _cts.Token))
            {
                switch (evt.Kind)
                {
                    case ModAi.AiEventKind.ContentDelta:
                        if (isFirstChunk)
                        {
                            _AddAssistantBlock(streamingBlock);
                            isFirstChunk = false;
                        }
                        streamingText += evt.Text;
                        streamingBlock.Text = streamingText;
                        _ScrollToBottom();
                        break;
                    case ModAi.AiEventKind.ToolStarted:
                        currentToolName = evt.Text ?? "";
                        _AddBubble(Lang.Text("Tools.Ai.Tool.Running", _ToolDisplayName(currentToolName)), isUser: false, isStatus: true);
                        break;
                    case ModAi.AiEventKind.ToolFinished:
                        _AddBubble(Lang.Text("Tools.Ai.Tool.Done", _ToolDisplayName(currentToolName)) + "\n" + evt.Text, isUser: false, isStatus: true);
                        break;
                    case ModAi.AiEventKind.Done:
                        break;
                    case ModAi.AiEventKind.UsageUpdated:
                        _UpdateTokenLabel(evt.PromptTokens, evt.CompletionTokens);
                        break;
                    case ModAi.AiEventKind.Error:
                        _AddBubble(evt.Text ?? "", isUser: false, isError: true);
                        break;
                }
            }

            // 完成后用 Markdown 重新渲染助手回答
            if (!isFirstChunk)
                _FinalizeAssistantBlock(streamingBlock, streamingText);
        }
        catch (OperationCanceledException)
        {
            if (streamingText.Length > 0)
                _FinalizeAssistantBlock(streamingBlock, streamingText);
            _AddBubble(Lang.Text("Tools.Ai.Stopped"), isUser: false, isStatus: true);
        }
        catch (AiApiException ex)
        {
            _AddBubble(Lang.Text("Tools.Ai.Error.Api", ex.StatusCode, ex.Message), isUser: false, isError: true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "AI 对话失败");
            _AddBubble(Lang.Text("Tools.Ai.Error.Unknown", ex.Message), isUser: false, isError: true);
        }
        finally
        {
            _isRunning = false;
            BtnSend.IsEnabled = true;
            BtnStop.Visibility = Visibility.Collapsed;
            _SaveSession();
            _RefreshSessionList();
            _ScrollToBottom();
        }
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        _cts?.Cancel();
    }

    private static string _ToolDisplayName(string? name) => name switch
    {
        "translate_core" => Lang.Text("Tools.Ai.Skill.Core"),
        "translate_mods" => Lang.Text("Tools.Ai.Skill.Mod"),
        "set_keybinds" => Lang.Text("Tools.Ai.Skill.Keybind"),
        _ => name ?? "?"
    };

    #endregion

    #region 会话与记忆

    private void _RefreshSessionList()
    {
        _isLoadingSessions = true;
        try
        {
            ComboSessions.Items.Clear();
            foreach (var (path, name) in ModAi.ListSessions())
                ComboSessions.Items.Add(new MyComboBoxItem { Content = name, Tag = path });
            ComboSessions.SelectedIndex = -1;
        }
        finally
        {
            _isLoadingSessions = false;
        }
    }

    private void ComboSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSessions || _isRunning)
            return;
        if (ComboSessions.SelectedItem is not MyComboBoxItem { Tag: string path })
        {
            _RefreshSessionList();
            return;
        }
        // 切换前自动学习当前对话中的用户偏好（后台异步，不阻塞）
        _ = ModAi.LearnMemoryAsync(new List<AiChatMessage>(_history), CancellationToken.None);

        var messages = ModAi.LoadSession(path);
        if (messages.Count == 0)
        {
            HintService.Hint(Lang.Text("Tools.Ai.History.Empty"), HintType.Warning);
            _RefreshSessionList();
            return;
        }
        _history.Clear();
        _history.AddRange(messages);
        _sessionPath = path;
        ModAi.ResetTokenUsage();
        _UpdateTokenLabel();
        _RenderHistory();
    }

    private void BtnNewChat_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isRunning)
            return;
        if (_history.Any(m => m.Role != "system"))
        {
            _SaveSession();
            _ = ModAi.LearnMemoryAsync(new List<AiChatMessage>(_history), CancellationToken.None);
        }
        _history.Clear();
        _sessionPath = null;
        ModAi.ResetTokenUsage();
        _UpdateTokenLabel();
        _RenderHistory();
        _AddBubble(Lang.Text("Tools.Ai.Chat.EmptyHint"), isUser: false, isStatus: true);
        _RefreshSessionList();
        TextInput.Focus();
    }

    private void BtnMemory_Click(object sender, MouseButtonEventArgs e)
    {
        var memory = ModAi.LoadMemory();
        if (memory.Count == 0)
        {
            _AddBubble(Lang.Text("Tools.Ai.Memory.Empty"), isUser: false, isStatus: true);
            return;
        }
        _AddBubble(Lang.Text("Tools.Ai.Memory.Content") + "\n" + string.Join("\n", memory.Select(m => "• " + m)),
            isUser: false, isStatus: true);
    }

    private void _SaveSession()
    {
        if (_history.Any(m => m.Role != "system"))
            _sessionPath = ModAi.SaveSession(_sessionPath, _history);
    }

    private void _UpdateTokenLabel(int promptTokens = 0, int completionTokens = 0)
    {
        if (promptTokens <= 0 && completionTokens <= 0)
        {
            promptTokens = ModAi.SessionPromptTokens;
            completionTokens = ModAi.SessionCompletionTokens;
        }
        TextTokenUsage.Text = promptTokens + completionTokens > 0
            ? Lang.Text("Tools.Ai.TokenUsage", promptTokens, completionTokens, promptTokens + completionTokens)
            : "";
    }

    /// <summary>按历史重建聊天区（加载历史会话时使用，工具过程消息省略）。</summary>
    private void _RenderHistory()
    {
        PanChatList.Children.Clear();
        foreach (var message in _history)
        {
            switch (message.Role)
            {
                case "user" when message.Content is { Length: > 0 }:
                    _AddBubble(message.Content, isUser: true);
                    break;
                case "assistant" when message.Content is { Length: > 0 }:
                    _AddBubble(message.Content, isUser: false);
                    break;
            }
        }
    }

    #endregion

    #region 气泡渲染

    private TextBlock _CreateStreamingBlock() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Foreground = (Brush)System.Windows.Application.Current.Resources["ColorBrush1"]
    };

    private void _AddAssistantBlock(TextBlock block)
    {
        var border = new Border
        {
            Background = (Brush)System.Windows.Application.Current.Resources["ColorBrush7"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 2, 36, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 560,
            Child = block
        };
        PanChatList.Children.Add(border);
    }

    private void _FinalizeAssistantBlock(TextBlock block, string text)
    {
        if (block.Parent is not Border border)
            return;
        var viewer = new MarkdownViewer
        {
            Markdown = text,
            FontSize = 13,
            Foreground = (Brush)System.Windows.Application.Current.Resources["ColorBrush1"]
        };
        border.Child = viewer;
    }

    private void _AddBubble(string text, bool isUser, bool isStatus = false, bool isError = false)
    {
        if (isStatus)
        {
            PanChatList.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.65,
                Margin = new Thickness(4, 4, 4, 4)
            });
            _ScrollToBottom();
            return;
        }

        FrameworkElement content;
        if (isUser)
        {
            content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = (Brush)System.Windows.Application.Current.Resources["ColorBrushInfoDark"]
            };
        }
        else
        {
            content = new MarkdownViewer
            {
                Markdown = text,
                FontSize = 13,
                Foreground = (Brush)System.Windows.Application.Current.Resources["ColorBrush1"]
            };
        }

        var border = new Border
        {
            Background = isError
                ? (Brush)System.Windows.Application.Current.Resources["ColorBrushRedBack"]
                : isUser
                    ? (Brush)System.Windows.Application.Current.Resources["ColorBrush2"]
                    : (Brush)System.Windows.Application.Current.Resources["ColorBrush7"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = isUser ? new Thickness(36, 2, 0, 8) : new Thickness(0, 2, 36, 8),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 560,
            Child = content
        };
        PanChatList.Children.Add(border);
        _ScrollToBottom();
    }

    private void _ScrollToBottom() => ModBase.RunInUi(PanChat.ScrollToBottom);

    #endregion
}
