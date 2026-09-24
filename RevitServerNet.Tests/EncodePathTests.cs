using System;
using System.Linq;
using Xunit;

namespace RevitServerNet.Tests
{
    public class EncodePathTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("|")]
        [InlineData("/")]
        [InlineData("\\")]
        public void RootPath_IsEncodedAsPipe(string path)
        {
            Assert.Equal("|", RevitServerApi.EncodePath(path));
        }

        [Theory]
        [InlineData("|Projects|Demo_1|Model-A.v2~x.rvt")]
        [InlineData("|ABCxyz0189|-_.~")]
        public void UnreservedAsciiNames_AreNotChanged(string path)
        {
            Assert.Equal(path, RevitServerApi.EncodePath(path));
        }

        [Theory]
        [InlineData("Projects\\Demo\\Model.rvt", "|Projects|Demo|Model.rvt")]
        [InlineData("Projects/Demo//Model.rvt", "|Projects|Demo|Model.rvt")]
        [InlineData("\\\\Projects\\/Model.rvt", "|Projects|Model.rvt")]
        [InlineData("||Projects|||Model.rvt", "|Projects|Model.rvt")]
        [InlineData("Projects", "|Projects")]
        [InlineData("|Projects|", "|Projects|")]
        public void Separators_AreNormalised(string path, string expected)
        {
            Assert.Equal(expected, RevitServerApi.EncodePath(path));
        }

        [Theory]
        [InlineData("#Архив", "|%23%D0%90%D1%80%D1%85%D0%B8%D0%B2")]
        [InlineData("Проект #1", "|%D0%9F%D1%80%D0%BE%D0%B5%D0%BA%D1%82%20%231")]
        [InlineData("A&B", "|A%26B")]
        [InlineData("M+1 (копия)", "|M%2B1%20%28%D0%BA%D0%BE%D0%BF%D0%B8%D1%8F%29")]
        [InlineData("100%", "|100%25")]
        [InlineData("lit%23", "|lit%2523")]
        [InlineData("a;b", "|a%3Bb")]
        [InlineData("a?b=c", "|a%3Fb%3Dc")]
        public void SpecialCharactersInName_AreEscaped(string name, string expected)
        {
            Assert.Equal(expected, RevitServerApi.EncodePath(name));
        }

        [Fact]
        public void EveryNameInPath_IsEscapedSeparately()
        {
            Assert.Equal(
                "|%23%D0%90%D1%80%D1%85%D0%B8%D0%B2|A%26B|M%2B1%20%28%D0%BA%D0%BE%D0%BF%D0%B8%D1%8F%29.rvt",
                RevitServerApi.EncodePath("\\#Архив\\A&B/M+1 (копия).rvt"));
        }

        [Theory]
        [InlineData("|#Архив|Проект #1|M+1 (копия).rvt")]
        [InlineData("|A&B|100%|lit%23|a;b.rvt")]
        public void Segments_RoundTripThroughUnescapeDataString(string path)
        {
            var encoded = RevitServerApi.EncodePath(path);

            var decoded = encoded.Split('|').Select(Uri.UnescapeDataString).ToArray();

            Assert.Equal(path.Split('|'), decoded);
        }
    }
}
