// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Globalization;
using Xunit;

namespace Em68030.Tests.AppTests;

/// <summary>
/// Tests for the --lang command line argument parsing and CultureInfo handling.
/// </summary>
public class LanguageOverrideTests
{
    /// <summary>
    /// Simulates the --lang argument parsing logic from App.xaml.cs.
    /// Returns the CultureInfo if successful, or null if the argument is
    /// missing, empty, or contains an invalid language code.
    /// </summary>
    private static CultureInfo? ParseLangArg(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                var lang = arg["--lang=".Length..];
                if (!string.IsNullOrEmpty(lang))
                {
                    try { return new CultureInfo(lang); }
                    catch (CultureNotFoundException) { return null; }
                }
                return null;
            }
        }
        return null;
    }

    [Fact]
    public void ValidLanguage_EnUS_ReturnsCulture()
    {
        var culture = ParseLangArg(["--lang=en-US"]);
        Assert.NotNull(culture);
        Assert.Equal("en-US", culture.Name);
    }

    [Fact]
    public void ValidLanguage_JaJP_ReturnsCulture()
    {
        var culture = ParseLangArg(["--lang=ja-JP"]);
        Assert.NotNull(culture);
        Assert.Equal("ja-JP", culture.Name);
    }

    [Fact]
    public void ValidLanguage_CaseInsensitiveFlag()
    {
        var culture = ParseLangArg(["--Lang=en-US"]);
        Assert.NotNull(culture);
        Assert.Equal("en-US", culture.Name);
    }

    [Fact]
    public void EmptyValue_ReturnsNull()
    {
        var culture = ParseLangArg(["--lang="]);
        Assert.Null(culture);
    }

    [Fact]
    public void NoEqualsSign_ReturnsNull()
    {
        var culture = ParseLangArg(["--lang"]);
        Assert.Null(culture);
    }

    [Fact]
    public void InvalidLanguageCode_ReturnsNull()
    {
        var culture = ParseLangArg(["--lang=xyz-INVALID"]);
        Assert.Null(culture);
    }

    [Fact]
    public void NoLangArg_ReturnsNull()
    {
        var culture = ParseLangArg(["--other=value"]);
        Assert.Null(culture);
    }

    [Fact]
    public void EmptyArgs_ReturnsNull()
    {
        var culture = ParseLangArg([]);
        Assert.Null(culture);
    }

    [Fact]
    public void LangArgAmongOtherArgs_ReturnsCulture()
    {
        var culture = ParseLangArg(["--debug", "--lang=en-US", "--verbose"]);
        Assert.NotNull(culture);
        Assert.Equal("en-US", culture.Name);
    }

    [Fact]
    public void UnsupportedButValidLanguage_ReturnsCulture()
    {
        // fr-FR is a valid CultureInfo but no .resx exists — should still parse
        var culture = ParseLangArg(["--lang=fr-FR"]);
        Assert.NotNull(culture);
        Assert.Equal("fr-FR", culture.Name);
    }
}
