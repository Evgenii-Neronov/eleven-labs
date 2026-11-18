# Интеграция с ElevenLabs

Приложение на ASP.NET Core (.NET 10) обслуживает статический интерфейс из `wwwroot`. Код API находится в `Program.cs`, плеер и форма — в `wwwroot/app.js`. Из корня репозитория запустите `dotnet run --urls http://127.0.0.1:5178`.

## Авторизация

Сервер при запуске читает `ELEVENLABS_API_KEY` из конфигурации .NET. Если значение пустое, он ищет `API_KEY` или `ELEVENLABS_API_KEY` в локальном `.env`. Запросы к `https://api.elevenlabs.io/` подписываются заголовком `xi-api-key`. Ключ используется только на сервере, файл `.env` исключён через `.gitignore`. После замены значения ключа перезапустите приложение.

Разрешения ключа — **Voices → Read** для списка и **Text to Speech → Access** для синтеза.

## Список голосов и параметры

- `GET /api/voices` запрашивает ElevenLabs `GET /v2/voices?page_size=100`, проходит страницы по `has_more` и `next_page_token` и отдаёт массив объектов `{id, name}`, отсортированный по имени. Когда API отвечает `401` с `detail.status = missing_permissions`, сервер возвращает стандартные голоса Adam и George.
- `GET /api/options` отдаёт допустимые языки и форматы. Доступны `mp3_22050_32`, `mp3_44100_64` и `mp3_44100_128`. Поле качества при открытии страницы установлено на 128 кбит/с.
- Для готовых пар голос–язык используйте [карту голосов](VOICE-MAP.md). Она служит справочником. Интерфейс загружает каталог напрямую через локальный API.

## Потоковое воспроизведение

1. Браузер отправляет `POST /api/speech` с JSON `{"text":"Hello","voice":"JBFqnCBsd6RMkjVDRZzb","language":"en","format":"mp3_44100_128"}`. Сервер проверяет непустой текст (до 5 000 символов), допустимый вид идентификатора голоса и значения языка и формата.
2. Сервер хранит параметры в памяти до двух минут и возвращает `{"url":"/api/speech/<uuid>"}`. Браузер передаёт одноразовый URL в `<audio>`; исходный текст передаётся в теле POST.
3. При запросе этого URL сервер атомарно забирает параметры и вызывает `POST /v1/text-to-speech/{voice_id}/stream?output_format={format}`. В JSON передаются `text`, `model_id: "eleven_flash_v2_5"` и `language_code`. Для автоопределения `language_code` равен `null`.
4. Сервер получает заголовки через `ResponseHeadersRead`, передаёт MP3 как `audio/mpeg` и сбрасывает каждый прочитанный блок в ответ браузера. При разрыве соединения ASP.NET отменяет чтение через токен запроса. Повторный запрос использованного или просроченного URL вернёт 404. Ошибка ElevenLabs до начала аудио возвращается как 502.

[Список голосов](https://elevenlabs.io/docs/api-reference/voices/search) · [Потоковый синтез](https://elevenlabs.io/docs/api-reference/text-to-speech/stream) · [Коды языков](https://elevenlabs.io/docs/help-center/other/what-languages-do-you-support)
