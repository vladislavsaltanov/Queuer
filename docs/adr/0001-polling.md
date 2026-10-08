# ADR-0001: .NET 10 + Telegram.Bot + polling

Дата: 2026-10-08. Статус: принято.

## Контекст

Нужен один процесс на N чатов, деплой одним контейнером, без входящего HTTPS.

## Решение

- `.NET 10`, пакет `Telegram.Bot` (v22, API по Context7: `OnMessage`/`OnUpdate`/`OnError`, ручной `GetUpdates`-цикл как запасной).
- Polling: апдейты всех чатов из одного потока, роутинг по `chat_id` в `ConcurrentDictionary<chatId, QueueState>`.
- На очередь `SemaphoreSlim(1)` + дебаунс правок 1–2с (защита от 429).
- `callbackData` короткие (`join`, `leave`, `swap`, `yes:{id}`, `no:{id}` — лимит 64 байта), на каждый клик `AnswerCallbackQuery` за 10с.
- Админка через `GetChatMember`, кеш на минуты, проверяется отдельно в каждом чате.
- Персист для MVP не обязателен (in-memory; дальше sqlite по `chat_id`).

## Отклонено

- Webhook — требует публичный HTTPS, избыточен для одного инстанса.
- Несколько очередей на чат, `/next`, статусы текущий/вызван — вне спека.

## Источники

Context7 `/websites/telegrambots_github_io_book`: event-based updates, manual long polling loop, inline keyboards + `AnswerCallbackQuery(alert)`, delete messages.
