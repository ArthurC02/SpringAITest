using Backend.Api.Common;
using Backend.Api.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Backend.Api.Tests;

public sealed class ConversationCursorCodecTests
{
    private const string Tenant = "tenant-a";
    private const string User = "user-a";
    private static readonly ConversationItem Item = new(
        42,
        "reply",
        new DateTime(2026, 8, 5, 12, 34, 56, DateTimeKind.Utc));

    [Fact]
    public void Decode_ValidCursor_RestoresPosition()
    {
        var codec = Codec("key-a");

        var position = codec.Decode(codec.Encode(Item, Tenant, User), Tenant, User);

        Assert.Equal(Item.Id, position.Id);
        Assert.Equal(Item.CreatedAt, position.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, position.CreatedAt.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void Decode_BitFlippedSignedField_Returns400(int byteIndex)
    {
        var codec = Codec("key-a");
        var payload = WebEncoders.Base64UrlDecode(codec.Encode(Item, Tenant, User));
        payload[byteIndex] ^= 1;

        AssertInvalid(() => codec.Decode(
            WebEncoders.Base64UrlEncode(payload), Tenant, User));
    }

    [Theory]
    [InlineData("tenant-b", User)]
    [InlineData(Tenant, "user-b")]
    public void Decode_DifferentIdentity_Returns400(string tenant, string user)
    {
        var codec = Codec("key-a");
        var cursor = codec.Encode(Item, Tenant, User);

        AssertInvalid(() => codec.Decode(cursor, tenant, user));
    }

    [Fact]
    public void Decode_WrongKey_Returns400()
    {
        var cursor = Codec("key-a").Encode(Item, Tenant, User);

        AssertInvalid(() => Codec("key-b").Decode(cursor, Tenant, User));
    }

    [Fact]
    public void InternalTokenRotation_InvalidatesOldCursorAndIssuesUsableNewCursor()
    {
        var oldCodec = Codec("old-internal-token");
        var newCodec = Codec("new-internal-token");
        var oldCursor = oldCodec.Encode(Item, Tenant, User);

        AssertInvalid(() => newCodec.Decode(oldCursor, Tenant, User));
        var newCursor = newCodec.Encode(Item, Tenant, User);
        Assert.Equal(Item.Id, newCodec.Decode(newCursor, Tenant, User).Id);
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('=')]
    public void Decode_StandardBase64Character_Returns400(char invalidCharacter)
    {
        var codec = Codec("key-a");
        var cursor = codec.Encode(Item, Tenant, User);
        var malformed = cursor[..10] + invalidCharacter + cursor[11..];

        AssertInvalid(() => codec.Decode(malformed, Tenant, User));
    }

    [Fact]
    public void Decode_NonCanonicalTrailingBits_Returns400()
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var codec = Codec("key-a");
        var cursor = codec.Encode(Item, Tenant, User);
        var canonicalIndex = alphabet.IndexOf(cursor[^1]);
        var nonCanonicalIndex = (canonicalIndex & 0b11_0000) | 1;
        var nonCanonical = cursor[..^1] + alphabet[nonCanonicalIndex];

        Assert.NotEqual(cursor, nonCanonical);
        AssertInvalid(() => codec.Decode(nonCanonical, Tenant, User));
    }

    private static ConversationCursorCodec Codec(string internalToken)
        => new(internalToken);

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<ApiException>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
        Assert.Equal("聊天歷史游標無效", error.Message);
    }
}
