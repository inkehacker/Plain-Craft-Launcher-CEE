using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.AI;

namespace PCL.Core.Test.AI;

[TestClass]
public class AiResponseParserTest
{
    [TestMethod]
    public void ParseContentDelta()
    {
        var chunk = AiResponseParser.TryParseData(
            """{"choices":[{"index":0,"delta":{"content":"你好"}}]}""");

        Assert.IsNotNull(chunk);
        Assert.IsFalse(chunk.IsDone);
        Assert.AreEqual("你好", chunk.ContentDelta);
        Assert.AreEqual(0, chunk.ToolCallDeltas.Count);
    }

    [TestMethod]
    public void ParseToolCallFragments()
    {
        var first = AiResponseParser.TryParseData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"set_keybind","arguments":"{\"key\":\""}}]}}]}""");
        var second = AiResponseParser.TryParseData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"sneak\"}"}}]}}]}""");

        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.ToolCallDeltas.Count);
        Assert.AreEqual("call_1", first.ToolCallDeltas[0].Id);
        Assert.AreEqual("set_keybind", first.ToolCallDeltas[0].Name);
        Assert.AreEqual("{\"key\":\"", first.ToolCallDeltas[0].ArgumentsFragment);
        Assert.IsNotNull(second);
        Assert.AreEqual("sneak\"}", second.ToolCallDeltas[0].ArgumentsFragment);
    }

    [TestMethod]
    public void ParseDoneMarker()
    {
        var chunk = AiResponseParser.TryParseData("[DONE]");
        Assert.IsNotNull(chunk);
        Assert.IsTrue(chunk.IsDone);
    }

    [TestMethod]
    public void ParseFinishReason()
    {
        var chunk = AiResponseParser.TryParseData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");
        Assert.IsNotNull(chunk);
        Assert.AreEqual("stop", chunk.FinishReason);
    }

    [TestMethod]
    public void IgnoreInvalidLines()
    {
        Assert.IsNull(AiResponseParser.TryParseData(""));
        Assert.IsNull(AiResponseParser.TryParseData(": keep-alive"));
        Assert.IsNull(AiResponseParser.TryParseData("not json"));
    }

    [TestMethod]
    public void ParseCompletionWithToolCalls()
    {
        var completion = AiResponseParser.ParseCompletion(
            """
            {"choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_2","type":"function","function":{"name":"translate","arguments":"{\"a\":1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":20}}
            """);

        Assert.IsNotNull(completion);
        Assert.IsNull(completion.Choice.Content);
        Assert.AreEqual("tool_calls", completion.Choice.FinishReason);
        Assert.AreEqual(1, completion.Choice.ToolCalls.Count);
        Assert.AreEqual("call_2", completion.Choice.ToolCalls[0].Id);
        Assert.AreEqual("translate", completion.Choice.ToolCalls[0].Name);
        Assert.AreEqual("{\"a\":1}", completion.Choice.ToolCalls[0].ArgumentsJson);
        Assert.AreEqual(10, completion.PromptTokens);
        Assert.AreEqual(20, completion.CompletionTokens);
    }

    [TestMethod]
    public void ParseCompletionPlainContent()
    {
        var completion = AiResponseParser.ParseCompletion(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"回答"},"finish_reason":"stop"}]}""");
        Assert.IsNotNull(completion);
        Assert.AreEqual("回答", completion.Choice.Content);
        Assert.AreEqual(0, completion.Choice.ToolCalls.Count);
    }

    [TestMethod]
    public void ParseErrorMessage()
    {
        Assert.AreEqual("Invalid API key",
            AiResponseParser.ParseError("""{"error":{"message":"Invalid API key","type":"auth"}}"""));
        Assert.AreEqual("plain", AiResponseParser.ParseError("plain"));
    }
}
