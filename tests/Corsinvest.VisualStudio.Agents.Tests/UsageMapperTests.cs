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
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>get_usage decoding, which the Usage tab and the chat's Account &amp; Usage dialog both render.
/// <para>A reset time passes two readers before anything shows it: the transport's JObject.Parse, which
/// turns an ISO string into a Date token, and the view's own parsing. Lose the offset in between and
/// "Resets in" is off by however far the machine is from UTC — plausible enough on screen that nobody
/// notices.</para></summary>
public class UsageMapperTests
{
    private const string Wire = "2026-09-13T20:09:59.845282+00:00";

    private static readonly string PerKeyShape =
        $@"{{ ""rate_limits"": {{ ""five_hour"": {{ ""utilization"": 44, ""resets_at"": ""{Wire}"" }} }} }}";

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
    {
        using var reader = new JsonTextReader(new StringReader(PerKeyShape)) { DateParseHandling = DateParseHandling.None };

        var window = Assert.Single(UsageMapper.Build(JObject.Load(reader), null).Windows);

        Assert.Equal(Wire, window.ResetsAt);
    }

    [Fact]
    public void A_window_without_a_reset_time_has_none()
    {
        var raw = JObject.Parse(@"{ ""rate_limits"": { ""five_hour"": { ""utilization"": 44, ""resets_at"": null } } }");

        Assert.Null(Assert.Single(UsageMapper.Build(raw, null).Windows).ResetsAt);
    }
}
