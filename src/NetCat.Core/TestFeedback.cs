namespace NetCat.Core;
public static class TestFeedback
{
    public static string Summary(DelayResult result)
    {
        if (result.Success) return $"Доступен · {result.Milliseconds} мс";
        var text = result.Error.ToLowerInvariant();
        if (text.Contains("cancel") || text.Contains("отмен")) return "Проверка отменена";
        if (text.Contains("dns") || text.Contains("nameresolution") || text.Contains("no such host")) return "Не удалось определить адрес сервера";
        if (text.Contains("ssl") || text.Contains("tls") || text.Contains("secureconnection") || text.Contains("certificate")) return "Ошибка защищённого соединения (TLS)";
        if (text.Contains("timeout") || text.Contains("timed out") || text.Contains("таймаут") || text.Contains("секунд") || text.Contains("http-ответа")) return "Сервер не ответил вовремя";
        if (text.Contains("порт") || text.Contains("bind:") || text.Contains("socket address")) return "Не удалось открыть локальный порт";
        if (text.StartsWith("http ")) return "Сайт проверки вернул " + result.Error;
        if (text.Contains("конфигурац") || text.Contains("config")) return "Проверьте параметры профиля";
        if (text.Contains("refused") || text.Contains("отказ")) return "Сервер отклонил соединение";
        return "Не удалось установить соединение";
    }
}
