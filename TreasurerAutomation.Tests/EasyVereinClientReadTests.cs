using System;
using System.Collections.Generic;
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
    }
}
