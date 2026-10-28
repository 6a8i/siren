using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// One Content-Security-Policy violation as a browser reports it, reduced to the fields worth logging
/// and cleaned so a report cannot forge log lines.
/// </summary>
/// <param name="DocumentUri">The page the policy applied to.</param>
/// <param name="ViolatedDirective">The directive the resource broke.</param>
/// <param name="BlockedUri">The resource that was (or would have been) blocked.</param>
/// <param name="SourceFile">The script that requested it, when the browser knows.</param>
/// <param name="LineNumber">The line in that script, when the browser knows.</param>
/// <param name="Disposition"><c>report</c> under the report-only header, <c>enforce</c> otherwise.</param>
public sealed record CspViolationReport(
    string DocumentUri,
    string ViolatedDirective,
    string BlockedUri,
    string SourceFile,
    int? LineNumber,
    string Disposition)
{
    /// <summary>
    /// The most a report body may be; a browser's report is a few hundred bytes.
    /// </summary>
    public const int MaxBodyBytes = 64 * 1024;

    private const int MaxFieldLength = 512;
    private const string Missing = "-";

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 8
    };

    /// <summary>
    /// Gets the key under which repeats of this violation are counted as one.
    /// </summary>
    public string ThrottleKey => string.Join('|', DocumentUri, ViolatedDirective, BlockedUri, SourceFile);

    /// <summary>
    /// Reads the reports out of a request body. Both wire formats are understood: the <c>report-uri</c>
    /// one (<c>application/csp-report</c>, a single object under <c>csp-report</c>) and the Reporting API
    /// one (<c>application/reports+json</c>, an array of reports with a <c>body</c>). Anything that is not
    /// one of those yields nothing rather than an error, since the sender is a browser nobody can answer.
    /// </summary>
    /// <param name="utf8Json">The request body.</param>
    /// <returns>The reports found, possibly none.</returns>
    public static IReadOnlyList<CspViolationReport> Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var reports = new List<CspViolationReport>(1);
        try
        {
            using var document = JsonDocument.Parse(utf8Json, _documentOptions);
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    if (root.TryGetProperty("csp-report", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                    {
                        reports.Add(FromReportUri(legacy));
                    }
                    else if (IsCspViolation(root, out var body))
                    {
                        reports.Add(FromReportingApi(body));
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var element in root.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Object && IsCspViolation(element, out var body))
                        {
                            reports.Add(FromReportingApi(body));
                        }
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or nested past MaxDepth: nothing to log.
        }

        return reports;
    }

    private static bool IsCspViolation(JsonElement report, out JsonElement body)
    {
        body = default;
        if (report.TryGetProperty("type", out var type)
            && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "csp-violation", StringComparison.Ordinal)))
        {
            return false;
        }

        return report.TryGetProperty("body", out body) && body.ValueKind == JsonValueKind.Object;
    }

    private static CspViolationReport FromReportUri(JsonElement report)
        => new(
            GetString(report, "document-uri"),
            GetString(report, "violated-directive", "effective-directive"),
            GetString(report, "blocked-uri"),
            GetString(report, "source-file"),
            GetInt(report, "line-number"),
            GetString(report, "disposition"));

    private static CspViolationReport FromReportingApi(JsonElement body)
        => new(
            GetString(body, "documentURL"),
            GetString(body, "effectiveDirective", "violatedDirective"),
            GetString(body, "blockedURL"),
            GetString(body, "sourceFile"),
            GetInt(body, "lineNumber"),
            GetString(body, "disposition"));

    private static string GetString(JsonElement element, string name, string? alternative = null)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return Clean(value.GetString());
        }

        if (alternative is not null && element.TryGetProperty(alternative, out value) && value.ValueKind == JsonValueKind.String)
        {
            return Clean(value.GetString());
        }

        return Missing;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    /// <summary>
    /// Bounds a reported value and strips control characters, so that a crafted report can neither
    /// pad the log nor break a line to forge another entry.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Missing;
        }

        if (value.Length > MaxFieldLength)
        {
            value = value[..MaxFieldLength];
        }

        var firstControl = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, firstControl);
        for (var i = firstControl; i < value.Length; i++)
        {
            if (!char.IsControl(value[i]))
            {
                builder.Append(value[i]);
            }
        }

        return builder.Length == 0 ? Missing : builder.ToString();
    }
}
