using PodcastBookRecs.Core.Accounts;

namespace PodcastBookRecs.Tests;

public class AuthenticatorCodesTests
{
    [Theory]
    [InlineData("123456")]
    [InlineData("123 456")]
    [InlineData(" 123-456 ")]
    public void Six_digits_with_spacing_are_an_app_code(string code) => Assert.True(AuthenticatorCodes.IsAuthenticatorCode(code));

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("ab3cd-ef6gh")]
    [InlineData("１２３４５６")]
    public void Anything_else_is_tried_as_a_recovery_code(string code) => Assert.False(AuthenticatorCodes.IsAuthenticatorCode(code));

    [Fact]
    public void The_key_is_grouped_for_typing() =>
        Assert.Equal("abcd efgh ij", AuthenticatorCodes.FormatKey("ABCDEFGHIJ"));

    [Fact]
    public void The_enrolment_link_escapes_the_email_and_names_the_issuer() =>
        Assert.Equal(
            "otpauth://totp/PodcastBookRecs:reader%2Btag%40example.com?secret=ABCDEFGH&issuer=PodcastBookRecs&algorithm=SHA1&digits=6&period=30",
            AuthenticatorCodes.EnrolmentUri("reader+tag@example.com", "ABCDEFGH").AbsoluteUri);
}
