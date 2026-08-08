using System.Reflection;
using System.Text.RegularExpressions;
using Platform.Service.Dtos;

namespace Platform.Service.Tests;

public sealed class UserGroupContractTests
{
    // 契約鏡像釘樁:這三個上限與 group id regex 與 backend 的 AgentAudience
    // (backend/src/Backend.Api/Agents/AgentAudience.cs)逐字相同。任一邊放寬,另一邊會靜默
    // 丟棄整組 group(而非回錯)—— 授權無聲消失。單邊漂移必須讓兩套件其中一支變紅。
    [Fact]
    public void WireContract_PinsBoundsAndGroupIdPatternMirroredInBackend()
    {
        Assert.Equal(128, UserGroupContract.MaxGroupIdLength);
        Assert.Equal(256, UserGroupContract.MaxGroups);
        Assert.Equal(2_048, UserGroupContract.MaxGroupsWireUtf8Bytes);
        var regex = typeof(UserGroupContract)
            .GetMethod(
                "CanonicalGroupIdRegex",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .GetCustomAttribute<GeneratedRegexAttribute>()!;
        Assert.Equal("^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\\z", regex.Pattern);
        // Options 與 Pattern 同等載重:單邊加上 IgnoreCase 會讓同一字串在兩服務判定不同,
        // 只釘 Pattern 的話兩支測試仍全綠,正是上面警告的「靜默丟組」失效模式。
        Assert.Equal(RegexOptions.CultureInvariant, regex.Options);
    }
}
