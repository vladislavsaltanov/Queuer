using Telegram.Bot;
using Telegram.Bot.Polling;
using Queuer;

// Load .env first. Real env wins.
Env.Load();
var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Set TELEGRAM_BOT_TOKEN first.");
    return 1;
}

using var cts = new CancellationTokenSource();
var bot = new TelegramBotClient(token, cancellationToken: cts.Token);
var svc = new BotService(bot);

bot.StartReceiving(
    svc.HandleUpdate,
    svc.HandleError,
    new ReceiverOptions { DropPendingUpdates = true },
    cancellationToken: cts.Token);

var me = await bot.GetMe(cancellationToken: cts.Token);
Console.WriteLine($"@{me.Username} up. Ctrl+C to stop.");
await svc.RestoreAsync(cts.Token);
Console.CancelKeyPress += (_, _) => cts.Cancel();
await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => 0);
return 0;
