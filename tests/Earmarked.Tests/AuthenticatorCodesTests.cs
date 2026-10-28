using Earmarked.Core.Accounts;

namespace Earmarked.Tests;

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

    [Theory]
    [InlineData("BCDFG-HJKMN")]
    [InlineData("bcdfg-hjkmn")]
    [InlineData("bcdfghjkmn")]
    [InlineData(" BCD FG-HJK MN ")]
    public void Recovery_codes_are_put_back_in_the_stored_form(string typed) =>
        Assert.Equal("BCDFG-HJKMN", AuthenticatorCodes.NormaliseRecoveryCode(typed));

    [Fact]
    public void A_recovery_code_of_the_wrong_length_is_left_to_fail() =>
        Assert.Equal("BCDFGHJK", AuthenticatorCodes.NormaliseRecoveryCode("bcdf-ghjk"));

    [Fact]
    public void The_key_is_grouped_for_typing() =>
        Assert.Equal("abcd efgh ij", AuthenticatorCodes.FormatKey("ABCDEFGHIJ"));

    [Fact]
    public void The_enrolment_link_escapes_the_email_and_names_the_issuer() =>
        Assert.Equal(
            "otpauth://totp/Earmarked:reader%2Btag%40example.com?secret=ABCDEFGH&issuer=Earmarked&algorithm=SHA1&digits=6&period=30",
            AuthenticatorCodes.EnrolmentUri("reader+tag@example.com", "ABCDEFGH").AbsoluteUri);
}
