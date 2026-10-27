namespace NgBubbles.Core;

public static class ConversationFilters
{
    public static List<Chat> Apply(Snapshot state, string search, bool unreadOnly, bool groupsOnly) => state.Chats
        .Where(chat => (!unreadOnly || state.Unread.Contains(chat.Guid)) && (!groupsOnly || chat.IsGroup))
        .Where(chat => Model.Title(chat, state.Contacts).Contains(search, StringComparison.OrdinalIgnoreCase) ||
            chat.Participants.Any(participant => participant.Address.Contains(search, StringComparison.OrdinalIgnoreCase)))
        .ToList();
}
