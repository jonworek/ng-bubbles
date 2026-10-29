using System.Security.Cryptography;
using System.Text;

namespace NgBubbles.Core;

internal sealed record HistoryPage(List<Message> Messages, long? Start);
internal sealed record HistoryRange(long Upper, long Lower, long? Start, string File);

internal sealed class HistoryPages(Store root)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Store ForChat(string guid) => new(Path.Combine(root.DirectoryPath, "history", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(guid)))));
    public Task<HistoryPage?> LatestAsync(string guid) => Task.Run(() => ForChat(guid).Load<HistoryPage?>("latest.json", () => null));
    public async Task<HistoryPage?> BeforeAsync(string guid, long before)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = ForChat(guid);
            var ranges = await Task.Run(() => store.Load("index.json", () => new List<HistoryRange>())).ConfigureAwait(false);
            var range = ranges.Where(page => page.Upper >= before && (page.Lower < before || page.Start != null)).OrderBy(page => page.Lower).FirstOrDefault();
            if (range == null) return null;
            var page = await Task.Run(() => store.Load<HistoryPage?>(range.File, () => null)).ConfigureAwait(false);
            if (page == null) return null;
            return new(page.Messages.Where(message => message.DateCreated < before).TakeLast(100).ToList(), page.Start);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string guid)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = ForChat(guid).DirectoryPath;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(string guid, long? before, HistoryPage page)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = ForChat(guid);
            var file = before?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "latest";
            file += ".json";
            var ranges = await Task.Run(() => store.Load("index.json", () => new List<HistoryRange>())).ConfigureAwait(false);
            var upper = before ?? page.Messages.Select(message => message.DateCreated).DefaultIfEmpty(0).Max();
            if (before == null && upper < long.MaxValue) upper++;
            var lower = page.Messages.Select(message => message.DateCreated).DefaultIfEmpty(upper).Min();
            ranges.RemoveAll(range => range.File == file);
            ranges.Add(new(upper, lower, page.Start, file));
            await store.SaveAsync(file, page).ConfigureAwait(false);
            await store.SaveAsync("index.json", ranges).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
