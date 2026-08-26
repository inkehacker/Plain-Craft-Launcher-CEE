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
