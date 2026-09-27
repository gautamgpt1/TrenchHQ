using TrenchHQ.Core.Social;
using TrenchHQ.Infrastructure.Social;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TrenchHQ.SocialTests;

public partial class SocialTests
{
    void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    SocialPost? Parse(string json, string source) { using var doc = JsonDocument.Parse(json); return SocialPostParser.ParseJson(doc.RootElement, source); }
    Task Done() => Task.CompletedTask;

}
