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

    /// <summary>设置卡自动折叠的高度下限。低于该高度时默认折叠设置卡，把空间让给聊天区（内嵌到启动页时页面通常较矮）。</summary>
    private const double SettingsExpandMinHeight = 820;

    /// <summary>用户是否手动展开/折叠过设置卡；一旦手动操作过就不再随窗口高度自动切换。</summary>
    private bool _settingsUserToggled;

    /// <summary>待自动开始的报错诊断上下文（单槽）。由报错入口排队；页面可见/空闲时消费并自动开跑。</summary>
    private static string? _queuedDiagnosisText;

    /// <summary>Agent 是否正在运行（发送中/工具循环中）。运行时不能插入新会话。</summary>
    internal bool IsAgentRunning => _isRunning;

    /// <summary>排队一次报错诊断会话。随后页面导航/可见/空闲时会自动开跑。</summary>
    internal static void QueueDiagnosis(string contextText) => _queuedDiagnosisText = contextText;

    /// <summary>若排队的诊断尚未开跑且 Agent 空闲，立即开跑。返回是否已开始。</summary>
    internal bool TryConsumePendingDiagnosis()
    {
        var text = _queuedDiagnosisText;
        if (text is null || _isRunning || !IsVisible)
            return false;
        _queuedDiagnosisText = null;
        _BeginDiagnosisSession(text);
        return true;
    }

    public PageToolsAi()
    {
        InitializeComponent();
        Loaded += PageToolsAi_Loaded;
        // 用户手动展开/折叠过设置卡后，不再随高度自动切换
        CardSettings.Swap += (_, _) => _settingsUserToggled = true;
        // 页面从隐藏变为可见时（含导航回到本页）消费排队的诊断，并按当前高度调整设置卡
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                ModBase.RunInUi(() =>
                {
                    _AutoFitSettingsCard();
                    TryConsumePendingDiagnosis();
                }, true);
        };
    }

    /// <summary>页面较矮时自动折叠设置卡，保证聊天区可见且可用（嵌入启动页右栏时为矮视口）。</summary>
    private void _AutoFitSettingsCard()
    {
        if (_settingsUserToggled || ActualHeight < 1d)
            return;
        CardSettings.IsSwapped = ActualHeight < SettingsExpandMinHeight;
    }

    private void PageToolsAi_Loaded(object sender, RoutedEventArgs e)
    {
        _AutoFitSettingsCard();
        if (TextEndpoint.Text.Length == 0)
        {
            TextEndpoint.Text = Config.Ai.Endpoint;
            TextModel.Text = Config.Ai.Model;
            CheckDiagnosis.SetChecked(Config.Ai.AiDiagnosisEnabled, false);
            _ApplyKeyMask();
            _RefreshSessionList();
            if (PanChatList.Children.Count == 0)
                _AddBubble(Lang.Text("Tools.Ai.Chat.EmptyHint"), isUser: false, isStatus: true);
        }
        // 首次加载或重新加载时消费排队的诊断（启动早期的报错可能先于本页排队）
        ModBase.RunInUi(() => TryConsumePendingDiagnosis(), true);
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
        Config.Ai.AiDiagnosisEnabled = CheckDiagnosis.Checked == true;
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

        await _RunAgentCoreAsync();
    }

    /// <summary>
    /// 把历史交给 ModAi 跑一轮 Agent 对话（流式渲染正文、展示工具过程）。
    /// 仅在 UI 线程、且 _isRunning 为 false 时调用（BtnSend 与诊断会话共用）。
    /// </summary>
    private async Task _RunAgentCoreAsync()
    {
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
            // 兜底：若有排队未开的诊断（例如运行期间外部排队），现在自动补开
            TryConsumePendingDiagnosis();
        }
    }

    /// <summary>
    /// 用报错上下文开一场新会话：当前对话自动存档 → 清空历史 → 注入诊断指令与用户上下文 → 自动开跑。
    /// 必须在 UI 线程、且 Agent 空闲时调用（TryConsumePendingDiagnosis 保证）。
    /// </summary>
    private void _BeginDiagnosisSession(string contextText)
    {
        if (_history.Any(m => m.Role != "system"))
            _SaveSession(); // 之前的对话自动存档

        _history.Clear();
        _sessionPath = null;
        ModAi.ResetTokenUsage();
        _UpdateTokenLabel();

        // 在完整基础提示词（人设/语言/记忆）之上叠加诊断工作模式
        _history.Add(AiChatMessage.System(ModAi.SystemPrompt + "\n\n" + Lang.Text("Ai.Diagnosis.SystemPrompt")));
        _history.Add(AiChatMessage.User(contextText));

        PanChatList.Children.Clear();
        _AddBubble(contextText, isUser: true);
        _AddBubble(Lang.Text("Ai.Diagnosis.Starting"), isUser: false, isStatus: true);
        _SaveSession(); // 立即落盘（含诊断上下文），中断后仍可恢复
        _RefreshSessionList();
        TextInput.Focus();

        _ = _RunAgentCoreAsync();
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        _cts?.Cancel();
    }

    private static string _ToolDisplayName(string? name) => ModAi.ToolDisplayName(name);

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
