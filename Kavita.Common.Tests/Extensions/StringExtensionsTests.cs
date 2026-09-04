using System.Text;
using Kavita.Common.Extensions;

namespace Kavita.Common.Tests.Extensions;

public class StringExtensionsTests
{
    #region ToNormalized

    [Theory]
    [InlineData("Darker than Black", "darkerthanblack")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToNormalized_BasicWhitespaceAndCase_Test(string? input, string expected)
    {
        Assert.Equal(expected, input.ToNormalized());
    }

    [Theory]
    [InlineData("귀멸의 칼날", "귀멸의칼날")]
    [InlineData("귀 멸 의 칼 날", "귀멸의칼날")]
    [InlineData("귀멸의칼날", "귀멸의칼날")]
    public void ToNormalized_KoreanSpacingInsensitive_Test(string input, string expected)
    {
        Assert.Equal(expected, input.ToNormalized());
    }

    [Fact]
    public void ToNormalized_KoreanNfcAndNfdFormsAreEquivalent_Test()
    {
        const string precomposed = "각성"; // NFC (precomposed Hangul syllables)
        var decomposed = precomposed.Normalize(NormalizationForm.FormD); // decomposed jamo

        Assert.NotEqual(precomposed, decomposed); // sanity check the two representations differ at the byte level
        Assert.Equal(precomposed.ToNormalized(), decomposed.ToNormalized());
    }

    #endregion
}
