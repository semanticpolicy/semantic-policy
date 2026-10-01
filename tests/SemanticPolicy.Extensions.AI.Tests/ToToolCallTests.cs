using System.Text.Json;
using Microsoft.Extensions.AI;
using SemanticPolicy.Extensions.AI.Tests.Support;

namespace SemanticPolicy.Extensions.AI.Tests;

public sealed class ToToolCallTests
{
    [Fact]
    public void Invocation_Context_Maps_To_The_Call_The_Model_Proposed()
    {
        FunctionInvocationContext context = new()
        {
            Function = AIFunctionFactory.Create((string? name) => name, "delete_branch", "Deletes a branch."),
            Arguments = new AIFunctionArguments(ScriptedLoop.Named("test-old")),
            Messages =
            [
                new ChatMessage(ChatRole.System, "system-text-1"),
                new ChatMessage(ChatRole.User, "user-text-1"),
                new ChatMessage(ChatRole.Assistant, "assistant-text-1"),
                new ChatMessage(ChatRole.User, "user-text-2"),
            ],
            CallContent = new FunctionCallContent("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
        };

        ToolCall call = context.ToToolCall();

        call.Name.Should().Be("delete_branch");
        call.Description.Should().Be("Deletes a branch.");
        call.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        call.Arguments.EnumerateObject().Select(property => property.Name).Should().Equal("name");
        call.Arguments.GetProperty("name").GetString().Should().Be("test-old");
        call.Conversation.Should().Equal(
            new ConversationMessage("system", "system-text-1"),
            new ConversationMessage("user", "user-text-1"),
            new ConversationMessage("assistant", "assistant-text-1"),
            new ConversationMessage("user", "user-text-2"));
        call.CorrelationId.Should().Be("call_1");
    }
}
