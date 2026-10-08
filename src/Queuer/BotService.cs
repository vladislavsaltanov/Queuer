using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Queuer;

// One instance, N chats. Route by chat id.
public sealed class BotService(ITelegramBotClient bot)
{
    private sealed class ChatQueue
    {
        public readonly QueueCore Core = new();
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int MsgId;
        public DateTimeOffset Deadline;
        public DateTimeOffset LastRender;
        public CancellationTokenSource Cts = new();
    }

    private sealed record Offer(long ChatId, long FromId, long TargetId, int MsgId);
    private sealed record Prompt(long ChatId, long UserId, int MsgId, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<long, ChatQueue> _chats = new();
    private readonly ConcurrentDictionary<(long, long), Offer> _offers = new(); // key: chat + from
    private readonly ConcurrentDictionary<long, Prompt> _prompts = new(); // key: chat
    private readonly ConcurrentDictionary<(long, long), (bool Admin, DateTimeOffset At)> _adminCache = new();

    private static readonly TimeSpan PoolTtl = TimeSpan.FromHours(3);
    private static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PromptTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfferTtl = TimeSpan.FromMinutes(5);

    public async Task HandleUpdate(ITelegramBotClient b, Update u, CancellationToken ct)
    {
        if (u.Message is { } m) await OnMessage(m, ct);
        else if (u.CallbackQuery is { } q) await OnCallback(q, ct);
    }

    public Task HandleError(ITelegramBotClient b, Exception ex, CancellationToken ct)
    {
        Console.WriteLine(ex.Message);
        return Task.CompletedTask;
    }

    private async Task OnMessage(Message m, CancellationToken ct)
    {
        if (m.Text is null || m.From is null) return;
        var chatId = m.Chat.Id;

        // Id lookup. DM only, never leaks ids in chats.
        if (m.Text.StartsWith("/whoami"))
        {
            if (m.Chat.Type is ChatType.Private)
                await bot.SendMessage(chatId, $"Твой ID: {m.From.Id}\nID чата: {chatId}", cancellationToken: ct);
            return;
        }

        // TEMP: ids for forwarded messages. Revert later.
        if (m.Chat.Type is ChatType.Private && (m.ForwardOrigin is not null || m.ForwardFrom is not null || m.ForwardFromChat is not null || m.ForwardSenderName is not null))
        {
            var lines = new List<string> { $"Твой ID: {m.From.Id}" };
            switch (m.ForwardOrigin)
            {
                case MessageOriginUser u: lines.Add($"ID автора: {u.SenderUser.Id}"); break;
                case MessageOriginHiddenUser h: lines.Add($"Автор скрыт: {h.SenderUserName}"); break;
                case MessageOriginChat c: lines.Add($"ID чата: {c.SenderChat.Id}"); break;
                case MessageOriginChannel ch: lines.Add($"ID канала: {ch.Chat.Id}"); break;
            }
            if (m.ForwardFrom is not null) lines.Add($"ID автора: {m.ForwardFrom.Id}");
            if (m.ForwardFromChat is not null) lines.Add($"ID чата: {m.ForwardFromChat.Id}");
            if (m.ForwardSenderName is not null) lines.Add($"Автор скрыт: {m.ForwardSenderName}");
            await bot.SendMessage(chatId, string.Join("\n", lines), cancellationToken: ct);
            return;
        }

        if (m.Text.StartsWith("/start_queue"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return; // silent for others
            if (_chats.ContainsKey(chatId)) return; // one queue per chat
            await StartQueue(chatId, ct);
            return;
        }
        if (m.Text.StartsWith("/close_queue"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return;
            await CloseQueue(chatId, ct);
            return;
        }
        if (m.Text.StartsWith("/kick"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return;
            var parts = m.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var n) || !_chats.TryGetValue(chatId, out var q))
                return;
            q.Core.Kick(n);
            await Render(chatId, ct);
            return;
        }

        // Swap number as reply to prompt.
        if (_prompts.TryGetValue(chatId, out var p) && m.From.Id == p.UserId
            && m.ReplyToMessage?.MessageId == p.MsgId && DateTimeOffset.UtcNow < p.Expires)
        {
            await TrySwap(m, p, ct);
        }
    }

    private async Task OnCallback(CallbackQuery q, CancellationToken ct)
    {
        if (q.Message is null || q.From is null) return;
        var chatId = q.Message.Chat.Id;
        var data = q.Data ?? "";

        Task Alert(string t) => bot.AnswerCallbackQuery(q.Id, t, showAlert: true, cancellationToken: ct);

        if (data is "join" or "leave" or "swap")
        {
            if (!_chats.TryGetValue(chatId, out var cq)) { await Alert("Нет очереди"); return; }
            var name = q.From.FirstName + (q.From.Username is null ? "" : $" (@{q.From.Username})");
            string? err = data switch
            {
                "join" => cq.Core.Join(new QueueUser(q.From.Id, name), DateTimeOffset.UtcNow),
                "leave" => LeaveFlow(cq, q.From.Id),
                _ => await SwapPrompt(chatId, q.From.Id, ct),
            };
            if (err is not null) await Alert(err);
            else if (data is "join" or "leave") await Render(chatId, ct);
            else await bot.AnswerCallbackQuery(q.Id, cancellationToken: ct);
            return;
        }

        if (data.StartsWith("yes:") && long.TryParse(data[4..], out var fromId))
        {
            await AnswerSwap(chatId, fromId, q.From.Id, accept: true, ct, q.Id);
            return;
        }
        if (data.StartsWith("no:") && long.TryParse(data[3..], out var fromId2))
        {
            await AnswerSwap(chatId, fromId2, q.From.Id, accept: false, ct, q.Id);
            return;
        }
        if (data is "extend")
        {
            if (!await IsAdmin(chatId, q.From.Id, ct)) { await Alert("Только для админов"); return; }
            if (_chats.TryGetValue(chatId, out var cq)) cq.Deadline += TimeSpan.FromHours(1);
            await SafeDelete(chatId, q.Message.MessageId, ct);
            await bot.AnswerCallbackQuery(q.Id, cancellationToken: ct);
            return;
        }
        if (data is "close_now")
        {
            if (!await IsAdmin(chatId, q.From.Id, ct)) { await Alert("Только для админов"); return; }
            await CloseQueue(chatId, ct);
            await SafeDelete(chatId, q.Message.MessageId, ct);
            return;
        }
    }

    private string? LeaveFlow(ChatQueue q, long userId)
    {
        var err = q.Core.Leave(userId, DateTimeOffset.UtcNow);
        if (err is null) BurnOffersOf(userId);
        return err;
    }

    private async Task StartQueue(long chatId, CancellationToken ct)
    {
        var q = new ChatQueue { Deadline = DateTimeOffset.UtcNow + PoolTtl };
        _chats[chatId] = q;
        var sent = await bot.SendMessage(chatId, q.Core.Render(),
            replyMarkup: Buttons(), cancellationToken: ct);
        q.MsgId = sent.MessageId;
        _ = RunTtl(chatId, q.Cts.Token);
    }

    private async Task CloseQueue(long chatId, CancellationToken ct)
    {
        if (!_chats.TryGetValue(chatId, out var q)) return;
        await q.Gate.WaitAsync(ct);
        try
        {
            q.Core.Close();
            q.Cts.Cancel();
            await bot.EditMessageText(chatId, q.MsgId, q.Core.Render(), cancellationToken: ct);
            await bot.EditMessageReplyMarkup(chatId, q.MsgId, replyMarkup: null, cancellationToken: ct);
        }
        catch (ApiRequestException) { } // stale message, skip
        finally { q.Gate.Release(); _chats.TryRemove(chatId, out _); }
    }

    private async Task Render(long chatId, CancellationToken ct)
    {
        if (!_chats.TryGetValue(chatId, out var q) || q.Core.IsClosed) return;
        await q.Gate.WaitAsync(ct);
        try
        {
            // Debounce: skip fast repeats.
            if (DateTimeOffset.UtcNow - q.LastRender < TimeSpan.FromSeconds(1)) return;
            q.LastRender = DateTimeOffset.UtcNow;
            await bot.EditMessageText(chatId, q.MsgId, q.Core.Render(),
                replyMarkup: Buttons(), cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("not modified")) { } // same text
        catch (ApiRequestException ex) when (ex.ErrorCode == 429) { } // flood, next edit wins
        finally { q.Gate.Release(); }
    }

    private static InlineKeyboardMarkup Buttons() =>
        new InlineKeyboardButton[][]
        {
            [("➕ Встать", "join"), ("➖ Выйти", "leave")],
            [("🔀 Поменяться", "swap")],
        };

    private async Task<bool> IsAdmin(long chatId, long userId, CancellationToken ct)
    {
        if (Env.ChatAdmins(chatId).Contains(userId)) return true; // .env list
        if (_adminCache.TryGetValue((chatId, userId), out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(5))
            return c.Admin;
        try
        {
            var m = await bot.GetChatMember(chatId, userId, cancellationToken: ct);
            var admin = m.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
            _adminCache[(chatId, userId)] = (admin, DateTimeOffset.UtcNow);
            return admin;
        }
        catch (ApiRequestException) { return false; } // unknown, deny
    }

    private async Task<string?> SwapPrompt(long chatId, long userId, CancellationToken ct)
    {
        var sent = await bot.SendMessage(chatId, "Ответь на это сообщение номером, с кем меняешься (60с).",
            replyParameters: new ReplyParameters { MessageId = _chats[chatId].MsgId }, cancellationToken: ct);
        _prompts[chatId] = new Prompt(chatId, userId, sent.MessageId, DateTimeOffset.UtcNow + PromptTtl);
        _ = BurnPrompt(chatId, sent.MessageId);
        return null;
    }

    private async Task TrySwap(Message m, Prompt p, CancellationToken ct)
    {
        var chatId = m.Chat.Id;
        _prompts.TryRemove(chatId, out _);
        await SafeDelete(chatId, p.MsgId, ct);
        await SafeDelete(chatId, m.MessageId, ct);
        if (!_chats.TryGetValue(chatId, out var q)) return;
        if (!int.TryParse(m.Text!.Trim(), out var n)) return;
        var err = q.Core.ProposeSwap(p.UserId, n, out var targetId);
        if (err is not null) return; // silent, prompt gone
        var from = m.From!;
        var fname = from.FirstName + (from.Username is null ? "" : $" (@{from.Username})");
        var tname = q.Core.List.FirstOrDefault(x => x.Id == targetId)?.Name ?? "участник";
        var mention = $"<a href=\"tg://user?id={targetId}\">{Escape(tname)}</a>";
        var sent = await bot.SendMessage(chatId,
            $"{mention}, {Escape(fname)} ({q.Core.Pos(p.UserId)}) хочет поменяться с тобой ({q.Core.Pos(targetId)}). Согласен?",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardButton[][]
            {
                [("Да", $"yes:{p.UserId}"), ("Нет", $"no:{p.UserId}")],
            }, cancellationToken: ct);
        _offers[(chatId, p.UserId)] = new Offer(chatId, p.UserId, targetId, sent.MessageId);
        _ = BurnOffer(chatId, p.UserId, targetId, sent.MessageId);
    }

    private async Task AnswerSwap(long chatId, long fromId, long targetId, bool accept, CancellationToken ct, string queryId)
    {
        if (!_offers.TryRemove((chatId, fromId), out var o) || o.TargetId != targetId) return;
        await SafeDelete(chatId, o.MsgId, ct);
        if (!_chats.TryGetValue(chatId, out var q)) return;
        if (!accept) { q.Core.CancelSwap(fromId, targetId); await bot.AnswerCallbackQuery(queryId, cancellationToken: ct); return; }
        var ok = q.Core.AcceptSwap(fromId, targetId);
        await bot.AnswerCallbackQuery(queryId, ok ? "Поменялись" : "Уже не в очереди", showAlert: !ok, cancellationToken: ct);
        if (ok) await Render(chatId, ct);
    }

    private void BurnOffersOf(long userId)
    {
        foreach (var k in _offers.Keys)
            if (k.Item2 == userId || _offers[k].TargetId == userId) _offers.TryRemove(k, out _);
    }

    private async Task BurnPrompt(long chatId, int msgId)
    {
        await Task.Delay(PromptTtl);
        _prompts.TryRemove(chatId, out _);
        await SafeDelete(chatId, msgId, CancellationToken.None);
    }

    private async Task BurnOffer(long chatId, long fromId, long targetId, int msgId)
    {
        await Task.Delay(OfferTtl);
        if (!_offers.TryRemove((chatId, fromId), out _)) return;
        if (_chats.TryGetValue(chatId, out var q)) q.Core.CancelSwap(fromId, targetId);
        await SafeDelete(chatId, msgId, CancellationToken.None);
    }

    private async Task RunTtl(long chatId, CancellationToken ct)
    {
        try
        {
            if (!_chats.TryGetValue(chatId, out var q)) return;
            var wait = q.Deadline - DateTimeOffset.UtcNow - WarnBefore;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            if (ct.IsCancellationRequested || !_chats.ContainsKey(chatId)) return;
            var warn = await bot.SendMessage(chatId, "До закрытия очереди 10 мин. Продлить?",
                replyMarkup: new InlineKeyboardButton[][]
                {
                    [("+1 час", "extend"), ("Закрыть", "close_now")],
                }, cancellationToken: ct);
            var left = _chats.TryGetValue(chatId, out var q2) ? q2.Deadline - DateTimeOffset.UtcNow : TimeSpan.Zero;
            if (left > TimeSpan.Zero) await Task.Delay(left, ct);
            await SafeDelete(chatId, warn.MessageId, CancellationToken.None);
            await CloseQueue(chatId, CancellationToken.None);
        }
        catch (TaskCanceledException) { } // closed early
    }

    // Escape text for Html parse mode.
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task SafeDelete(long chatId, int msgId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(chatId, msgId, cancellationToken: ct); }
        catch (ApiRequestException) { } // no rights or gone, skip
    }
}
