using System.Net;
using System.Text;
using System.Text.Json;
using TreasurerAutomation.Commands;
using TreasurerAutomation.Services;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Live-Format: Referenzen sind URLs (member.contactDetails, memberGroups, customField),
    /// Endpunkte singular (member-group, custom-field), VB2M = Familie Münsterlandkarte.
    /// </summary>
    public class MemberAuditLiveFormatTests
    {
        [Theory]
        [InlineData("36271917", "36271917")]
        [InlineData("https://easyverein.com/api/v2.0/contact-details/36271917", "36271917")]
        [InlineData("https://easyverein.com/api/v2.0/custom-field/41958459?limit=5", "41958459")]
        [InlineData("https://easyverein.com/api/v2.0/member-group/47854604/", "47854604")]
        [InlineData(null, null)]
        [InlineData("  ", null)]
        public void ExtractPk_LoestUrlsUndIds(string? eingabe, string? erwartet)
        {
            Assert.Equal(erwartet, MemberAuditCommand.ExtractPk(eingabe));
        }

        [Fact]
        public void ExtractPk_AusJsonUrl()
        {
            var el = JsonDocument.Parse("""{"contactDetails": "https://easyverein.com/api/v2.0/contact-details/36271917"}""").RootElement;
            var pk = MemberAuditCommand.ExtractPk(el.GetProperty("contactDetails"));
            Assert.Equal("36271917", pk);
        }

        [Fact]
        public void TechnikFindings_MarkiertNurLadefehler()
        {
            var jsonMitRef = JsonDocument.Parse("""{"id": 1, "contactDetails": "https://easyverein.com/api/v2.0/contact-details/5", "memberGroups": ["https://easyverein.com/api/v2.0/member/1/groups/9"]}""").RootElement.Clone();
            var leer = new MemberRecord { Id = 1, DisplayName = "?" };
            var res = new MemberAuditResult { Mitglied = leer };
            var n = MemberAuditCommand.ErgänzeTechnikFindings(
                new List<(JsonElement, MemberRecord)> { (jsonMitRef, leer) },
                new List<MemberAuditResult> { res });
            Assert.Equal(1, n);
            Assert.Contains(res.Findings, f => f.Code == "API_DETAILS_UNVOLLSTAENDIG");

            // Ohne Referenzen -> kein Technik-Befund (echte Datenlücke)
            var jsonOhne = JsonDocument.Parse("""{"id": 2}""").RootElement.Clone();
            var res2 = new MemberAuditResult { Mitglied = new MemberRecord { Id = 2, DisplayName = "?" } };
            var n2 = MemberAuditCommand.ErgänzeTechnikFindings(
                new List<(JsonElement, MemberRecord)> { (jsonOhne, res2.Mitglied) },
                new List<MemberAuditResult> { res2 });
            Assert.Equal(0, n2);
        }

        private sealed class FolgeHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _q = new();
            public void Einreihen(HttpResponseMessage r) => _q.Enqueue(r);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(_q.Dequeue());
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
        public async Task ListRaw_WiederholtBei429()
        {
            var stub = new FolgeHandler();
            stub.Einreihen(JsonAntwort("rate limited", HttpStatusCode.TooManyRequests));
            stub.Einreihen(JsonAntwort("""{"count":1,"next":null,"results":[{"id":1}]}"""));
            var http = new HttpClient(stub) { BaseAddress = new Uri("http://test/") };
            using var client = new EasyVereinClient("T", http);
            var items = await client.ListRawAsync("member", "limit=1", maxPages: 1);
            Assert.Single(items);
        }
    }
}
