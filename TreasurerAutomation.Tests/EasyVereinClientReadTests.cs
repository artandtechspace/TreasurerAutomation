using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Tests für die lesenden easyVerein-Zugriffe (ohne Netzwerk, gemocktes HTTP).
    /// </summary>
    public class EasyVereinClientReadTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _antworten = new();
            public readonly List<Uri?> AufgerufeneUrls = new();

            public void JsonEinreihen(string json, HttpStatusCode status = HttpStatusCode.OK)
            {
                _antworten.Enqueue(new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                AufgerufeneUrls.Add(request.RequestUri);
                return Task.FromResult(_antworten.Dequeue());
            }
        }

        private static EasyVereinClient ClientMitStub(StubHandler stub) =>
            new("TESTTOKEN", new HttpClient(stub) { BaseAddress = new Uri("http://test/") });

        private sealed class ZaehlStub : HttpMessageHandler
        {
            private int _calls;
            public int Calls => _calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":1}", Encoding.UTF8, "application/json")
                });
            }
        }

        [Fact]
        public void Konstruktor_LehntLeerenTokenAb()
        {
            Assert.Throws<ArgumentException>(() => new EasyVereinClient(""));
        }

        [Fact]
        public async Task ListRawAsync_LiefertResultsEinerSeite()
        {
            var stub = new StubHandler();
            stub.JsonEinreihen("{\"count\":2,\"next\":null,\"results\":[{\"id\":1,\"amount\":\"10.00\"},{\"id\":2,\"amount\":\"20.00\"}]}");
            var client = ClientMitStub(stub);

            var items = await client.ListRawAsync("booking", "limit=5");

            Assert.Equal(2, items.Count);
            Assert.Equal(1, items[0].GetProperty("id").GetInt32());
            Assert.Equal("20.00", items[1].GetProperty("amount").GetString());
            Assert.Single(stub.AufgerufeneUrls);
            Assert.Contains("v2.0/booking?limit=5", stub.AufgerufeneUrls[0]?.ToString());
        }

        [Fact]
        public async Task ListRawAsync_FolgtNextLinksUeberSeiten()
        {
            var stub = new StubHandler();
            stub.JsonEinreihen("{\"count\":3,\"next\":\"http://test/v2.0/booking?offset=2\",\"results\":[{\"id\":1},{\"id\":2}]}");
            stub.JsonEinreihen("{\"count\":3,\"next\":null,\"results\":[{\"id\":3}]}");
            var client = ClientMitStub(stub);

            var items = await client.ListRawAsync("booking", "limit=2", maxPages: 5);

            Assert.Equal(new[] { 1, 2, 3 }, items.Select(i => i.GetProperty("id").GetInt32()).ToArray());
            Assert.Equal(2, stub.AufgerufeneUrls.Count);
            Assert.Contains("offset=2", stub.AufgerufeneUrls[1]?.ToString());
        }

        [Fact]
        public async Task ListRawAsync_RespektiertMaxPages()
        {
            var stub = new StubHandler();
            stub.JsonEinreihen("{\"count\":9,\"next\":\"http://test/v2.0/booking?offset=1\",\"results\":[{\"id\":1}]}");
            stub.JsonEinreihen("{\"count\":9,\"next\":\"http://test/v2.0/booking?offset=2\",\"results\":[{\"id\":2}]}");
            var client = ClientMitStub(stub);

            var items = await client.ListRawAsync("booking", null, maxPages: 1);

            Assert.Single(items);
            Assert.Single(stub.AufgerufeneUrls);
        }

        [Fact]
        public async Task ListRawAsync_WirftBeiFehlerstatus()
        {
            var stub = new StubHandler();
            stub.JsonEinreihen("{\"detail\":\"kaputt\"}", HttpStatusCode.InternalServerError);
            var client = ClientMitStub(stub);

            var ex = await Assert.ThrowsAsync<Exception>(() => client.ListRawAsync("booking"));
            Assert.Contains("500", ex.Message);
        }

        [Fact]
        public async Task ListRawAsync_LehntLeerenEndpunktAb()
        {
            var stub = new StubHandler();
            var client = ClientMitStub(stub);

            await Assert.ThrowsAsync<ArgumentException>(() => client.ListRawAsync(""));
        }

        [Fact]
        public async Task GetSingleRawAsync_ZaehltRequestsUndLimitHits()
        {
            var stub = new StubHandler();
            stub.JsonEinreihen("{}", HttpStatusCode.TooManyRequests);
            stub.JsonEinreihen("{\"id\":7}");
            var client = ClientMitStub(stub);

            var el = await client.GetSingleRawAsync("member/7");

            Assert.NotNull(el);
            Assert.Equal(7, el.Value.GetProperty("id").GetInt32());
            Assert.Equal(2, client.RequestCount);
            Assert.Equal(1, client.RateLimitHits);
        }

        [Fact]
        public async Task RateLimiter_StautNichtMinutenlang()
        {
            // 30 parallele GETs bei Burst 20: Mit 60s-Refill-Fenster dauern die
            // letzten 10 bis zum Minutenschub (~60s), mit glattem 2s-Fenster ~2s.
            // (Achtung: TokenLimit deckelt zusätzlich – Burst < TokensProFenster
            // würde den Dauer-Durchsatz drosseln, daher Burst 20 hier.)
            var stub = new ZaehlStub();
            using var client = new EasyVereinClient("TESTTOKEN",
                new HttpClient(stub) { BaseAddress = new Uri("http://test/") },
                rateLimitProMinute: 3600, rateLimitBurst: 20);
            var uhr = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, 30),
                new ParallelOptions { MaxDegreeOfParallelism = 10 },
                async (_, ct) => await client.GetSingleRawAsync("member/1", ct));
            uhr.Stop();

            Assert.Equal(30, stub.Calls);
            Assert.Equal(30, client.RequestCount);
            Assert.Equal(0, client.RateLimitHits);
            Assert.True(uhr.Elapsed < TimeSpan.FromSeconds(20),
                $"Dauerte {uhr.Elapsed.TotalSeconds:N0}s – Refill staut minutenlang statt glatt zu füllen.");
        }
    }
}
