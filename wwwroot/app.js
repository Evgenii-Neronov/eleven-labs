const audio = $("#audio")[0];
const generate = $("#generate");
const stop = $("#stop");
const status = $("#status");
let job;

function message(text, error = false) {
    status.text(text).toggleClass("text-danger", error);
}

function endJob() {
    job = undefined;
    generate.prop("disabled", false);
    stop.prop("hidden", true);
}

function addOption(select, value, label) {
    $(select).append($("<option>").val(value).text(label));
}

async function loadOptions() {
    try {
        const [options, voices] = await Promise.all([
            $.getJSON("/api/options"),
            $.getJSON("/api/voices")
        ]);
        $("#voice").empty();
        for (const voice of voices) addOption("#voice", voice.id, voice.name);
        for (const [code, name] of Object.entries(options.languages)) addOption("#language", code, name);
        for (const [format, name] of Object.entries(options.formats)) addOption("#quality", format, name);
        $("#quality").val("mp3_44100_128");
        if (!voices.length) {
            message("В аккаунте ElevenLabs нет доступных голосов.", true);
            return;
        }
        generate.prop("disabled", false);
    } catch (error) {
        message(error.responseJSON?.error ?? "Не удалось загрузить настройки. Обновите страницу.", true);
    }
}

$("#speech-form").on("submit", async event => {
    event.preventDefault();
    if (job) return;

    const request = new AbortController();
    job = request;
    generate.prop("disabled", true);
    stop.prop("hidden", false);
    message("Генерация аудио…");
    audio.hidden = true;

    try {
        const response = await fetch("/api/speech", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            signal: request.signal,
            body: JSON.stringify({
                text: $("#text").val(),
                voice: $("#voice").val(),
                language: $("#language").val(),
                format: $("#quality").val()
            })
        });
        const result = await response.json();
        if (job !== request) return;
        if (!response.ok) throw new Error(result.error ?? "Не удалось начать озвучивание.");
        audio.src = result.url;
        audio.hidden = false;
        try {
            await audio.play();
        } catch (error) {
            if (job !== request) return;
            if (error.name === "NotAllowedError") message("Нажмите ▶ в плеере, чтобы начать воспроизведение.");
            else throw new Error("Не удалось воспроизвести поток ElevenLabs.");
        }
    } catch (error) {
        if (job !== request) return;
        endJob();
        audio.hidden = true;
        message(error.message, true);
    }
});

$(audio).on("playing", () => {
    if (job) message("Воспроизведение…");
}).on("ended", () => {
    if (job) endJob();
    message("Готово");
}).on("error", () => {
    if (!job) return;
    endJob();
    audio.hidden = true;
    message("ElevenLabs не вернул аудио. Проверьте лимит и доступ ключа к озвучиванию.", true);
});

stop.on("click", () => {
    job?.abort();
    endJob();
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    audio.hidden = true;
    message("Остановлено");
});

loadOptions();
