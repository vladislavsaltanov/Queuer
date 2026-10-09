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
    private readonly ConcurrentDictionary<long, (string Title, List<QueueUser> List)> _closed = new(); // last closed list
    private readonly ConcurrentDictionary<(long Chat, int Msg), DateTimeOffset> _botMsgs = new(); // bot-sent ids for /delete_all

    private static readonly TimeSpan PoolTtl = TimeSpan.FromHours(3);
    private static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PromptTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfferTtl = TimeSpan.FromMinutes(5);

    public async Task HandleUpdate(ITelegramBotClient b, Update u, CancellationToken ct)
    {
        // Trace updates.
        Console.WriteLine($"upd {u.Type} chat {u.Message?.Chat.Id ?? u.CallbackQuery?.Message?.Chat.Id}");
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

        if (m.Text.StartsWith("/start_queue"))
        {
            if (!await CanControl(chatId, m.From.Id, ct)) return; // silent for others
            if (_chats.ContainsKey(chatId)) return; // one queue per chat
            await StartQueue(chatId, ParseTitle(m.Text, "/start_queue"), ct);
            return;
        }
        if (m.Text.StartsWith("/continue_queue"))
        {
            if (!await CanControl(chatId, m.From.Id, ct)) return;
            await ContinueQueue(chatId, ct);
            return;
        }
        if (m.Text.StartsWith("/close_queue"))
        {
            if (!await CanControl(chatId, m.From.Id, ct)) return;
            await CloseQueue(chatId, ct);
            return;
        }
        if (m.Text.StartsWith("/remove_top"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return;
            if (!_chats.TryGetValue(chatId, out var qt)) return;
            await RemoveAt(chatId, qt, 1, ct);
            return;
        }
        if (m.Text.StartsWith("/remove") || m.Text.StartsWith("/kick"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return;
            var parts = m.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var n) || !_chats.TryGetValue(chatId, out var q))
                return;
            await RemoveAt(chatId, q, n, ct);
            return;
        }
        if (m.Text.StartsWith("/delete_all"))
        {
            if (!await IsAdmin(chatId, m.From.Id, ct)) return;
            await DeleteAllBotMsgs(chatId, ct);
            await SafeDelete(chatId, m.MessageId, ct);
            return;
        }

        if (m.Text.StartsWith("/delete"))
        {
            if (!await CanControl(chatId, m.From.Id, ct)) return;
            // delete bot message from reply, skip live queue message
            if (m.ReplyToMessage is { } r && r.From?.Id == bot.BotId
                && (!_chats.TryGetValue(chatId, out var q) || r.MessageId != q.MsgId))
                await SafeDelete(chatId, r.MessageId, ct);
            await SafeDelete(chatId, m.MessageId, ct);
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
            var headBefore = data is "leave" ? cq.Core.List.FirstOrDefault()?.Id : null;
            string? err = data switch
            {
                "join" => cq.Core.Join(new QueueUser(q.From.Id, name), DateTimeOffset.UtcNow),
                "leave" => LeaveFlow(cq, q.From.Id),
                _ => await SwapPrompt(chatId, q.From.Id, ct),
            };
            if (err is not null) await Alert(err);
            else if (data is "join" or "leave")
            {
                await Render(chatId, ct);
                if (data is "leave") PingIfNewHead(chatId, headBefore, cq);
            }
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
            if (!await CanControl(chatId, q.From.Id, ct)) { await Alert("Только для админов"); return; }
            if (_chats.TryGetValue(chatId, out var cq)) { cq.Deadline += TimeSpan.FromHours(1); Save(); }
            await SafeDelete(chatId, q.Message.MessageId, ct);
            await bot.AnswerCallbackQuery(q.Id, cancellationToken: ct);
            return;
        }
        if (data is "close_now")
        {
            if (!await CanControl(chatId, q.From.Id, ct)) { await Alert("Только для админов"); return; }
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

    private async Task StartQueue(long chatId, string title, CancellationToken ct)
    {
        _closed.TryRemove(chatId, out _); // fresh start drops saved list
        var q = new ChatQueue { Deadline = DateTimeOffset.UtcNow + PoolTtl };
        q.Core.Title = title;
        await PostQueue(chatId, q, ct);
    }

    // Text after command, one line, max 80. Strips @bot mention.
    private static string ParseTitle(string text, string cmd)
    {
        var rest = text.StartsWith(cmd) ? text[cmd.Length..] : "";
        rest = rest.Trim();
        if (rest.StartsWith("@"))
        {
            var sp = rest.IndexOf(' ');
            rest = sp < 0 ? "" : rest[(sp + 1)..].Trim();
        }
        rest = rest.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return rest.Length > 80 ? rest[..80].TrimEnd() : rest;
    }

    // Ping new head after leave/kick. Fire and forget, self-deletes in 15s.
    private void PingIfNewHead(long chatId, long? headBefore, ChatQueue cq)
    {
        var head = cq.Core.List.FirstOrDefault();
        if (head is null || head.Id == headBefore) return;
        var mention = $"<a href=\"tg://user?id={head.Id}\">{Escape(head.Name)}</a>";
        _ = Task.Run(async () =>
        {
            try
            {
                var sent = await bot.SendMessage(chatId, $"{mention}, Ваша очередь!", parseMode: ParseMode.Html);
                Track(chatId, sent.MessageId);
                await Task.Delay(TimeSpan.FromSeconds(15));
                await SafeDelete(chatId, sent.MessageId, CancellationToken.None);
            }
            catch { } // best effort
        });
    }

    private async Task ContinueQueue(long chatId, CancellationToken ct)
    {
        if (_chats.ContainsKey(chatId)) return; // active wins
        if (!_closed.TryRemove(chatId, out var saved) || saved.List.Count == 0) return; // nothing saved
        var q = new ChatQueue { Deadline = DateTimeOffset.UtcNow + PoolTtl };
        q.Core.Title = saved.Title;
        q.Core.Restore(saved.List);
        await PostQueue(chatId, q, ct);
    }

    private async Task PostQueue(long chatId, ChatQueue q, CancellationToken ct)
    {
        _chats[chatId] = q;
        var sent = await bot.SendMessage(chatId, q.Core.Render(),
            replyMarkup: Buttons(), cancellationToken: ct);
        q.MsgId = sent.MessageId;
        Track(chatId, sent.MessageId);
        Save();
        _ = RunTtl(chatId, q.Cts.Token);
    }

    private async Task CloseQueue(long chatId, CancellationToken ct)
    {
        if (!_chats.TryGetValue(chatId, out var q)) return;
        await q.Gate.WaitAsync(ct);
        try
        {
            q.Core.Close();
            if (q.Core.List.Count > 0) _closed[chatId] = (q.Core.Title, [.. q.Core.List]); // keep for /continue_queue
            q.Cts.Cancel();
            await bot.EditMessageText(chatId, q.MsgId, q.Core.Render(), cancellationToken: ct);
            await bot.EditMessageReplyMarkup(chatId, q.MsgId, replyMarkup: null, cancellationToken: ct);
        }
        catch (ApiRequestException) { } // stale message, skip
        finally { q.Gate.Release(); _chats.TryRemove(chatId, out _); }
        Save();
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
        Save();
    }

    private static InlineKeyboardMarkup Buttons() =>
        new InlineKeyboardButton[][]
        {
            [("➕ Встать", "join"), ("➖ Выйти", "leave")],
            [("🔀 Поменяться", "swap")],
        };

    private async Task<bool> IsAdmin(long chatId, long userId, CancellationToken ct)
    {
        if (Env.ChatAdmins(chatId).Contains(userId)) { Console.WriteLine($"admin {userId} via env"); return true; } // .env list
        if (_adminCache.TryGetValue((chatId, userId), out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(5))
            return c.Admin;
        try
        {
            var m = await bot.GetChatMember(chatId, userId, cancellationToken: ct);
            var admin = m.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
            _adminCache[(chatId, userId)] = (admin, DateTimeOffset.UtcNow);
            Console.WriteLine($"admin {userId} via chat = {admin}");
            return admin;
        }
        catch (ApiRequestException) { Console.WriteLine($"admin {userId} deny"); return false; } // unknown, deny
    }

    // Open flag or admin may manage queue.
    private async Task<bool> CanControl(long chatId, long userId, CancellationToken ct)
        => Env.IsOpenControl(chatId) || await IsAdmin(chatId, userId, ct);

    private async Task<string?> SwapPrompt(long chatId, long userId, CancellationToken ct)
    {
        var sent = await bot.SendMessage(chatId, "Ответь на это сообщение номером, с кем меняешься (60с).",
            replyParameters: new ReplyParameters { MessageId = _chats[chatId].MsgId }, cancellationToken: ct);
        Track(chatId, sent.MessageId);
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
        Track(chatId, sent.MessageId);
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
            Track(chatId, warn.MessageId);
            var left = _chats.TryGetValue(chatId, out var q2) ? q2.Deadline - DateTimeOffset.UtcNow : TimeSpan.Zero;
            if (left > TimeSpan.Zero) await Task.Delay(left, ct);
            await SafeDelete(chatId, warn.MessageId, CancellationToken.None);
            await CloseQueue(chatId, CancellationToken.None);
        }
        catch (TaskCanceledException) { } // closed early
    }

    // Escape text for Html parse mode.
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Shared remove-by-number for /kick, /remove, /remove_top.
    private async Task RemoveAt(long chatId, ChatQueue q, int n, CancellationToken ct)
    {
        var headBefore = q.Core.List.FirstOrDefault()?.Id;
        q.Core.Kick(n);
        await Render(chatId, ct);
        PingIfNewHead(chatId, headBefore, q);
    }

    // Remember bot-sent message for /delete_all. Prunes older than 24h.
    private void Track(long chatId, int msgId)
    {
        _botMsgs[(chatId, msgId)] = DateTimeOffset.UtcNow;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        foreach (var k in _botMsgs.Keys)
            if (_botMsgs.TryGetValue(k, out var at) && at < cutoff)
                _botMsgs.TryRemove(k, out _);
    }

    // Delete tracked bot messages from last 24h. Keeps live queue message.
    private async Task DeleteAllBotMsgs(long chatId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        _chats.TryGetValue(chatId, out var live);
        foreach (var k in _botMsgs.Keys)
        {
            if (k.Chat != chatId) continue;
            if (!_botMsgs.TryGetValue(k, out var at) || at < cutoff) { _botMsgs.TryRemove(k, out _); continue; }
            if (live is not null && k.Msg == live.MsgId) continue; // keep live board
            _botMsgs.TryRemove(k, out _);
            await SafeDelete(chatId, k.Msg, ct);
        }
        Save();
    }

    // Snapshot chats to disk. Fail-open, never throws.
    private void Save()
    {
        try
        {
            var snap = new QueueStore.Snapshot(
                _chats.Select(kv => new QueueStore.SavedQueue(kv.Key, kv.Value.MsgId, kv.Value.Deadline,
                    kv.Value.Core.Title, kv.Value.Core.List.Select(u => new QueueStore.SavedUser(u.Id, u.Name)).ToList())).ToList(),
                _closed.Select(kv => new QueueStore.SavedClosed(kv.Key, kv.Value.Title,
                    kv.Value.List.Select(u => new QueueStore.SavedUser(u.Id, u.Name)).ToList())).ToList(),
                _botMsgs.Select(kv => new QueueStore.SavedBotMsg(kv.Key.Chat, kv.Key.Msg, kv.Value)).ToList());
            QueueStore.Save(snap);
        }
        catch { } // disk full or locked, skip
    }

    // Resume chats after restart. Timers restart, no re-render.
    public Task RestoreAsync(CancellationToken ct)
    {
        var snap = QueueStore.Load();
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        foreach (var b in snap.BotMsgs ?? [])
            if (b.At >= cutoff)
                _botMsgs[(b.ChatId, b.MsgId)] = b.At;
        foreach (var c in snap.Closed)
            _closed[c.ChatId] = (c.Title, c.Users.Select(u => new QueueUser(u.Id, u.Name)).ToList());
        foreach (var s in snap.Active)
        {
            var q = new ChatQueue { MsgId = s.MsgId, Deadline = s.Deadline, LastRender = DateTimeOffset.UtcNow };
            q.Core.Title = s.Title;
            q.Core.Restore(s.Users.Select(u => new QueueUser(u.Id, u.Name)));
            _chats[s.ChatId] = q;
            _ = RunTtl(s.ChatId, q.Cts.Token);
        }
        return Task.CompletedTask;
    }

    private async Task SafeDelete(long chatId, int msgId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(chatId, msgId, cancellationToken: ct); }
        catch (ApiRequestException) { } // no rights or gone, skip
    }
}
