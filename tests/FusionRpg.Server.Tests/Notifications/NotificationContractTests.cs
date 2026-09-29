using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §2 step 1, Testing 4 - an unregistered category, an undeclared
/// message key, a severity above the ceiling, or an unknown argument kind each throw
/// `NotificationContractException` naming the category and key.</summary>
[Collection("NotificationHub")]
public class NotificationContractTests
{
    readonly NotificationContract _contract;

    public NotificationContractTests()
    {
        NotificationHubFixture.Configure();
        _contract = new NotificationContract(NotificationCatalogHub.Catalog);
    }

    [Fact]
    public void An_unregistered_category_throws_naming_it()
    {
        var draft = NotificationHubFixture.Draft("k1", category: "nothing.registered");
        var ex = Assert.Throws<NotificationContractException>(() => _contract.Validate(draft));
        Assert.Equal("nothing.registered", ex.CategoryId);
    }

    [Fact]
    public void An_undeclared_message_key_throws_naming_it()
    {
        var draft = new NotificationDraft("k2", NotificationHubFixture.TestCategory, NotifySeverity.Important,
            "src", "not.a.declared.key", Array.Empty<NotifyArg>(), null, "w", null);
        var ex = Assert.Throws<NotificationContractException>(() => _contract.Validate(draft));
        Assert.Equal("not.a.declared.key", ex.MessageKey);
    }

    [Fact]
    public void A_severity_above_the_ceiling_throws()
    {
        // TestCategory has no critical promotion, so its ceiling is Important.
        var draft = NotificationHubFixture.Draft("k3", severity: NotifySeverity.Critical);
        Assert.Throws<NotificationContractException>(() => _contract.Validate(draft));
    }

    [Fact]
    public void A_severity_at_the_ceiling_is_accepted()
    {
        var draft = NotificationHubFixture.Draft("k4", severity: NotifySeverity.Important);
        _contract.Validate(draft); // does not throw
    }

    [Fact]
    public void An_unknown_argument_kind_throws()
    {
        var badArg = new NotifyArg("bad", (NotifyArgKind)999, "value");
        var draft = new NotificationDraft("k5", NotificationHubFixture.TestCategory, NotifySeverity.Important,
            "src", NotificationHubFixture.TestMessageKey, new[] { badArg }, null, "w", null);
        Assert.Throws<NotificationContractException>(() => _contract.Validate(draft));
    }

    [Fact]
    public void A_critical_category_accepts_critical_severity()
    {
        var draft = NotificationHubFixture.Draft("k6", severity: NotifySeverity.Critical, category: NotificationHubFixture.CriticalCategory);
        _contract.Validate(draft); // does not throw
    }
}
