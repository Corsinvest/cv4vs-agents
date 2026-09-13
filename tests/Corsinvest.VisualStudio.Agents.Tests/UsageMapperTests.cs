/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>get_usage decoding, which the Usage tab and the chat's Account &amp; Usage dialog both render.
/// <para>A reset time passes two readers before anything shows it: the transport's JObject.Parse, which
/// turns an ISO string into a Date token, and the view's own parsing. Lose the offset in between and
/// "Resets in" is off by however far the machine is from UTC — plausible enough on screen that nobody
/// notices.</para>
/// <para>The windows themselves come in two shapes: per-key (<c>five_hour</c>, <c>seven_day</c>, …) and
/// a normalized <c>limits</c> list, the only one that carries a weekly limit scoped to one model. A
/// window the mapper doesn't read is simply absent from both views.</para></summary>
public class UsageMapperTests
{
    private const string Wire = "2026-09-13T20:09:59.845282+00:00";

    private static readonly string PerKeyShape =
        $@"{{ ""rate_limits"": {{ ""five_hour"": {{ ""utilization"": 44, ""resets_at"": ""{Wire}"" }} }} }}";

    // Trimmed from a real CLI 2.1.270 answer. The codenamed and null windows are part of it on purpose:
    // the CLI lists windows it has nothing to say about.
    private const string ListShape = @"{
        ""subscription_type"": ""max"",
        ""rate_limits_available"": true,
        ""rate_limits"": {
            ""five_hour"": { ""utilization"": 44, ""resets_at"": ""2026-09-13T20:09:59.845282+00:00"" },
            ""seven_day"": { ""utilization"": 10, ""resets_at"": ""2026-09-20T04:59:59.845306+00:00"" },
            ""seven_day_opus"": null,
            ""nimbus_quill"": { ""utilization"": 0, ""resets_at"": null },
            ""limits"": [
                { ""kind"": ""session"", ""group"": ""session"", ""percent"": 44, ""severity"": ""normal"",
                  ""resets_at"": ""2026-09-13T20:09:59.845282+00:00"", ""scope"": null, ""is_active"": true },
                { ""kind"": ""weekly_all"", ""group"": ""weekly"", ""percent"": 10, ""severity"": ""normal"",
                  ""resets_at"": ""2026-09-20T04:59:59.845306+00:00"", ""scope"": null, ""is_active"": false },
                { ""kind"": ""weekly_scoped"", ""group"": ""weekly"", ""percent"": 13, ""severity"": ""normal"",
                  ""resets_at"": ""2026-09-20T04:59:59.845585+00:00"",
                  ""scope"": { ""model"": { ""id"": null, ""display_name"": ""Fable"" }, ""surface"": null },
                  ""is_active"": false }
            ]
        }
    }";

    // Dates left as the strings the CLI wrote, so they can be compared verbatim.
    private static JObject ParseAsWritten(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    [Fact]
    public void A_reset_time_keeps_its_offset_through_a_reader_that_parses_dates()
    {
        // JObject.Parse is how the transport reads every line. A Date token's plain string form is local
        // time with no offset, which ResetsIn then takes for UTC.
        var window = Assert.Single(UsageMapper.Build(JObject.Parse(PerKeyShape), null).Windows);

        Assert.Matches(@"(Z|[+-]\d{2}:\d{2})$", window.ResetsAt);
        Assert.Equal(DateTimeOffset.Parse(Wire, CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(window.ResetsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
    }

    [Fact]
    public void A_reset_time_read_as_a_string_is_kept_as_written()
        => Assert.Equal(Wire, Assert.Single(UsageMapper.Build(ParseAsWritten(PerKeyShape), null).Windows).ResetsAt);

    [Fact]
    public void A_window_without_a_reset_time_has_none()
    {
        var raw = JObject.Parse(@"{ ""rate_limits"": { ""five_hour"": { ""utilization"": 44, ""resets_at"": null } } }");

        Assert.Null(Assert.Single(UsageMapper.Build(raw, null).Windows).ResetsAt);
    }

    [Fact]
    public void The_limits_list_wins_over_the_per_key_windows()
    {
        // In the same answer the per-key object has no model-scoped window at all: seven_day_opus is null.
        var windows = UsageMapper.Build(ParseAsWritten(ListShape), null).Windows;

        Assert.Equal(new[] { "Session (5hr)", "Weekly (7 day)", "Weekly Fable" }, windows.Select(w => w.Name));
        Assert.Equal(new[] { 44, 10, 13 }, windows.Select(w => w.Utilization));
        Assert.Equal("2026-09-20T04:59:59.845585+00:00", windows[2].ResetsAt);
    }

    [Fact]
    public void Without_a_limits_list_the_per_key_windows_are_read()
    {
        var raw = ParseAsWritten(@"{ ""rate_limits"": {
            ""seven_day_opus"": { ""utilization"": 30, ""resets_at"": null },
            ""five_hour"": { ""utilization"": 44, ""resets_at"": ""2026-09-13T20:09:59Z"" } } }");

        Assert.Equal(new[] { "Session (5hr)", "Weekly Opus" }, UsageMapper.Build(raw, null).Windows.Select(w => w.Name));
    }

    [Fact]
    public void Codenamed_windows_never_reach_the_views() =>
        // Filled in, but with no name anyone could read.
        Assert.Empty(UsageMapper.Build(
            ParseAsWritten(@"{ ""rate_limits"": { ""nimbus_quill"": { ""utilization"": 5, ""resets_at"": null } } }"), null).Windows);

    [Fact]
    public void The_windows_come_out_session_first_whatever_order_the_CLI_uses()
    {
        var raw = ParseAsWritten(@"{ ""rate_limits"": { ""limits"": [
            { ""kind"": ""weekly_scoped"", ""percent"": 13, ""scope"": { ""model"": { ""display_name"": ""Fable"" } } },
            { ""kind"": ""weekly_all"", ""percent"": 10 },
            { ""kind"": ""session"", ""percent"": 44 } ] } }");

        Assert.Equal(new[] { "Session (5hr)", "Weekly (7 day)", "Weekly Fable" },
            UsageMapper.Build(raw, null).Windows.Select(w => w.Name));
    }

    [Theory]
    [InlineData(140, 100)]
    [InlineData(-5, 0)]
    public void Utilization_is_kept_to_a_percentage(int reported, int expected)
    {
        var raw = ParseAsWritten($@"{{ ""rate_limits"": {{ ""limits"": [ {{ ""kind"": ""session"", ""percent"": {reported} }} ] }} }}");

        Assert.Equal(expected, Assert.Single(UsageMapper.Build(raw, null).Windows).Utilization);
    }

    [Fact]
    public void No_rate_limits_means_no_windows()
    {
        var usage = UsageMapper.Build(ParseAsWritten(@"{ ""subscription_type"": null, ""rate_limits"": null }"), null);

        Assert.False(usage.RateLimitsAvailable);
        Assert.Empty(usage.Windows);
    }
}
