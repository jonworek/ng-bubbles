namespace NgBubbles.Core;

public sealed record HomeDraft(Chat Chat, string Text);
public sealed record HomeSummary(int ConversationCount, int UnreadCount, int DraftCount,
    List<Chat> Unread, List<Chat> Recent, List<HomeDraft> Drafts)
{
    public static HomeSummary Create(Snapshot state, IReadOnlyDictionary<string, string> drafts)
    {
        var chats = state.Chats.OrderByDescending(c => c.LastMessage?.DateCreated ?? 0).ToList();
        var unread = chats.Where(c => state.Unread.Contains(c.Guid)).ToList();
        var saved = chats.Select(c => new HomeDraft(c, drafts.GetValueOrDefault(c.Guid) ?? "")).Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList();
        return new(chats.Count, unread.Count, saved.Count, unread.Take(4).ToList(), chats.Take(4).ToList(), saved.Take(3).ToList());
    }
}
