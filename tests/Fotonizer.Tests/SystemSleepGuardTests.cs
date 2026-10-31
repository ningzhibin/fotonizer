using Fotonizer.Core.Power;

namespace Fotonizer.Tests;

public class SystemSleepGuardTests
{
    [Fact]
    public void EnableAndDisable_TogglesPowerRequest()
    {
        using var guard = new SystemSleepGuard();

        guard.SetEnabled(true);
        Assert.True(guard.IsActive);

        guard.SetEnabled(false);
        Assert.False(guard.IsActive);
    }
}
