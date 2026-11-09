using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PBLApp.Core.Trader;

/// <summary>
/// Локальный приём OAuth-колбэка на http://localhost:{port} — сразу на обоих
/// loopback-стеках (127.0.0.1 и [::1]).
///
/// HttpListener здесь не годится: на Linux managed-реализация с префиксом
/// «localhost» слушает только IPv6 (подключение по 127.0.0.1 — connection
/// refused), каждый сокет матчится только со «своим» хостом, а IPv6-литералы
/// в префиксах она не парсит вовсе («Invalid port in prefix»). GGG при этом
/// редиректит браузер на Host: localhost — колбэк по IPv4 не доходил.
/// Поэтому читаем единственный браузерный GET вручную и отвечаем заглушкой.
/// </summary>
internal sealed class LoopbackCallback : IDisposable
{
    private const string ResponseHtml =
        "<html><body><h3>PathBuildLab: авторизация завершена, вкладку можно закрыть.</h3></body></html>";

    private readonly TcpListener? _v4;
    private readonly TcpListener? _v6;

    public int Port { get; }

    /// <summary>Есть ли слушатель на IPv6 (на системах без IPv6 колбэк ловит только v4).</summary>
    public bool HasIpv6 => _v6 is not null;

    private LoopbackCallback(int port, TcpListener? v4, TcpListener? v6)
    {
        Port = port;
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>Поднять слушателей на первом порту из списка, где занялся хотя бы
    /// один стек; предпочтительно оба. null — ни один порт не удалось занять.</summary>
    public static LoopbackCallback? Bind(IReadOnlyList<int> ports)
    {
        foreach (var port in ports)
        {
            var v4 = TryBind(IPAddress.Loopback, port);
            var v6 = TryBind(IPAddress.IPv6Loopback, port);
            if (v4 is not null || v6 is not null)
                return new LoopbackCallback(port, v4, v6);
            foreach (var l in new[] { v4, v6 })
                try { l?.Stop(); } catch { /* не стартовал */ }
        }
        return null;
    }

    private static TcpListener? TryBind(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException) { /* порт занят или стек недоступен */ }
        return null;
    }

    /// <summary>Дождаться первого браузерного GET и ответить заглушкой.
    /// Возвращает query-строку запроса (без '?'), "" без query, null — по отмене
    /// или если соединение оборвалось до читаемого запроса.</summary>
    public async Task<string?> WaitForCallbackAsync(CancellationToken ct)
    {
        // Отдельный CTS на accept: когда один стек выиграл, второй надо снять —
        // иначе брошенный accept останется висеть на listener'е и «съест»
        // следующий колбэк (несколько ждущих accept на одном сокете разбирают
        // подключения по очереди).
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var accepts = new List<Task<TcpClient>>();
        if (_v4 is { } v4) accepts.Add(v4.AcceptTcpClientAsync(acceptCts.Token).AsTask());
        if (_v6 is { } v6) accepts.Add(v6.AcceptTcpClientAsync(acceptCts.Token).AsTask());

        var done = await Task.WhenAny(accepts);
        acceptCts.Cancel();
        // Наблюдаем исключения проигравших (отмена/останов listener'а при Dispose),
        // чтобы не ловить UnobservedTaskException при финализации.
        foreach (var t in accepts)
            if (!ReferenceEquals(t, done))
                _ = t.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        using var client = await done;
        try
        {
            await using var stream = client.GetStream();
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(TimeSpan.FromSeconds(10));

            // Читаем голову запроса до "\r\n\r\n" (GET тела не имеет).
            var buffer = new byte[8192];
            var total = 0;
            string? requestLine = null;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ioCts.Token);
                if (n == 0) break;
                total += n;
                var head = Encoding.ASCII.GetString(buffer, 0, total);
                if (head.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    requestLine = head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
                    break;
                }
            }

            await RespondAsync(stream, ioCts.Token);

            // "GET /path?query HTTP/1.1" → query после '?'
            var parts = requestLine?.Split(' ');
            if (parts is { Length: >= 2 })
            {
                var q = parts[1].IndexOf('?');
                return q >= 0 ? parts[1][(q + 1)..] : "";
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; } // соединение оборвалось — вход не состоялся
    }

    private static async Task RespondAsync(NetworkStream stream, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(ResponseHtml);
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose()
    {
        try { _v4?.Stop(); } catch { /* уже остановлен */ }
        try { _v6?.Stop(); } catch { /* уже остановлен */ }
    }
}
