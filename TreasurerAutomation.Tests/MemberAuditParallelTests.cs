using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TreasurerAutomation.Commands;
using TreasurerAutomation.Services;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Concurrency-Pfad des member-audit (10 parallele Anreicherungen, geteilter
    /// Client + Rate-Limiter). Gemocktes HTTP, keine Echtdaten.
    /// </summary>
    public class MemberAuditParallelTests
    {
        private sealed class RoutingStub : HttpMessageHandler
        {
            private int _calls;
            public int Calls => _calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                var url = request.RequestUri?.ToString() ?? "";
                string json = url.Contains("/contact-details/")
                    ? "{\"firstName\":\"Max\",\"familyName\":\"Muster\"}"
                    : url.Contains("/groups")
                        ? "{\"results\":[{\"memberGroup\":\"http://test/v2.0/member-group/9\",\"paymentActive\":true}],\"next\":null}"
                        : "{\"results\":[],\"next\":null}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }

        [Fact]
        public async Task Anreichern_Parallel_LiefertAlleMitgliederKorrekt()
        {
            var stub = new RoutingStub();
            using var client = new EasyVereinClient("TESTTOKEN",
                new HttpClient(stub) { BaseAddress = new System.Uri("http://test/") },
                rateLimitProMinute: 60_000, rateLimitBurst: 1000);
            var gruppenLookup = new Dictionary<string, JsonElement>
            {
                ["9"] = JsonDocument.Parse("{\"short\":\"VB03\",\"name\":\"Regul\u00e4r\"}").RootElement.Clone(),
            };
            var customDefLookup = new Dictionary<string, JsonElement>();
            var members = new List<JsonElement>();
            foreach (var i in Enumerable.Range(1, 20))
            {
                using var doc = JsonDocument.Parse(
                    $"{{\"id\":{i},\"contactDetails\":\"http://test/v2.0/contact-details/{100 + i}\"}}");
                members.Add(doc.RootElement.Clone());
            }

            var ergebnisse = new MemberRecord[members.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, members.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 10 },
                async (i, ct) =>
                {
                    var m = members[i];
                    var ang = await MemberAuditCommand.Anreichern(client, m, gruppenLookup, customDefLookup, ct);
                    ergebnisse[i] = EasyVereinMemberParser.Parse(m, ang.Cd, ang.Kuerzel, ang.Namen, ang.Customs);
                });

            Assert.All(ergebnisse, r => Assert.Contains("VB03", r.GruppenKuerzel));
            Assert.All(ergebnisse, r => Assert.Equal("Max Muster", r.DisplayName));
            Assert.Equal(60, stub.Calls); // 20 Mitglieder × 3 Sub-Requests
        }
    }
}
