using NgBubbles.Core;
using Xunit;

namespace NgBubbles.Tests;

public sealed class MessageLinksTests
{
    [Fact]
    public void LinksKeepQueriesAndOriginalTextOffsetsAcrossLines()
    {
        const string text = "💬 Survey: https://www.example.test/r/ABC?N=002832738-10\nVisit www.example.com/plans?x=1&y=2. Thanks!";
        var links = MessageLinks.Find(text);
        Assert.Equal(2, links.Count);
        Assert.Equal("https://www.example.test/r/ABC?N=002832738-10", links[0].Url);
        Assert.Equal("https://www.example.com/plans?x=1&y=2", links[1].Url);
        Assert.Equal("www.example.com/plans?x=1&y=2", text.Substring(links[1].Start, links[1].Length));
        Assert.Equal("https://www.example.test/r/ABC?N=002832738-10", text.Substring(links[0].Start, links[0].Length));
    }
    [Fact]
    public void PunctuationDoesNotBreakBalancedPathsOrIpv6Hosts()
    {
        var links = MessageLinks.Find("(https://example.com/a_(b)). [http://[::1]:1234/path] https://example.com/q?a=b%20c&d=e#section!");
        Assert.Equal(new[] { "https://example.com/a_(b)", "http://[::1]:1234/path", "https://example.com/q?a=b%20c&d=e#section" }, links.Select(link => link.Url));
    }
    [Fact]
    public void NonWebSchemesEmailsAndCredentialUrlsStayPlainText()
    {
        Assert.Empty(MessageLinks.Find("javascript:alert(1) file:///C:/test.exe mailto:me@example.com me@www.example.com https://user:password@example.com/"));
        Assert.Empty(MessageLinks.Find("Ordinary text with no links"));
    }
}
