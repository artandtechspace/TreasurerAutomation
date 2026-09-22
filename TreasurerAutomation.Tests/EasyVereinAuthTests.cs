using System.Net;
using System.Text;
using System.Text.Json;
using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Login-Normalisierung, get-token Parsing, Session-Roundtrip, Refresh-Header.
    /// Alles ohne Netzwerk (gemocktes HTTP / Temp-Session-Datei).
    /// </summary>
    public class EasyVereinAuthTests : IDisposable
    {
        private readonly string _tmpSession;
        private readonly string? _prevSessionPath;
        private readonly string? _prevToken;

        public EasyVereinAuthTests()
        {
            _tmpSession = Path.Combine(Path.GetTempPath(), $"ev-session-test-{Guid.NewGuid():N}.json");
            _prevSessionPath = Environment.GetEnvironmentVariable("EASYVEREIN_SESSION_PATH");
            _prevToken = Environment.GetEnvironmentVariable("EASYVEREIN_TOKEN");
            Environment.SetEnvironmentVariable("EASYVEREIN_SESSION_PATH", _tmpSession);
            Environment.SetEnvironmentVariable("EASYVEREIN_TOKEN", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("EASYVEREIN_SESSION_PATH", _prevSessionPath);
            Environment.SetEnvironmentVariable("EASYVEREIN_TOKEN", _prevToken);
            try { if (File.Exists(_tmpSession)) File.Delete(_tmpSession); } catch { }
        }

        [Theory]
        [InlineData("luca.schoeneberg@artandtech.space", "ats", "ats_luca.schoeneberg@artandtech.space")]
        [InlineData("ats_luca.schoeneberg@artandtech.space", "ats", "ats_luca.schoeneberg@artandtech.space")]
        [InlineData("  luca@x.de  ", "ats", "ats_luca@x.de")]
        [InlineData("some@example.de", "abc", "abc_some@example.de")]
        public void NormalizeUsername_StelltOrgPrefixVoran(string eingabe, string org, string erwartet)
        {
            Assert.Equal(erwartet, EasyVereinLogin.NormalizeUsername(eingabe, org));
        }

        [Fact]
        public void NormalizeUsername_LehntLeerAb()
        {
            Assert.Throws<ArgumentException>(() => EasyVereinLogin.NormalizeUsername("  "));
        }

        [Fact]
        public void TokenResponse_ParstBeispiel()
        {
            var el = JsonDocument.Parse("""
                {"id": 1320097, "email": "luca.schoeneberg@artandtech.space",
                 "needs2FA": false, "expiresIn": 2592000, "token": "ABC123"}
                """).RootElement;
            var r = EasyVereinTokenResponse.Parse(el);
            Assert.Equal(1320097, r.Id);
            Assert.False(r.Needs2FA);
            Assert.Equal("ABC123", r.Token);
            Assert.Equal(2592000, r.ExpiresIn);
        }

        [Fact]
        public void TokenResponse_OhneToken_Wirft()
        {
            var el = JsonDocument.Parse("""{"id": 1, "needs2FA": true}""").RootElement;
            Assert.Throws<Exception>(() => EasyVereinTokenResponse.Parse(el));
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _antwort;
            public List<HttpRequestMessage> Requests = new();
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> antwort) => _antwort = antwort;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(_antwort(request));
            }
        }

        private static HttpResponseMessage JsonAntwort(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            var r = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return r;
        }

        [Fact]
        public async Task GetToken_SendetUsernamePasswordUnd2FA()
        {
            string? gesendet = null;
            var stub = new StubHandler(req =>
            {
                gesendet = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonAntwort("""{"id":1,"email":"a@b.de","needs2FA":false,"expiresIn":2592000,"token":"T-1"}""");
            });
            var http = new HttpClient(stub) { BaseAddress = new Uri("https://easyverein.com/api/") };
            var resp = await EasyVereinClient.GetTokenAsync("ats_a@b.de", "geheim", "123456", http);
            Assert.Equal("T-1", resp.Token);
            Assert.Contains("ats_a@b.de", gesendet);
            Assert.Contains("123456", gesendet);
        }

        [Fact]
        public async Task GetToken_401_WirftVerstaendlich()
        {
            var stub = new StubHandler(_ => JsonAntwort("""{"detail":"invalid"}""", HttpStatusCode.Unauthorized));
            var http = new HttpClient(stub) { BaseAddress = new Uri("https://easyverein.com/api/") };
            var ex = await Assert.ThrowsAsync<Exception>(() =>
                EasyVereinClient.GetTokenAsync("ats_a@b.de", "falsch", null, http));
            Assert.Contains("401", ex.Message);
        }

        [Fact]
        public async Task RefreshToken_AktualisiertBearerUndFlag()
        {
            var stub = new StubHandler(req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                var r = JsonAntwort("""{"id":1,"email":"a@b.de","needs2FA":false,"expiresIn":2592000,"token":"NEU"}""");
                return r;
            });
            var http = new HttpClient(stub) { BaseAddress = new Uri("https://easyverein.com/api/") };
            using var client = new EasyVereinClient("ALT", http);
            var resp = await client.RefreshTokenAsync();
            Assert.Equal("NEU", resp.Token);
            Assert.Equal("NEU", client.ApiToken);
            Assert.Single(stub.Requests);
            Assert.Contains("refresh-token", stub.Requests[0].RequestUri?.ToString());
        }

        [Fact]
        public async Task ListRaw_BemerktTokenRefreshHeader()
        {
            var stub = new StubHandler(_ =>
            {
                var r = JsonAntwort("""{"count":0,"next":null,"results":[]}""");
                r.Headers.Add("tokenRefreshNeeded", "true");
                return r;
            });
            var http = new HttpClient(stub) { BaseAddress = new Uri("http://test/") };
            using var client = new EasyVereinClient("T", http);
            Assert.False(client.TokenRefreshNeeded);
            await client.ListRawAsync("member", "limit=1", maxPages: 1);
            Assert.True(client.TokenRefreshNeeded);
        }

        [Fact]
        public void Session_SaveLoadRoundtrip()
        {
            var s = new EasyVereinSession("TOK", "a@b.de", 7, DateTime.UtcNow, DateTime.UtcNow.AddDays(30));
            s.Save();
            var geladen = EasyVereinSession.Load();
            Assert.NotNull(geladen);
            Assert.Equal("TOK", geladen!.Token);
            Assert.False(geladen.IsExpired());
        }

        [Fact]
        public void Resolver_BevorzugtExplizitVorEnvVorSession()
        {
            // Session vorhanden
            new EasyVereinSession("SESSION-TOK", "a@b.de", 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)).Save();
            Assert.Equal("SESSION-TOK", EasyVereinTokenResolver.Resolve(null));

            Environment.SetEnvironmentVariable("EASYVEREIN_TOKEN", "ENV-TOK");
            Assert.Equal("ENV-TOK", EasyVereinTokenResolver.Resolve(null));

            Assert.Equal("EXPLIZIT", EasyVereinTokenResolver.Resolve("EXPLIZIT"));
        }

        [Fact]
        public void Resolver_IgnoriertAbgelaufeneSession()
        {
            new EasyVereinSession("ALT", "a@b.de", 1, DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-10)).Save();
            Assert.Equal(string.Empty, EasyVereinTokenResolver.Resolve(null));
        }
    }
}
