using System.Net.Sockets;

namespace AutoPrint.Services;

/// <summary>
/// Прямая печать на сетевой термопринтер по протоколу RAW / JetDirect (TCP 9100).
/// Для принтеров, подключённых по RJ45, драйвер Windows не нужен — ESC/POS байты
/// уходят прямо в сокет.
/// </summary>
public static class RawNetworkPrinter
{
    public const int DefaultPort = 9100;

    /// <summary>Проверка доступности endpoint: TCP-подключение с таймаутом.</summary>
    public static async Task<bool> ProbeAsync(string host, int port, int timeoutMs = 400)
    {
        var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (done != connect)
            {
                // Таймаут: ConnectAsync ещё летит в фоне. Просто выбросить client из-под него
                // (через using) — значит оборвать соединение и получить необработанное исключение
                // на финализаторе (при массовом сканировании подсети — сотни таких в лог).
                // Поэтому наблюдаем и гасим исход отдельно, диспозим только когда он реально завершится.
                _ = connect.ContinueWith(t => { _ = t.Exception; client.Dispose(); },
                    TaskScheduler.Default);
                return false;
            }
            await connect;                          // пробросить исключение, если было
            bool ok = client.Connected;
            client.Dispose();
            return ok;
        }
        catch
        {
            client.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Мягкая проверка "это похоже на ESC/POS-принтер": шлём запрос реального статуса
    /// (DLE EOT 1) и ждём хоть один байт ответа. Порт 9100 открыт не только у чековых
    /// принтеров (JetDirect есть и у обычных офисных МФУ) — так отличаем чужое устройство.
    /// Best-effort: не все модели/прошивки отвечают на статус даже будучи ESC/POS, поэтому
    /// используется только как подсказка в диагностике, а не жёсткий фильтр.
    /// </summary>
    public static async Task<bool> LooksLikeEscPosAsync(string host, int port, int timeoutMs = 400)
    {
        var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
            {
                // Как и в ProbeAsync: не диспозим синхронно, пока connect ещё летит —
                // это оборвёт его и даст необработанное исключение на финализаторе.
                _ = connect.ContinueWith(t => { _ = t.Exception; client.Dispose(); }, TaskScheduler.Default);
                return false;
            }
            await connect;

            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x10, 0x04, 0x01 });   // DLE EOT 1 — запрос статуса принтера

            var buffer = new byte[1];
            var read = stream.ReadAsync(buffer).AsTask();
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)) != read)
            {
                // Тот же приём: чтение может ещё выполняться — гасим исход в фоне, а не рвём сокет сейчас.
                _ = read.ContinueWith(t => { _ = t.Exception; client.Dispose(); }, TaskScheduler.Default);
                return false;
            }
            client.Dispose();
            return await read > 0;
        }
        catch
        {
            client.Dispose();
            return false;
        }
    }

    /// <summary>Отправляет байты на сетевой принтер (открыть сокет → записать → закрыть).</summary>
    public static async Task SendBytesAsync(string host, int port, byte[] bytes, int timeoutMs = 5000)
    {
        var client = new TcpClient();
        var connect = client.ConnectAsync(host, port);
        if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
        {
            // Не диспозим client синхронно (это оборвёт ещё летящий connect и даст
            // необработанное исключение на финализаторе) — гасим исход в фоне.
            _ = connect.ContinueWith(t => { _ = t.Exception; client.Dispose(); }, TaskScheduler.Default);
            throw new TimeoutException($"Не удалось подключиться к {host}:{port} за {timeoutMs} мс.");
        }

        try
        {
            await connect;
            await using var stream = client.GetStream();
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        finally
        {
            client.Dispose();
        }
    }
}
