using System.Text;
using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public class CspViolationReportTests
    {
        [Fact]
        public void Parse_ReportUriFormat_ReadsTheLegacyFields()
        {
            const string Body = """
                {"csp-report":{"document-uri":"https://media.example/web/","referrer":"","violated-directive":"img-src","effective-directive":"img-src","original-policy":"default-src 'self'","disposition":"report","blocked-uri":"http://cdn.example/poster.jpg","line-number":12,"source-file":"https://media.example/web/main.js","status-code":200}}
                """;

            var report = Assert.Single(CspViolationReport.Parse(Encoding.UTF8.GetBytes(Body)));

            Assert.Equal("https://media.example/web/", report.DocumentUri);
            Assert.Equal("img-src", report.ViolatedDirective);
            Assert.Equal("http://cdn.example/poster.jpg", report.BlockedUri);
            Assert.Equal("https://media.example/web/main.js", report.SourceFile);
            Assert.Equal(12, report.LineNumber);
            Assert.Equal("report", report.Disposition);
            Assert.Equal("https://media.example/web/|img-src|http://cdn.example/poster.jpg|https://media.example/web/main.js", report.ThrottleKey);
        }

        [Fact]
        public void Parse_ReportingApiFormat_ReadsEveryCspViolation()
        {
            const string Body = """
                [
                  {"type":"csp-violation","age":10,"url":"https://media.example/web/","body":{"documentURL":"https://media.example/web/","effectiveDirective":"script-src","blockedURL":"eval","disposition":"enforce","sourceFile":"https://media.example/web/plugin.js","lineNumber":"7"}},
                  {"type":"deprecation","age":10,"url":"https://media.example/web/","body":{"id":"x"}},
                  {"type":"csp-violation","body":{"documentURL":"https://media.example/web/","effectiveDirective":"connect-src","blockedURL":"wss://other.example/"}}
                ]
                """;

            var reports = CspViolationReport.Parse(Encoding.UTF8.GetBytes(Body));

            Assert.Equal(2, reports.Count);
            Assert.Equal("script-src", reports[0].ViolatedDirective);
            Assert.Equal("eval", reports[0].BlockedUri);
            Assert.Equal(7, reports[0].LineNumber);
            Assert.Equal("enforce", reports[0].Disposition);
            Assert.Equal("connect-src", reports[1].ViolatedDirective);
            Assert.Equal("-", reports[1].SourceFile);
            Assert.Null(reports[1].LineNumber);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("42")]
        [InlineData("{}")]
        [InlineData("{\"csp-report\":\"text\"}")]
        [InlineData("[1, \"two\", {\"type\":\"csp-violation\"}]")]
        public void Parse_NotAReport_YieldsNothing(string body)
        {
            Assert.Empty(CspViolationReport.Parse(Encoding.UTF8.GetBytes(body)));
        }

        [Fact]
        public void Parse_ControlCharactersAndLongValues_AreCleaned()
        {
            var longUri = "https://media.example/" + new string('a', 2000);
            var body = "{\"csp-report\":{\"document-uri\":\"line\\r\\nforged entry\",\"blocked-uri\":\"" + longUri + "\",\"violated-directive\":\"\\u0000\"}}";

            var report = Assert.Single(CspViolationReport.Parse(Encoding.UTF8.GetBytes(body)));

            Assert.Equal("lineforged entry", report.DocumentUri);
            Assert.Equal(512, report.BlockedUri.Length);
            Assert.Equal("-", report.ViolatedDirective);
        }
    }
}
