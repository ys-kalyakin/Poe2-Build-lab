using PBLApp.Core.Trader;
using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace PBLEngine.Tests;

public class PoeOAuthTests
{
    [Fact]
    public void CreatePkcePair_ChallengeIsBase64UrlSha256OfVerifier()
    {
        var (verifier, challenge) = PoeOAuthService.CreatePkcePair();

        using var sha = System.Security.Cryptography.SHA256.Create();
        var expected = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(expected, challenge);
        // base64url без паддинга
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);
        Assert.True(verifier.Length >= 43, $"verifier too short: {verifier.Length}"); // RFC 7636 минимум
    }

    [Fact]
    public void CreatePkcePair_VerifiersAreUnique()
    {
        var (v1, _) = PoeOAuthService.CreatePkcePair();
        var (v2, _) = PoeOAuthService.CreatePkcePair();
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void BuildAuthorizeUrl_HasPobClientIdScopesAndRedirectUri()
    {
        var url = PoeOAuthService.BuildAuthorizeUrl("aabbccdd11223344", "CHLG", 49082);

        Assert.StartsWith("https://www.pathofexile.com/oauth/authorize?", url);
        Assert.Contains("client_id=pob", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=aabbccdd11223344", url);
        Assert.Contains("code_challenge=CHLG", url);
        Assert.Contains("code_challenge_method=S256", url);
        // scope как в PoEAPI.lua:5-10, пробелы как %20
        Assert.Contains("account:profile%20account:leagues%20account:characters%20account:trade", url);
        // redirect_uri обязателен и добавляется как в LaunchServer.lua:32 (без URL-кодирования)
        Assert.Contains("&redirect_uri=http://localhost:49082", url);
    }

    [Fact]
    public void RedirectPorts_AreTheRegisteredPobPorts()
    {
        // GGG-клиент "pob" принимает redirect только на эти порты (LaunchServer.lua:11)
        Assert.Equal([49082, 49083, 49084], PoeOAuthService.RedirectPorts);
    }

    [Fact]
    public async Task CallbackListener_AcceptsLoopbackOnBothIpStacks()
    {
        // GGG редиректит браузер на http://localhost:{port}; подключаться клиент
        // может и по 127.0.0.1, и по [::1]. Бывший HttpListener с префиксом
        // «localhost» на Linux слушал только IPv6 — колбэк по IPv4 отклонялся и
        // вход GGG не работал (репро: редирект в браузере падал с refused).
        using var callback = LoopbackCallback.Bind(PoeOAuthService.RedirectPorts);
        Assert.NotNull(callback);

        // 1) IPv4 — тот стек, который раньше был мёртв.
        var wait = callback!.WaitForCallbackAsync(CancellationToken.None);
        var status = await BrowserGetAsync("127.0.0.1", callback.Port, "code=x&state=y");
        Assert.StartsWith("HTTP/1.1 200", status);
        var query = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(query);
        Assert.Contains("code=x", query);
        Assert.Contains("state=y", query);

        // 2) IPv6 — если стек поднят (на системах без IPv6 ловим только v4).
        if (callback.HasIpv6)
        {
            var wait6 = callback.WaitForCallbackAsync(CancellationToken.None);
            var status6 = await BrowserGetAsync("[::1]", callback.Port, "code=z&state=w");
            Assert.StartsWith("HTTP/1.1 200", status6);
            var query6 = await wait6.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(query6);
            Assert.Contains("code=z", query6);
        }
    }

    /// <summary>Минимальный «браузер»: GET с Host: localhost по сырому TCP.</summary>
    private static async Task<string> BrowserGetAsync(string host, int port, string query)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(host, port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET /?{query} HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var n = await stream.ReadAsync(buffer, cts.Token);
        Assert.True(n > 0, "callback listener closed without a response");
        return Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0];
    }
}
